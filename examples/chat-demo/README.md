# chat-demo

A tiny sample showing how an application integrates with adaptiveapi. It's two
services:

- `backend/` — a .NET minimal API (one `Program.cs`, ~200 lines) that calls
  OpenAI's chat-completions API through adaptiveapi.
- `ui/` — a Vue 3 chat interface with a language picker, stream / no-stream
  toggle, and a live request log.

The integration surface is one-line: set the OpenAI base URL to
`http://<adaptiveapi>/v1/<route-token>/` and add `X-AdaptiveApi-Target-Lang` to each
request. Everything else — streaming, JSON shape, tool calls — passes through
untouched.

## Run it

From the repo root:

```bash
cd deploy
cp .env.example .env
docker compose --profile demo up --build
```

Then open:

- **Demo UI**:   <http://localhost:8100>
- Demo API:     <http://localhost:5100/api/config>
- adaptiveapi API: <http://localhost:8080>
- adaptiveapi admin UI: <http://localhost:8000>

Without `--profile demo` the demo services aren't started — the main adaptiveapi
stack runs on its own, undisturbed.

### OpenAI key

The demo UI opens a key panel on first load. Paste your OpenAI key there —
it's saved in the browser's `localStorage` and sent to the demo backend as
an `X-Demo-OpenAI-Key` header on every request. The backend forwards it
verbatim to OpenAI as `Authorization: Bearer …` through adaptiveapi.

If you'd rather put the key on the server side, set `OPENAI_API_KEY` in
`deploy/.env`. The UI's `/api/config` call sees `hasServerKey: true` and
skips the prompt.

> **Localhost only.** The browser-stored key is plaintext. Don't expose this
> demo to the internet — deploy a real auth layer if you want to host it
> somewhere.

### What the demo does

The UI picks a language, posts a message to its own backend, which relays to
adaptiveapi, which relays to OpenAI. adaptiveapi translates the user message into
English before OpenAI sees it, then translates OpenAI's reply back into the
user's language before returning it. The UI sees only the final, localized
answer — exactly what it'd see if you'd used the OpenAI SDK directly with an
English chat model that happened to speak the user's language natively.

Flip the **stream** checkbox and pick **progressive** to see lower-latency
sentence-by-sentence deltas, or **sentence-boundary** for the default strategy
that waits until a complete sentence is ready before translating.

## The integration, in code

```csharp
// Point OpenAI's client at adaptiveapi. The token identifies your route + language
// pair + configured translator (DeepL / LLM / passthrough).
var http = factory.CreateClient("adaptiveapi");
http.BaseAddress = new Uri($"{adaptiveapiBaseUrl}/v1/{routeToken}/");

using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
{
    Content = JsonContent.Create(new { model = "gpt-4o-mini", messages = [...] }),
};
req.Headers.Add("Authorization", $"Bearer {OPENAI_API_KEY}");    // forwarded upstream verbatim
req.Headers.Add("X-AdaptiveApi-Target-Lang", "de");                 // stripped before upstream
req.Headers.Add("X-AdaptiveApi-Source-Lang", "en");                 // stripped before upstream

var resp = await http.SendAsync(req);
```

Everything else is unchanged from a direct-to-OpenAI integration: body shape,
authentication, streaming semantics, tool calls, error responses.

## Dev without Docker

```bash
# terminal 1 — adaptiveapi API
cd ../../src/AdaptiveApi.Api
dotnet run --urls http://localhost:8080

# terminal 2 — demo backend
cd backend
export Demo__OpenAiApiKey=sk-...
export Demo__LlmtransBaseUrl=http://localhost:8080
dotnet run --urls http://localhost:5100

# terminal 3 — demo UI
cd ui
npm install
npm run dev                     # → http://localhost:5174
```

The dev UI (`npm run dev`) proxies `/api` to `http://localhost:5100`, so the
backend stays cleanly separated.

## Customizing

`docker-compose.yml` and `.env.example` expose these knobs:

| Variable | Default | Purpose |
| --- | --- | --- |
| `OPENAI_API_KEY` | — | Required for the demo backend to reach OpenAI. |
| `DEMO_MODEL` | `gpt-4o-mini` | OpenAI model the demo requests. |
| `DEMO_LLM_LANGUAGE` | `en` | The upstream LLM's working language. |
| `DEMO_TEMPERATURE` | `0.3` | Sampling temperature. |
| `DEMO_SYSTEM_PROMPT` | concise assistant | System prompt, translated alongside user text. |
| `DEV_FIXED_ROUTE_TOKEN` | `rt_dev_LOCALDEMO` | The route token adaptiveapi seeds + the demo uses. |
| `DEMO_API_PORT` / `DEMO_UI_PORT` | `5100` / `8100` | Host-side ports for the demo pair. |

To actually see translations, configure adaptiveapi with a translator backend
(set `LLMTRANS_DEFAULT_TRANSLATOR=deepl` + `DEEPL_API_KEY`). Without that, the
default `passthrough` translator means you'll get the upstream LLM's own
output verbatim — useful for testing the routing shape but not the
translation.
