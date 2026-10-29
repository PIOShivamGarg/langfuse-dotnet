using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LangfuseDemo.Langfuse;

/// <summary>A single immutable prompt version as stored in Langfuse.</summary>
sealed record LangfusePrompt(string Name, int Version, string Template, JsonElement? Config)
{
    /// <summary>
    /// Reads a string out of the prompt's config object. Lets render settings such as
    /// image size or quality live beside the prompt text in Langfuse, so changing them is a
    /// tracked prompt version rather than a code or config change.
    /// </summary>
    public string? ConfigString(string key)
        => Config is { ValueKind: JsonValueKind.Object } c
           && c.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

/// <summary>
/// Minimal client for the Langfuse Prompt Management REST API (there is no official .NET
/// SDK). Authenticates with the same key pair the OTLP exporter uses.
/// </summary>
sealed class LangfusePromptClient
{
    private readonly HttpClient _http;

    public LangfusePromptClient(string baseUrl, string publicKey, string secretKey)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}")));
    }

    /// <summary>Gets the version of <paramref name="name"/> that carries <paramref name="label"/>.</summary>
    public async Task<LangfusePrompt> GetAsync(string name, string label)
        => await TryGetAsync(name, label)
           ?? throw new InvalidOperationException(
               $"Prompt '{name}' with label '{label}' was not found in Langfuse. Create it "
               + "under Prompts in the Langfuse UI, or set Langfuse:SeedPromptIfMissing to true.");

    public async Task<LangfusePrompt?> TryGetAsync(string name, string label)
    {
        using var res = await _http.GetAsync(
            $"/api/public/v2/prompts/{Uri.EscapeDataString(name)}?label={Uri.EscapeDataString(label)}");

        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        res.EnsureSuccessStatusCode();
        return Parse(JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement);
    }

    /// <summary>
    /// Creates version 1 only when no version carries the label yet. Posting again would
    /// create a *new* version in Langfuse, so this deliberately no-ops once the prompt
    /// exists - the Langfuse UI stays the source of truth.
    /// </summary>
    public async Task<LangfusePrompt> SeedIfMissingAsync(
        string name, string label, string template, object config, string commitMessage)
    {
        var existing = await TryGetAsync(name, label);
        if (existing is not null)
            return existing;

        var body = JsonSerializer.Serialize(new
        {
            name,
            type = "text",
            prompt = template,
            labels = new[] { label },
            config,
            commitMessage,
        });

        using var res = await _http.PostAsync(
            "/api/public/v2/prompts",
            new StringContent(body, Encoding.UTF8, "application/json"));

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Could not create prompt '{name}' in Langfuse: {(int)res.StatusCode} "
                + $"{res.ReasonPhrase}\n{await res.Content.ReadAsStringAsync()}");

        Console.WriteLine($"  Created prompt '{name}' in Langfuse (version 1).");
        return Parse(JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement);
    }

    private static LangfusePrompt Parse(JsonElement root)
        => new(
            Name: root.GetProperty("name").GetString()!,
            Version: root.GetProperty("version").GetInt32(),
            Template: root.GetProperty("prompt").GetString()!,
            Config: root.TryGetProperty("config", out var config) ? config.Clone() : null);
}
