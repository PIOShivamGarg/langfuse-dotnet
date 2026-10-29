using System.Diagnostics;
using System.Text.Json;

namespace LangfuseDemo.Langfuse;

/// <summary>
/// Writes the span attributes Langfuse reads when it turns an OpenTelemetry span into an
/// observation. The image API has no Microsoft.Extensions.AI client to instrument it for
/// us, so this pipeline opens its own spans and fills in the same attributes by hand.
///
/// Attribute names come from the Langfuse OpenTelemetry property mapping.
/// </summary>
static class LangfuseTrace
{
    private static readonly JsonSerializerOptions Json = new()
    {
        // Keep the media tokens ("@@@langfuseMedia:...@@@") readable rather than @-escaped.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Marks a span as a Langfuse generation produced by a named model.</summary>
    public static Activity AsGeneration(this Activity activity, string modelName)
    {
        activity.SetTag("langfuse.observation.type", "generation");
        activity.SetTag("langfuse.observation.model.name", modelName);
        return activity;
    }

    /// <summary>Links the generation to the exact Langfuse prompt version that produced it.</summary>
    public static Activity WithPrompt(this Activity activity, LangfusePrompt prompt)
    {
        activity.SetTag("langfuse.observation.prompt.name", prompt.Name);
        activity.SetTag("langfuse.observation.prompt.version", prompt.Version);
        return activity;
    }

    /// <summary>
    /// Records the request as a chat-style message array: the prompt text followed by one
    /// entry per reference image. Each <paramref name="imageTokens"/> entry is a Langfuse
    /// media token, which the UI replaces with the image itself.
    /// </summary>
    public static Activity WithImageInput(
        this Activity activity, string promptText, IEnumerable<string> imageTokens)
    {
        var content = new List<object> { new { type = "text", text = promptText } };

        foreach (var token in imageTokens)
            content.Add(new { type = "image_url", image_url = new { url = token } });

        activity.SetTag(
            "langfuse.observation.input",
            JsonSerializer.Serialize(new[] { new { role = "user", content } }, Json));

        return activity;
    }

    /// <summary>Records the generated image (again as a media token) as the span's output.</summary>
    public static Activity WithImageOutput(this Activity activity, string imageToken)
    {
        activity.SetTag(
            "langfuse.observation.output",
            JsonSerializer.Serialize(
                new
                {
                    role = "assistant",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = imageToken } },
                    },
                },
                Json));

        return activity;
    }

    /// <summary>Feeds Langfuse's cost and usage charts.</summary>
    public static Activity WithUsage(this Activity activity, int? input, int? output, int? total)
    {
        if (input is null && output is null && total is null)
            return activity;

        var usage = new Dictionary<string, int>();
        if (input is not null)  usage["input"]  = input.Value;
        if (output is not null) usage["output"] = output.Value;
        if (total is not null)  usage["total"]  = total.Value;

        activity.SetTag("langfuse.observation.usage_details", JsonSerializer.Serialize(usage, Json));
        return activity;
    }

    /// <summary>Adds one filterable metadata key to the observation.</summary>
    public static Activity WithMetadata(this Activity activity, string key, object? value)
    {
        if (value is not null)
            activity.SetTag($"langfuse.observation.metadata.{key}", value.ToString());
        return activity;
    }

    /// <summary>
    /// Trace-level attributes. Langfuse recommends setting these on every span in the trace
    /// so filtering and aggregation work no matter which span is inspected.
    /// </summary>
    public static Activity WithTraceContext(
        this Activity activity, string traceName, string sessionId, params string[] tags)
    {
        activity.SetTag("langfuse.trace.name", traceName);
        activity.SetTag("langfuse.session.id", sessionId);
        if (tags.Length > 0)
            activity.SetTag("langfuse.trace.tags", tags);
        return activity;
    }

    /// <summary>Marks the span as failed so it shows up red in Langfuse.</summary>
    public static void RecordFailure(this Activity activity, Exception ex)
    {
        activity.SetTag("langfuse.observation.level", "ERROR");
        activity.SetTag("langfuse.observation.status_message", ex.Message);
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
    }
}
