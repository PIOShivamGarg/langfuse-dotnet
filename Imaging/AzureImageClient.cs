using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LangfuseDemo.Imaging;

/// <summary>One input image handed to the model as a reference.</summary>
sealed record ImageInput(string FileName, string ContentType, byte[] Bytes);

/// <summary>What the image model gave back, plus the token usage it reported.</summary>
sealed record ImageResult(
    byte[] Png,
    string? RevisedPrompt,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens);

/// <summary>
/// Minimal REST client for the Azure OpenAI image models (gpt-image-*). The .NET
/// Azure.AI.OpenAI package has no image *edit* surface that accepts several reference
/// images, so this talks to the documented multipart endpoint directly.
///
/// We always use /images/edits rather than /images/generations because every call in this
/// pipeline is conditioned on at least one reference photograph.
/// </summary>
sealed class AzureImageClient
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _apiVersion;
    private readonly string _deployment;
    private readonly string _quality;
    private readonly string _size;

    public AzureImageClient(
        string endpoint, string apiKey, string apiVersion,
        string deployment, string quality, string size)
    {
        _endpoint   = endpoint.TrimEnd('/');
        _apiVersion = apiVersion;
        _deployment = deployment;
        _quality    = quality;
        _size       = size;

        // Image generation is slow - a high-quality gpt-image-2 render can take minutes.
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.Add("api-key", apiKey);
    }

    /// <summary>
    /// Sends the prompt plus one or more reference images and returns the generated PNG.
    /// </summary>
    public async Task<ImageResult> EditAsync(
        string prompt, IReadOnlyList<ImageInput> images, CancellationToken ct = default)
    {
        if (images.Count == 0)
            throw new ArgumentException("At least one reference image is required.", nameof(images));

        // Microsoft's REST reference documents the repeated 'image[]' field, while the
        // Python quickstart uses the singular 'image'. Which one a deployment accepts
        // varies by api-version, so try the documented spelling and fall back once.
        var (response, body) = await PostAsync("image[]", prompt, images, ct);

        if (response.StatusCode == HttpStatusCode.BadRequest && images.Count == 1)
        {
            response.Dispose();
            (response, body) = await PostAsync("image", prompt, images, ct);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Azure image API returned {(int)response.StatusCode} {response.ReasonPhrase}.\n{body}");

            return Parse(body);
        }
    }

    private async Task<(HttpResponseMessage Response, string Body)> PostAsync(
        string imageFieldName, string prompt, IReadOnlyList<ImageInput> images, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();

        foreach (var image in images)
        {
            var part = new ByteArrayContent(image.Bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
            form.Add(part, imageFieldName, image.FileName);
        }

        form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent(_deployment), "model");
        form.Add(new StringContent(_size), "size");
        form.Add(new StringContent(_quality), "quality");
        form.Add(new StringContent("1"), "n");

        var response = await _http.PostAsync(BuildUrl(), form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return (response, body);
    }

    /// <summary>
    /// Dated api-versions (2025-04-01, 2025-04-01-preview, ...) use the deployment-scoped
    /// route; the newer rolling 'preview'/'v1' versions use the /openai/v1 route and carry
    /// the deployment in the 'model' form field instead of the path.
    /// </summary>
    private string BuildUrl()
        => _apiVersion is "preview" or "v1"
            ? $"{_endpoint}/openai/v1/images/edits?api-version={_apiVersion}"
            : $"{_endpoint}/openai/deployments/{Uri.EscapeDataString(_deployment)}/images/edits"
              + $"?api-version={Uri.EscapeDataString(_apiVersion)}";

    private ImageResult Parse(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;

        if (!root.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            throw new InvalidOperationException($"Azure image API returned no image data.\n{body}");

        var first = data[0];

        if (!first.TryGetProperty("b64_json", out var b64))
            throw new InvalidOperationException(
                $"Azure image API returned an image without 'b64_json'.\n{body}");

        int? input = null, output = null, total = null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input  = ReadInt(usage, "input_tokens");
            output = ReadInt(usage, "output_tokens");
            total  = ReadInt(usage, "total_tokens");
        }

        return new ImageResult(
            Png: Convert.FromBase64String(b64.GetString()!),
            RevisedPrompt: first.TryGetProperty("revised_prompt", out var rp) ? rp.GetString() : null,
            InputTokens: input,
            OutputTokens: output,
            TotalTokens: total);
    }

    private static int? ReadInt(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;
}
