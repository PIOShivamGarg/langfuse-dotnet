# LangfuseDemo

A .NET 9 console app that turns a flat **garment product photo** into a pair of photorealistic
e-commerce model shots using **Azure OpenAI `gpt-image-2`**, with the prompts versioned in
**Langfuse Prompt Management** and every call traced to **Langfuse** — input images and
generated images attached to the trace, not just text.

## The pipeline

For each garment, two calls chained together:

| Step | Reference images sent | Prompt | Produces |
|---|---|---|---|
| 1. front shot | the product photo | `garment-front-shot` | model wearing the garment, front/hero view |
| 2. back shot | the product photo **+ the front shot from step 1** | `garment-back-shot` | the same model, seen from behind |

Step 2 depends on step 1 by design: the back prompt treats the first image as the garment
authority and the second as "the approved front frame", which is what keeps the model,
styling and lighting consistent across the pair.

Both land in Langfuse under one trace per garment, and one session per run.

## Configuration

**Nothing is committed.** There is a single settings file, `appsettings.Local.json`, and it
is gitignored. Two layers, the second overriding the first:

| Layer | File / source | Committed? | Use for |
|---|---|---|---|
| 1 | `appsettings.Local.json` | **no** (gitignored) | Every setting, local dev |
| 2 | Environment variables | n/a | CI and production |

Because no template is committed, **this section is the reference for the key set** — a fresh
clone has no `appsettings.json` to copy. Create `appsettings.Local.json` next to the
`.csproj` with all of the following:

```json
{
  "Langfuse": {
    "BaseUrl": "http://localhost:3000",
    "PublicKey": "pk-lf-...",
    "SecretKey": "sk-lf-...",
    "FrontPromptName": "garment-front-shot",
    "BackPromptName": "garment-back-shot",
    "PromptLabel": "production",
    "SeedPromptIfMissing": true
  },
  "AzureOpenAIImage": {
    "Endpoint": "https://<your-image-resource>.openai.azure.com",
    "ApiKey": "<your-image-key>",
    "ApiVersion": "preview",
    "Deployment": "gpt-image-2",
    "Quality": "high",
    "Size": "1024x1536"
  },
  "Garments": {
    "Images": ["PBD525L", "BD7806J", "AR101A"],
    "OutputDirectory": "Output"
  }
}
```

| Key | Required | Notes |
|---|---|---|
| `Langfuse:BaseUrl` | yes | Where your Langfuse instance listens |
| `Langfuse:PublicKey` / `SecretKey` | yes | Used for both the REST API and the OTLP exporter |
| `Langfuse:FrontPromptName` / `BackPromptName` | yes | Prompt names in Langfuse |
| `Langfuse:PromptLabel` | yes | Which version is live, e.g. `production` |
| `Langfuse:SeedPromptIfMissing` | no | `true` pushes the `.md` seeds in on a blank instance |
| `AzureOpenAIImage:Endpoint` / `ApiKey` | yes | The `gpt-image-*` resource |
| `AzureOpenAIImage:ApiVersion` | yes | `preview` uses the `/openai/v1` route; a dated version uses the deployment route |
| `AzureOpenAIImage:Deployment` | yes | e.g. `gpt-image-2` |
| `AzureOpenAIImage:Quality` | no | Defaults to `high` |
| `AzureOpenAIImage:Size` | no | Defaults to `1024x1536` |
| `Garments:Images` | yes | Product photo names, without `.png` |
| `Garments:OutputDirectory` | no | Defaults to `Output` |
| `Garments:SourceDirectory` | no | Where the product photos and prompt `.md` files live; defaults to the working directory |

The image resource is **separate** from any chat deployment — `gpt-image-*` models have
their own endpoint and key.

The file is optional at load time, so an environment-variable-only deployment still runs.
Anything genuinely missing is reported by name at startup.

### Environment variables

Environment variables override `appsettings.Local.json`, in either spelling. The nested
config path uses `__` as the `:` separator:

```bash
export Langfuse__PublicKey="pk-lf-..."
export AzureOpenAIImage__ApiKey="<your-image-key>"
```

…or the flat `AZURE_*` names, if you already have them set:

```bash
export AZURE_OPENAI_ENDPOINT="https://<your-image-resource>.openai.azure.com"
export AZURE_OPENAI_API_KEY="<your-image-key>"
export AZURE_OPENAI_API_VERSION="2025-04-01-preview"
export AZURE_IMAGE_DEPLOYMENT="gpt-image-2"
export AZURE_IMAGE_QUALITY="high"
export AZURE_IMAGE_SIZE="1024x1536"
```

