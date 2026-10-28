using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// =====================================================================================
//  1. SETTINGS  (loaded from appsettings.json - no credentials live in this file)
// =====================================================================================
// Three layers, each overriding the one before it:
//   1. appsettings.json        - committed template, secrets blank
//   2. appsettings.Local.json  - your real keys, gitignored, optional
//   3. environment variables   - wins over both; use this in CI/production
//                                e.g. Langfuse__SecretKey, AzureAIFoundry__ApiKey
//                                ('__' is the separator for the ':' in a config path)

IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

// Fails fast with an actionable message instead of a confusing 401 later on.
string Required(string key) =>
    config[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException(
            $"Missing configuration '{key}'. Set it in appsettings.Local.json or as the "
            + $"environment variable '{key.Replace(":", "__")}'. See README.md.");

// --- Langfuse (self-hosted) ----------------------------------------------------------
string langfuseBaseUrl   = Required("Langfuse:BaseUrl");
string langfusePublicKey = Required("Langfuse:PublicKey");
string langfuseSecretKey = Required("Langfuse:SecretKey");

// Where OpenTelemetry pushes the finished traces.
string langfuseOtelEndpoint = langfuseBaseUrl.TrimEnd('/') + "/api/public/otel/v1/traces";

// --- Langfuse Prompt Management --------------------------------------------------------
// The prompt text lives in Langfuse, not in this file. Langfuse keeps every edit as a new
// immutable version number, and the "production" label points at the version this app
// should run. Changing the wording means editing it in the Langfuse UI - no redeploy.
string promptName  = Required("Langfuse:PromptName");
string promptLabel = Required("Langfuse:PromptLabel");

// Leave true to push the seed template below into Langfuse the first time you run against
// an empty instance (creates version 1). Afterwards it is a no-op; manage the prompt in the
// Langfuse UI, where each save becomes version 2, 3, ...
bool seedPromptIfMissing = bool.TryParse(config["Langfuse:SeedPromptIfMissing"], out var seed) && seed;

// --- Azure AI Foundry -------------------------------------------------------------------
string foundryEndpoint = Required("AzureAIFoundry:Endpoint");
string foundryKey      = Required("AzureAIFoundry:ApiKey");
string deploymentName  = Required("AzureAIFoundry:DeploymentName");

// The ActivitySource name is the wire between the AI client and OpenTelemetry: the AI
// client *emits* spans under this name, the tracer *subscribes* to this name.
const string aiSourceName = "LangfuseDemo.AI";


// =====================================================================================
//  2. PROMPT: FETCHED FROM LANGFUSE  (Langfuse  ->  app)
// =====================================================================================
// Plain HTTPS against the Langfuse Public API - nothing to do with OpenTelemetry.
// Prompt management is the inbound path (Langfuse -> app); tracing in step 3 is the
// outbound path (app -> Langfuse).

var langfusePrompts = new LangfusePromptClient(langfuseBaseUrl, langfusePublicKey, langfuseSecretKey);

if (seedPromptIfMissing)
{
    // One-time bootstrap so the demo works on a blank Langfuse instance. If a version
    // already carries the "production" label, nothing is written and no version is bumped.
    await langfusePrompts.SeedIfMissingAsync(
        name: promptName,
        label: promptLabel,
        template: "You are a patient teacher.\n"
                + "Explain {{topic}} to a {{audience}} in exactly one sentence.",
        config: new { temperature = 0.3, max_tokens = 200 },
        commitMessage: "Seeded from LangfuseDemo");
}

// Pull the live prompt. prompt.Version is the Langfuse version number that gets stamped
// onto the trace in step 3, so every generation is attributable to an exact version.
LangfusePrompt prompt = await langfusePrompts.GetAsync(promptName, promptLabel);

Console.WriteLine($"Using Langfuse prompt '{prompt.Name}' v{prompt.Version} (label: {promptLabel})");

// Fill the {{placeholders}} in the template Langfuse returned.
string userMessage = prompt.Compile(
    ("topic", "OpenTelemetry"),
    ("audience", "junior developer"));


// =====================================================================================
//  3. OPENTELEMETRY PIPELINE  (collect spans  ->  ship them to Langfuse)
// =====================================================================================
// Langfuse speaks OTLP over HTTP and authenticates with HTTP Basic using the same
// public/secret key pair as the REST API above.

var otelAuthHeader = Convert.ToBase64String(
    Encoding.UTF8.GetBytes($"{langfusePublicKey}:{langfuseSecretKey}"));

using var tracerProvider = Sdk.CreateTracerProviderBuilder()

    // Resource attributes -> shown in Langfuse as the service that produced the trace.
    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("langfuse-demo"))

    // ---- RECEIVING THE AZURE AI FOUNDRY DATA ------------------------------------------
    // Subscribe to the ActivitySource that the Microsoft.Extensions.AI wrapper (step 4)
    // writes to. Each Foundry call lands here as one GenAI span carrying the model name,
    // the request/response messages and the token usage Foundry reported.
    .AddSource(aiSourceName)

    // ---- LINKING THE LANGFUSE PROMPT VERSION TO THE SPAN -------------------------------
    // The AI client builds the span for us and exposes no enrichment hook, so we edit the
    // span in flight with a processor. Registered BEFORE the exporter so the tags are
    // already on the span when the batching exporter picks it up.
    .AddProcessor(new LangfusePromptLinkProcessor(aiSourceName, prompt.Name, prompt.Version))

    // ---- SENDING TO LANGFUSE ------------------------------------------------------------
    .AddOtlpExporter(options =>
    {
        options.Endpoint = new Uri(langfuseOtelEndpoint);
        options.Protocol = OtlpExportProtocol.HttpProtobuf; // Langfuse accepts HTTP, not gRPC
        options.Headers  = $"Authorization=Basic {otelAuthHeader}";
    })
    .Build();


