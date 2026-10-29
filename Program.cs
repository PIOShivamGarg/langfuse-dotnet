using System.Diagnostics;
using System.Text;
using LangfuseDemo.Imaging;
using LangfuseDemo.Langfuse;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// =====================================================================================
//  WHAT THIS DOES
// =====================================================================================
// For each garment product photo:
//   1. front shot - product photo        + front prompt -> photorealistic model, front view
//   2. back shot  - product photo + (1)  + back prompt  -> the same model, seen from behind
//
// Both prompts live in Langfuse Prompt Management, and every call lands in Langfuse as a
// generation whose input and output carry the actual images, not just text.
//
// Usage:  dotnet run                 -> the default garment
//         dotnet run -- AR101A       -> one named garment
//         dotnet run -- --all        -> every garment in Garments:Images


// =====================================================================================
//  1. SETTINGS
// =====================================================================================
// Two layers, the second overriding the first:
//   1. appsettings.Local.json  - every setting lives here; gitignored, never committed
//   2. environment variables   - wins over the file; use this in CI/production
//                                e.g. Langfuse__SecretKey, AzureOpenAIImage__ApiKey
//
// There is deliberately no committed appsettings.json. The file is optional so an
// environment-variable-only deployment still runs; anything genuinely missing is reported
// by name at startup, one key at a time. README.md lists the full key set.

IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

string Required(string key) =>
    config[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException(
            $"Missing configuration '{key}'. Set it in appsettings.Local.json (create it "
            + $"from the key list in README.md) or as the environment variable "
            + $"'{key.Replace(":", "__")}'.");

// The image settings also accept the flat AZURE_* environment variable names, so an
// existing .env-style setup works without being rewritten into the nested shape.
//
// Precedence, highest first - an environment variable in EITHER spelling beats the file,
// which is the same rule the rest of the config follows. Reading the environment directly
// is what keeps that true: config[...] cannot tell a file value from an env one, so
// checking the nested path first would silently let the file win over AZURE_OPENAI_*.
string ImageSetting(string key, string flatEnvName, string? fallback = null) =>
    Environment.GetEnvironmentVariable($"AzureOpenAIImage__{key}") is { Length: > 0 } nestedEnv ? nestedEnv
    : Environment.GetEnvironmentVariable(flatEnvName) is { Length: > 0 } flatEnv ? flatEnv
    : config[$"AzureOpenAIImage:{key}"] is { Length: > 0 } fromFile ? fromFile
    : fallback
      ?? throw new InvalidOperationException(
          $"Missing configuration 'AzureOpenAIImage:{key}'. Set it in appsettings.Local.json, "
          + $"or as the environment variable '{flatEnvName}'. See README.md.");

// --- Langfuse (self-hosted) ----------------------------------------------------------
string langfuseBaseUrl   = Required("Langfuse:BaseUrl");
string langfusePublicKey = Required("Langfuse:PublicKey");
string langfuseSecretKey = Required("Langfuse:SecretKey");
string langfuseOtelEndpoint = langfuseBaseUrl.TrimEnd('/') + "/api/public/otel/v1/traces";

// --- Langfuse Prompt Management --------------------------------------------------------
string frontPromptName = Required("Langfuse:FrontPromptName");
string backPromptName  = Required("Langfuse:BackPromptName");
string promptLabel     = Required("Langfuse:PromptLabel");
bool seedPromptIfMissing = bool.TryParse(config["Langfuse:SeedPromptIfMissing"], out var seed) && seed;

// --- Azure OpenAI image resource -------------------------------------------------------
// A separate resource from the chat deployment: the gpt-image-* models have their own
// endpoint and key, which is why these do not reuse the AzureAIFoundry settings.
string imageEndpoint   = ImageSetting("Endpoint",   "AZURE_OPENAI_ENDPOINT");
string imageApiKey     = ImageSetting("ApiKey",     "AZURE_OPENAI_API_KEY");
string imageApiVersion = ImageSetting("ApiVersion", "AZURE_OPENAI_API_VERSION");
string imageDeployment = ImageSetting("Deployment", "AZURE_IMAGE_DEPLOYMENT");
string imageQuality    = ImageSetting("Quality",    "AZURE_IMAGE_QUALITY", "high");
string imageSize       = ImageSetting("Size",       "AZURE_IMAGE_SIZE",    "1024x1536");

// --- Garments ---------------------------------------------------------------------------
string[] allGarments = config.GetSection("Garments:Images").Get<string[]>() is { Length: > 0 } list
    ? list
    : throw new InvalidOperationException(
        "Set Garments:Images in appsettings.Local.json - the list of product photo names "
        + "(without the .png extension). See README.md.");

string outputDirectory = config["Garments:OutputDirectory"] is { Length: > 0 } dir ? dir : "Output";

// The product photos and prompt files live in the project folder, not in bin/.
string projectDirectory = config["Garments:SourceDirectory"] is { Length: > 0 } src
    ? src
    : Directory.GetCurrentDirectory();

string[] garments = args.Length == 0       ? [allGarments[0]]
                  : args.Contains("--all") ? allGarments
                  : args.Where(a => !a.StartsWith("--")).ToArray();

if (garments.Length == 0) garments = [allGarments[0]];

// The ActivitySource name is the wire between this app and OpenTelemetry: we emit spans
// under this name, the tracer provider below subscribes to it.
const string sourceName = "LangfuseDemo.ImagePipeline";
using var activitySource = new ActivitySource(sourceName);


// =====================================================================================
//  2. PROMPTS: PUSHED INTO, THEN FETCHED FROM, LANGFUSE
// =====================================================================================
// The .md files are only a seed. Once a prompt exists in Langfuse the UI is the source of
// truth, and editing it there creates version 2, 3, ... with no redeploy here.

var prompts = new LangfusePromptClient(langfuseBaseUrl, langfusePublicKey, langfuseSecretKey);

Console.WriteLine("Prompts");

if (seedPromptIfMissing)
{
    await prompts.SeedIfMissingAsync(
        name: frontPromptName,
        label: promptLabel,
        template: await ReadPromptFileAsync("front_view_shot_prompt.md"),
        config: new { size = imageSize, quality = imageQuality, shot = "front" },
        commitMessage: "Seeded from front_view_shot_prompt.md");

    await prompts.SeedIfMissingAsync(
        name: backPromptName,
        label: promptLabel,
        template: await ReadPromptFileAsync("back_view_shot_prompt.md"),
        config: new { size = imageSize, quality = imageQuality, shot = "back" },
        commitMessage: "Seeded from back_view_shot_prompt.md");
}

LangfusePrompt frontPrompt = await prompts.GetAsync(frontPromptName, promptLabel);
LangfusePrompt backPrompt  = await prompts.GetAsync(backPromptName, promptLabel);

Console.WriteLine($"  {frontPrompt.Name} v{frontPrompt.Version} (label: {promptLabel})");
Console.WriteLine($"  {backPrompt.Name} v{backPrompt.Version} (label: {promptLabel})");

// Render settings may be overridden per prompt version in Langfuse, so changing the output
// size is a tracked prompt change rather than a config edit.
string effectiveSize    = frontPrompt.ConfigString("size")    ?? imageSize;
string effectiveQuality = frontPrompt.ConfigString("quality") ?? imageQuality;


// =====================================================================================
//  3. OPENTELEMETRY PIPELINE  (collect spans -> ship them to Langfuse)
// =====================================================================================

var otelAuthHeader = Convert.ToBase64String(
    Encoding.UTF8.GetBytes($"{langfusePublicKey}:{langfuseSecretKey}"));

using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("langfuse-image-pipeline"))
    .AddSource(sourceName)
    .AddOtlpExporter(options =>
    {
        options.Endpoint = new Uri(langfuseOtelEndpoint);
        options.Protocol = OtlpExportProtocol.HttpProtobuf; // Langfuse accepts HTTP, not gRPC
        options.Headers  = $"Authorization=Basic {otelAuthHeader}";
    })
    .Build();