For the `AzureOpenAIImage` block the lookup order is: `AzureOpenAIImage__*` environment
variable, then the flat `AZURE_*` one, then the file, then the built-in default. The flat
names are read from the environment directly rather than through the config layers —
otherwise a value in the file would silently win over `AZURE_OPENAI_ENDPOINT`.

Any missing required value fails at startup with a message naming the exact key, rather than
surfacing later as a confusing `401`.

## Run

Langfuse must be reachable at `Langfuse:BaseUrl` first — the prompts are fetched from it
before anything is generated.

```bash
dotnet run                 # the first garment in Garments:Images
dotnet run -- AR101A       # one named garment
dotnet run -- --all        # every garment
```

Generated images are written to `Output/<garment>_front.png` and `Output/<garment>_back.png`.

Expected output:

```
Prompts
  garment-front-shot v1 (label: production)
  garment-back-shot v1 (label: production)

Model gpt-image-2  |  1024x1536  |  quality high
Garments: PBD525L

PBD525L  (305 KB)
  front-shot: generating from 1 reference image(s)...
  front -> Output\PBD525L_front.png
  back-shot: generating from 2 reference image(s)...
  back  -> Output\PBD525L_back.png

Flushing traces to Langfuse...

Done. Open http://localhost:3000 -> Tracing -> session 'shoot-20261029-085448'.
```

A high-quality `gpt-image-2` render takes a while — minutes per image is normal, so a
`--all` run is six generations.

## How the prompts are versioned

`front_view_shot_prompt.md` and `back_view_shot_prompt.md` are **seed text only**. On the
first run against an empty Langfuse instance, `SeedPromptIfMissing` pushes each one in as
version 1; after that it no-ops and the Langfuse UI is the source of truth. Edit a prompt
there to create v2, v3, … and move the `production` label to pick which version is live —
no redeploy, no code change.

Each generation span is stamped with `langfuse.observation.prompt.name` and
`langfuse.observation.prompt.version`, which links the trace back to the exact prompt
version and populates the prompt's **Metrics** tab.

Render settings travel with the prompt too: a `size` or `quality` value in the prompt's
config in Langfuse overrides `appsettings.json`, so changing the output dimensions is a
tracked prompt version rather than a config edit.

## How the images get into Langfuse

Images are **not** inlined as base64 in the span. A multi-megabyte data URI in an OTLP
attribute bloats every export and can be rejected outright. Instead `LangfuseMediaClient`
follows the same three-step path the official SDKs take:

```
1. POST  /api/public/media            -> { mediaId, uploadUrl }
2. PUT   {uploadUrl}                  -> the raw bytes
                                         (+ x-amz-checksum-sha256, x-ms-blob-type)
3. PATCH /api/public/media/{mediaId}  -> report the upload result
```

The span then carries only a short reference token where the image belongs:

```
@@@langfuseMedia:type=image/png|id=<mediaId>|source=bytes@@@
```

The Langfuse UI swaps that token for the rendered image, so the trace shows the reference
photo(s) on the input and the generated shot on the output.

## Data flow

```
product photo ──┐
                ├─> AzureImageClient ──> POST /openai/deployments/gpt-image-2/images/edits
front shot   ───┘         │                     (multipart: image[] × N, prompt, size, quality)
                          │
                          ├─ reference bytes ──> Langfuse Media API ──> media token (input)
                          └─ generated PNG   ──> Langfuse Media API ──> media token (output)
                          │
                          ▼
           ActivitySource "LangfuseDemo.ImagePipeline"
                          │  span tags: langfuse.observation.{type,input,output,
                          │             model.name,usage_details,prompt.*}
                          ▼
                  TracerProvider ──> OTLP exporter (HTTP/protobuf, Basic auth)
                          │
                          ▼
                Langfuse /api/public/otel/v1/traces
```

Because the image API has no `Microsoft.Extensions.AI` client to instrument it, the pipeline
opens its own spans and writes the Langfuse GenAI attributes by hand — see
`Langfuse/LangfuseTrace.cs`.

## Layout

| File | Purpose |
|---|---|
| `Program.cs` | Config, prompt fetch, OTel setup, and the two-step shoot |
| `Imaging/AzureImageClient.cs` | Multipart REST client for `gpt-image-*` `/images/edits` |
| `Langfuse/LangfusePromptClient.cs` | Prompt Management: seed and fetch by label |
| `Langfuse/LangfuseMediaClient.cs` | Media upload + reference tokens |
| `Langfuse/LangfuseTrace.cs` | The Langfuse OTel span attributes |