// =====================================================================================
//  4. AI CLIENT  (talks to Azure AI Foundry, emits OpenTelemetry spans)
// =====================================================================================

IChatClient chatClient =
    // ---- SOURCE OF THE DATA: AZURE AI FOUNDRY ------------------------------------------
    // The model deployment on your Foundry resource. Everything Langfuse later displays -
    // model name, latency, prompt/completion text, token counts, cost - originates in the
    // HTTP response this client receives from Foundry.
    new AzureOpenAIClient(new Uri(foundryEndpoint), new AzureKeyCredential(foundryKey))
        .GetChatClient(deploymentName)
        .AsIChatClient()

        // ---- CONNECTING FOUNDRY TO OPENTELEMETRY ---------------------------------------
        // UseOpenTelemetry wraps the Foundry client in a decorator that opens an Activity
        // (span) around every call and fills it with the GenAI semantic-convention
        // attributes. sourceName must match the AddSource(...) in step 3, otherwise the
        // spans are never listened to and silently dropped.
        .AsBuilder()
        .UseOpenTelemetry(
            sourceName: aiSourceName,
            configure: c => c.EnableSensitiveData = true) // include prompt + answer text
        .Build();


// =====================================================================================
//  5. RUN THE CALL
// =====================================================================================
// Foundry returns the completion -> the decorator from step 4 records it as a span ->
// the processor from step 3 stamps the Langfuse prompt name + version on that span ->
// the OTLP exporter ships it to Langfuse.

Console.WriteLine("Asking the AI...");

var response = await chatClient.GetResponseAsync(
    userMessage,
    new ChatOptions
    {
        // Model settings come from the prompt's config in Langfuse, so tuning temperature
        // is a tracked prompt version change rather than a code change.
        Temperature     = prompt.Temperature,
        MaxOutputTokens = prompt.MaxTokens,
    });

Console.WriteLine();
Console.WriteLine("AI says: " + response.Text);
Console.WriteLine();
Console.WriteLine($"Done. Open {langfuseBaseUrl} -> Tracing; the generation links back to "
                + $"prompt '{prompt.Name}' v{prompt.Version}.");

// Disposing the TracerProvider flushes anything still buffered to Langfuse.


// =====================================================================================
//  HELPERS
// =====================================================================================

/// <summary>
/// Copies the Langfuse prompt name and version onto every GenAI span produced by the AI
/// client. Langfuse reads these two attributes to link the generation back to the exact
/// prompt version, which is what powers the per-version Metrics tab on the prompt.
/// </summary>
sealed class LangfusePromptLinkProcessor(string aiSourceName, string promptName, int promptVersion)
    : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        // Only spans emitted by the AI client are generations; leave everything else alone.
        if (activity.Source.Name != aiSourceName)
            return;

        // Langfuse OpenTelemetry property mapping -> observation.prompt
        activity.SetTag("langfuse.observation.prompt.name", promptName);
        activity.SetTag("langfuse.observation.prompt.version", promptVersion);
    }
}

/// <summary>A single immutable prompt version as stored in Langfuse.</summary>
sealed record LangfusePrompt(
    string Name,
    int Version,
    string Template,
    float? Temperature,
    int? MaxTokens)
{
    /// <summary>Substitutes the {{placeholders}} in the template Langfuse returned.</summary>
    public string Compile(params (string Key, string Value)[] variables)
    {
        var text = Template;
        foreach (var (key, value) in variables)
            text = text.Replace("{{" + key + "}}", value)
                       .Replace("{{ " + key + " }}", value);
        return text;
    }
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
               + "under Prompts in the Langfuse UI, or set seedPromptIfMissing = true.");

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
        res.EnsureSuccessStatusCode();

        Console.WriteLine($"Created prompt '{name}' in Langfuse.");
        return Parse(JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement);
    }

    private static LangfusePrompt Parse(JsonElement root)
    {
        float? temperature = null;
        int? maxTokens = null;

        if (root.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object)
        {
            if (config.TryGetProperty("temperature", out var t) && t.ValueKind == JsonValueKind.Number)
                temperature = t.GetSingle();
            if (config.TryGetProperty("max_tokens", out var m) && m.ValueKind == JsonValueKind.Number)
                maxTokens = m.GetInt32();
        }

        return new LangfusePrompt(
            Name: root.GetProperty("name").GetString()!,
            Version: root.GetProperty("version").GetInt32(),
            Template: root.GetProperty("prompt").GetString()!,
            Temperature: temperature,
            MaxTokens: maxTokens);
    }
}