// =====================================================================================
//  4. CLIENTS
// =====================================================================================

var images = new AzureImageClient(
    imageEndpoint, imageApiKey, imageApiVersion, imageDeployment, effectiveQuality, effectiveSize);

var media = new LangfuseMediaClient(langfuseBaseUrl, langfusePublicKey, langfuseSecretKey);

Directory.CreateDirectory(outputDirectory);


// =====================================================================================
//  5. RUN THE SHOOT
// =====================================================================================

Console.WriteLine();
Console.WriteLine($"Model {imageDeployment}  |  {effectiveSize}  |  quality {effectiveQuality}");
Console.WriteLine($"Garments: {string.Join(", ", garments)}");
Console.WriteLine();

// One session groups every garment from this run together in the Langfuse UI.
string sessionId = $"shoot-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
int failures = 0;

foreach (var garment in garments)
{
    try
    {
        await ShootAsync(garment);
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"  FAILED: {ex.Message}");
        Console.WriteLine();
    }
}

Console.WriteLine("Flushing traces to Langfuse...");
tracerProvider.ForceFlush(30_000);

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"Done. Open {langfuseBaseUrl} -> Tracing -> session '{sessionId}'."
    : $"Finished with {failures} failed garment(s). Open {langfuseBaseUrl} -> Tracing -> session '{sessionId}'.");

return failures == 0 ? 0 : 1;


// =====================================================================================
//  THE PIPELINE
// =====================================================================================

