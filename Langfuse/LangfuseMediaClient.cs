using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LangfuseDemo.Langfuse;

/// <summary>
/// Uploads images to Langfuse's media store and returns the reference token that makes the
/// Langfuse UI render them inline on a trace.
///
/// Why not just inline base64 in the span? Because an OTLP span attribute carrying a
/// multi-megabyte data URI is fragile - it bloats every export and can be rejected outright.
/// The media API is the path the official SDKs take: upload the bytes out of band, then put
/// a short token where the image belongs.
///
/// Three steps, per the Langfuse Media API:
///   1. POST /api/public/media        -> { mediaId, uploadUrl }   (uploadUrl is null if the
///                                       exact bytes were already uploaded before)
///   2. PUT  {uploadUrl}              -> the raw bytes
///   3. PATCH /api/public/media/{id}  -> report the upload result
/// </summary>
sealed class LangfuseMediaClient(string baseUrl, string publicKey, string secretKey)
{
    private readonly HttpClient _api = CreateApiClient(baseUrl, publicKey, secretKey);

    // The presigned PUT must NOT carry the Langfuse Authorization header - the signature in
    // the URL is the credential, and an extra auth header makes some S3 backends reject it.
    private readonly HttpClient _upload = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static HttpClient CreateApiClient(string baseUrl, string publicKey, string secretKey)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}")));
        return http;
    }

    /// <summary>
    /// Uploads one file and returns a token of the form
    /// <c>@@@langfuseMedia:type=image/png|id=...|source=bytes@@@</c>.
    /// Put that token anywhere inside the observation's input/output JSON and Langfuse
    /// swaps it for the rendered image.
    /// </summary>
    /// The <c>field</c> argument says which part of the observation the media belongs to:
    /// <c>input</c>, <c>output</c> or <c>metadata</c>.
    public async Task<string> UploadAsync(
        byte[] bytes,
        string contentType,
        string traceId,
        string? observationId,
        string field,
        CancellationToken ct = default)
    {
        // Langfuse de-duplicates on this hash, and the presigned PUT is validated against it.
        // It must be the base64 of the raw digest, not hex.
        string sha256 = Convert.ToBase64String(SHA256.HashData(bytes));

        var requestBody = JsonSerializer.Serialize(new
        {
            traceId,
            observationId,
            contentType,
            contentLength = bytes.Length,
            sha256Hash = sha256,
            field,
        });

        using var initResponse = await _api.PostAsync(
            "/api/public/media",
            new StringContent(requestBody, Encoding.UTF8, "application/json"),
            ct);

        if (!initResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Langfuse media init failed: {(int)initResponse.StatusCode} "
                + $"{initResponse.ReasonPhrase}\n{await initResponse.Content.ReadAsStringAsync(ct)}");

        var init = JsonDocument.Parse(await initResponse.Content.ReadAsStringAsync(ct)).RootElement;

        string mediaId  = init.GetProperty("mediaId").GetString()!;
        string? uploadUrl = init.TryGetProperty("uploadUrl", out var u) ? u.GetString() : null;

        // Null means Langfuse already has these exact bytes - nothing left to do.
        if (!string.IsNullOrEmpty(uploadUrl))
            await UploadBytesAsync(uploadUrl!, mediaId, bytes, contentType, sha256, ct);

        return $"@@@langfuseMedia:type={contentType}|id={mediaId}|source=bytes@@@";
    }

    private async Task UploadBytesAsync(
        string uploadUrl, string mediaId, byte[] bytes, string contentType, string sha256,
        CancellationToken ct)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        // Google Cloud Storage signs a fixed header set and rejects anything extra; S3,
        // MinIO and Azure Blob all want these. This mirrors what the Langfuse SDKs do.
        if (!uploadUrl.Contains("storage.googleapis.com", StringComparison.Ordinal))
        {
            content.Headers.Add("x-ms-blob-type", "BlockBlob");
            content.Headers.Add("x-amz-checksum-sha256", sha256);
        }

        var stopwatch = Stopwatch.StartNew();
        using var response = await _upload.PutAsync(uploadUrl, content, ct);
        stopwatch.Stop();

        string? error = response.IsSuccessStatusCode
            ? null
            : await response.Content.ReadAsStringAsync(ct);

        // Report the outcome either way: Langfuse shows failed uploads in the UI instead of
        // silently rendering a broken image.
        await PatchAsync(mediaId, (int)response.StatusCode, error, (int)stopwatch.ElapsedMilliseconds, ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Langfuse media upload failed: {(int)response.StatusCode} {response.ReasonPhrase}\n{error}");
    }

    private async Task PatchAsync(
        string mediaId, int status, string? error, int elapsedMs, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            uploadedAt = DateTimeOffset.UtcNow,
            uploadHttpStatus = status,
            uploadHttpError = error,
            uploadTimeMs = elapsedMs,
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/public/media/{Uri.EscapeDataString(mediaId)}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        // Best effort - a failed status report must not sink an otherwise good run.
        try { (await _api.SendAsync(request, ct)).Dispose(); }
        catch (Exception ex) { Console.WriteLine($"  (media status report failed: {ex.Message})"); }
    }
}
