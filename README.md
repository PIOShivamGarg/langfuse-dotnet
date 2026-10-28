# LangfuseDemo

A minimal .NET 9 console app that calls **Azure AI Foundry** through `Microsoft.Extensions.AI`,
traces the call with **OpenTelemetry**, and ships the trace to **Langfuse** — with the prompt
itself versioned in and fetched from Langfuse Prompt Management.

## Configuration

**No credentials are committed.** Settings load in three layers, each overriding the last:

| Layer | File / source | Committed? | Use for |
|---|---|---|---|
| 1 | `appsettings.json` | yes | Non-secret defaults; secrets left blank |
| 2 | `appsettings.Local.json` | **no** (gitignored) | Your real keys during local dev |
| 3 | Environment variables | n/a | CI and production |

### Local development

Create `appsettings.Local.json` next to the `.csproj` (it is in `.gitignore`):

```json
{
  "Langfuse": {
    "PublicKey": "pk-lf-...",
    "SecretKey": "sk-lf-..."
  },
  "AzureAIFoundry": {
    "Endpoint": "https://<your-resource>.cognitiveservices.azure.com",
    "ApiKey": "<your-foundry-key>"
  }
}
```

### CI / production

Use environment variables instead — they override both JSON files. A `:` in the config path
becomes `__`:

```bash
export Langfuse__PublicKey="pk-lf-..."
export Langfuse__SecretKey="sk-lf-..."
export AzureAIFoundry__Endpoint="https://<your-resource>.cognitiveservices.azure.com"
export AzureAIFoundry__ApiKey="<your-foundry-key>"
```

Any missing required value fails at startup with a message naming the exact key, rather than
surfacing later as a confusing `401`.

## Run

Langfuse must be reachable at `Langfuse:BaseUrl` before you start, because the prompt is
fetched first.

```bash
dotnet run
```

Expected output:

```
Using Langfuse prompt 'otel-explainer' v1 (label: production)
Asking the AI...

AI says: OpenTelemetry is an open-source framework that helps developers...

Done. Open http://localhost:3000 -> Tracing; the generation links back to prompt 'otel-explainer' v1.
```

## How the prompt is versioned

The prompt text lives in Langfuse, not in `Program.cs`. On first run against an empty
instance, `SeedPromptIfMissing` creates version 1 from the seed template; after that it
no-ops. Edit the prompt in the Langfuse UI to create v2, v3, … and move the `production`
label to pick which version is live — no redeploy.

Each generation span is stamped with `langfuse.observation.prompt.name` and
`langfuse.observation.prompt.version`, which is how Langfuse links the trace back to the
exact prompt version and populates the prompt's **Metrics** tab.

## Data flow

```
Azure AI Foundry  ──(completion + token usage)──>  Microsoft.Extensions.AI
                                                     │  UseOpenTelemetry() decorator
                                                     ▼
                                          ActivitySource "LangfuseDemo.AI"
                                                     │  AddSource(...)
                                                     ▼
                                          TracerProvider pipeline
                                           ├─ LangfusePromptLinkProcessor (adds prompt name + version)
                                           └─ OTLP exporter (HTTP/protobuf, Basic auth)
                                                     │
                                                     ▼
                                          Langfuse /api/public/otel/v1/traces
```