async Task ShootAsync(string garment)
{
    string sourcePath = ResolveGarmentPath(garment);
    byte[] productPhoto = await File.ReadAllBytesAsync(sourcePath);
    string productFileName = Path.GetFileName(sourcePath);

    Console.WriteLine($"{garment}  ({productPhoto.Length / 1024} KB)");

    // The root span is the trace. Both generations hang off it, so Langfuse shows the front
    // and back shot as two steps of one shoot rather than two unrelated traces.
    using var shoot = activitySource.StartActivity("garment-shoot", ActivityKind.Internal)
        ?? throw new InvalidOperationException(
            "No listener for the ActivitySource - tracing is misconfigured.");

    shoot.WithTraceContext($"shoot: {garment}", sessionId, "image-generation", "garment-shoot")
         .WithMetadata("garment", garment)
         .WithMetadata("source_image", productFileName);

    string traceId = shoot.TraceId.ToHexString();

    // ---- FRONT -------------------------------------------------------------------------
    // Reference: the product photo alone.
    var front = await GenerateAsync(
        spanName: "front-shot",
        prompt: frontPrompt,
        traceId: traceId,
        references: [new ImageInput(productFileName, "image/png", productPhoto)],
        garment: garment);

    string frontPath = Path.Combine(outputDirectory, $"{garment}_front.png");
    await File.WriteAllBytesAsync(frontPath, front.Png);
    Console.WriteLine($"  front -> {frontPath}");

    // ---- BACK --------------------------------------------------------------------------
    // Reference order matters: the back prompt calls the FIRST image the product photograph
    // and the SECOND the approved front frame, so the back shot keeps the same model.
    var back = await GenerateAsync(
        spanName: "back-shot",
        prompt: backPrompt,
        traceId: traceId,
        references:
        [
            new ImageInput(productFileName, "image/png", productPhoto),
            new ImageInput($"{garment}_front.png", "image/png", front.Png),
        ],
        garment: garment);

    string backPath = Path.Combine(outputDirectory, $"{garment}_back.png");
    await File.WriteAllBytesAsync(backPath, back.Png);
    Console.WriteLine($"  back  -> {backPath}");
    Console.WriteLine();
}

// Runs one image call inside its own Langfuse generation span, with the reference images
// attached to the input and the generated image attached to the output.
async Task<ImageResult> GenerateAsync(
    string spanName,
    LangfusePrompt prompt,
    string traceId,
    IReadOnlyList<ImageInput> references,
    string garment)
{
    using var span = activitySource.StartActivity(spanName, ActivityKind.Client)
        ?? throw new InvalidOperationException("No listener for the ActivitySource.");

    span.AsGeneration(imageDeployment)
        .WithPrompt(prompt)
        .WithTraceContext($"shoot: {garment}", sessionId, "image-generation", "garment-shoot")
        .WithMetadata("garment", garment)
        .WithMetadata("shot", spanName)
        .WithMetadata("size", effectiveSize)
        .WithMetadata("quality", effectiveQuality)
        .WithMetadata("reference_images", references.Count);

    string observationId = span.SpanId.ToHexString();

    try
    {
        // Upload the reference images first so the input can point at them. Langfuse links
        // media by id, so it does not matter that the trace has not been exported yet.
        var inputTokens = new List<string>();
        foreach (var reference in references)
            inputTokens.Add(await media.UploadAsync(
                reference.Bytes, reference.ContentType, traceId, observationId, field: "input"));

        span.WithImageInput(prompt.Template, inputTokens);

        Console.WriteLine($"  {spanName}: generating from {references.Count} reference image(s)...");
        var result = await images.EditAsync(prompt.Template, references);

        string outputToken = await media.UploadAsync(
            result.Png, "image/png", traceId, observationId, field: "output");

        span.WithImageOutput(outputToken)
            .WithUsage(result.InputTokens, result.OutputTokens, result.TotalTokens);

        if (result.RevisedPrompt is { Length: > 0 })
            span.WithMetadata("revised_prompt", result.RevisedPrompt);

        return result;
    }
    catch (Exception ex)
    {
        span.RecordFailure(ex);
        throw;
    }
}

string ResolveGarmentPath(string garment)
{
    string name = garment.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        ? garment
        : garment + ".png";

    foreach (var folder in new[] { projectDirectory, AppContext.BaseDirectory })
    {
        string candidate = Path.Combine(folder, name);
        if (File.Exists(candidate)) return candidate;
    }

    throw new FileNotFoundException(
        $"Could not find the product photo '{name}'. Looked in '{projectDirectory}' and "
        + $"'{AppContext.BaseDirectory}'. Set Garments:SourceDirectory if it lives elsewhere.");
}

async Task<string> ReadPromptFileAsync(string fileName)
{
    foreach (var folder in new[] { projectDirectory, AppContext.BaseDirectory })
    {
        string candidate = Path.Combine(folder, fileName);
        if (File.Exists(candidate)) return await File.ReadAllTextAsync(candidate);
    }

    throw new FileNotFoundException(
        $"Could not find the prompt file '{fileName}'. Looked in '{projectDirectory}' and "
        + $"'{AppContext.BaseDirectory}'.");
}
