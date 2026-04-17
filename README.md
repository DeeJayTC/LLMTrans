# llmtrans

**Drop-in multilingual proxy for LLMs, MCP servers, and any HTTP+JSON API.**

Most popular LLMs are trained predominantly on English: ask the same question
in German or Japanese and the answer is measurably worse than in English, on
the same model, for the same money. llmtrans sits between your app and the
vendor so the LLM keeps seeing English while your user keeps seeing their
language. JSON shapes, code blocks, URLs, tool schemas, and streaming deltas
survive the round trip. Works for MCP servers too.

Change your SDK's base URL from `api.openai.com` / `api.anthropic.com` / your
MCP server to llmtrans. That's it. Built on .NET 10 with the official
[DeepL .NET SDK](https://github.com/DeepLcom/deepl-dotnet) and a Vue 3 admin UI.

---

## Contents

- [Quick start](#quick-start)
- [How it fits into your code](#how-it-fits-into-your-code)
  - [OpenAI SDK](#openai-sdk)
  - [Anthropic SDK](#anthropic-sdk)
  - [MCP clients (Claude Desktop, Cursor, Zed, VS Code)](#mcp-clients)
  - [Any other HTTP+JSON API (Cohere, Mistral, Azure, …)](#generic-httpjson-api)
- [Request-time controls](#request-time-controls)
- [The admin UI](#the-admin-ui)
- [Configuration](#configuration)
- [Deployment](#deployment)
- [Extending llmtrans](#extending-llmtrans)
- [FAQ](#faq)
- [Roadmap](#roadmap)
- [License](#license)

---

## Quick start

```bash
git clone https://github.com/DeeJayTC/llmtrans.git
cd llmtrans/deploy
cp .env.example .env
docker compose up --build                  # API + Admin UI
docker compose --profile demo up --build   # adds the chat demo
```

- **API**: <http://localhost:8080> (health at `/healthz`)
- **Admin UI**: <http://localhost:8000>
- **Chat demo** (with `--profile demo`): <http://localhost:8100>

Full docker compose guide, env vars, scaling, observability, and production
notes live in [deploy/README.md](deploy/README.md).

Run from source instead:

```bash
cd src/LlmTrans.Api  &&  dotnet run --urls http://localhost:5000
cd src/LlmTrans.Ui   &&  npm install && npm run dev     # UI on :5173
```

## How it fits into your code

### OpenAI SDK

Change the base URL. Your SDK, your keys, and your streaming code all stay
the same.

```python
from openai import OpenAI

client = OpenAI(
    api_key="sk-...",                     # your existing OpenAI key, untouched
    base_url="https://llmtrans.example.com/v1/rt_yourtenant_xxxxx",
)

resp = client.chat.completions.create(
    model="gpt-4o-mini",
    messages=[{"role": "user", "content": "Was ist der Unterschied zwischen einem Array und einer Liste?"}],
    extra_headers={"X-LlmTrans-Target-Lang": "de"},
)
print(resp.choices[0].message.content)
# ↑ response arrives in German; the upstream model saw your question in English.
```

Streaming works identically. The proxy buffers at sentence boundaries, translates
each complete sentence, and re-emits SSE deltas. Your streaming UI animates in
near-real time without losing any tokens.

Tool calls work end-to-end: `function.arguments` (JSON-in-JSON) is parsed, walked
with a key-aware denylist (`id`, `*_id`, `*_code`, emails, URLs are preserved), and
re-serialised. Tool results come back translated too.

### Anthropic SDK

```python
import anthropic

client = anthropic.Anthropic(
    api_key="sk-ant-...",
    base_url="https://llmtrans.example.com/anthropic/v1/rt_yourtenant_xxxxx",
)

message = client.messages.create(
    model="claude-3-7-sonnet-latest",
    max_tokens=1024,
    system="Tu es un assistant serviable.",
    messages=[{"role": "user", "content": "Explique-moi le théorème de Pythagore."}],
    extra_headers={"X-LlmTrans-Target-Lang": "fr"},
)
```

### MCP clients

Point any MCP client (Claude Desktop, Cursor, Zed, VS Code + Continue) at
llmtrans and every MCP server it connects to now speaks your language: tool
descriptions, tool arguments, tool results, prompts.

**Remote MCP servers** (Linear, Atlassian, Sentry, Stripe, Zoom, …): change
the `url` in your client's MCP config:

```jsonc
{
  "mcpServers": {
    "linear-de": {
      "url": "https://llmtrans.example.com/mcp/rt_yourtenant_xxxxx",
      "headers": {
        "Authorization": "Bearer <your-linear-oauth-token>"
      }
    }
  }
}
```

Your OAuth token flows upstream byte-identical; llmtrans never stores or logs it.
The admin UI's `/mcp/catalog` has a curated list of 15 popular servers
pre-configured: one click and you get a copy-paste snippet for your client.

**Stdio MCP servers** (GitHub, GitLab, Slack, Postgres, Filesystem, …) plug into
the stateless translate API via a thin local bridge. The MCP client keeps spawning
your existing upstream command, your credentials stay on your machine, and only
JSON-RPC bodies flow through llmtrans for translation. The bridge CLI ships as
[`@llmtrans/mcp-bridge`](src/LlmTrans.McpBridge/) on npm and as a static Go
binary for zero-Node environments.

### Generic HTTP+JSON API

Any non-typed LLM or JSON API (Cohere, Mistral, local vLLM, your internal REST
service) plugs in via a declarative route config. Tell llmtrans which JSON paths
carry human text and it translates them in both directions:

```jsonc
{
  "upstream": {
    "urlTemplate": "https://api.cohere.com/v1/chat",
    "method": "POST",
    "additionalHeaders": { "X-Client-Name": "my-app" }
  },
  "request": {
    "translateJsonPaths": [
      "$.message",
      "$.chat_history[*].message",
      "$.tools[*].description"
    ]
  },
  "response": {
    "streaming": "sse",
    "eventPath": "$.text",
    "finalPaths": ["$.text", "$.generations[*].text"]
  },
  "direction": "bidirectional"
}
```

Then hit `POST /generic/<token>/` and llmtrans forwards the call with the selected
paths translated in both directions. Works for non-streaming JSON, SSE streams, or
passthrough with translation off.

## Request-time controls

Any of these headers override the route's defaults on a per-request basis. All
`X-LlmTrans-*` headers are stripped before reaching upstream.

| Header | Example | Purpose |
| --- | --- | --- |
| `X-LlmTrans-Target-Lang` | `de` | User language (what the caller speaks) |
| `X-LlmTrans-Source-Lang` | `en` | Upstream/LLM working language |
| `X-LlmTrans-Mode` | `bidirectional` / `request-only` / `response-only` / `off` | Direction toggle |
| `X-LlmTrans-Translator` | `deepl` / `llm` / `passthrough` | Force a translator for this call |
| `X-LlmTrans-Glossary` | `<glossary-id>` | Apply a specific glossary |
| `X-LlmTrans-Style-Rule` | `<style-rule-id>` | Apply a DeepL v3 style rule |
| `X-LlmTrans-Model-Type` | `quality_optimized` / `latency_optimized` / `prefer_quality_optimized` | DeepL model selector |

## The admin UI

<http://localhost:8000> (in dev) surfaces:

- **Routes**: create routes (OpenAI, Anthropic, Generic), issue tokens, manage
  per-route glossary, style rule, and proxy rule bindings.
- **MCP servers**: register remote and stdio-local MCP servers, generate
  copy-paste client snippets (Claude Desktop, Cursor, Zed, VS Code + Continue,
  raw JSON).
- **Catalog**: browse a curated list of 15+ popular MCP servers.
- **Glossaries**: tenant-scoped do-not-translate lists and source → target
  term maps that map 1:1 to DeepL v3 multilingual glossaries.
- **Style rules**: DeepL v3 style rules plus up to 10 × 300-char
  natural-language custom instructions per rule, passed to DeepL on every
  translate call.
- **Proxy rules**: visual editor for per-route allowlist and denylist
  overrides, plus the PII redaction toggle.
- **Playground**: four-pane editor with live send-through for debugging route
  config against real traffic.
- **Logs**: audit event table with filters for status, route, and tenant.
- **Dashboard / Settings**: counts, API health, and tenant view.

List views for glossary entries, style-rule instructions, and proxy rules are
browsable today; richer in-place editors are on the roadmap.

## Configuration

Driven by `appsettings.json`, env vars (`__` separator), or a Kubernetes
ConfigMap. The common knobs:

```jsonc
{
  "Database": {
    "Provider": "Sqlite",                     // Sqlite | Postgres | SqlServer
    "Path": "llmtrans.db",
    "ConnectionString": "Host=postgres;..."
  },
  "Translators": {
    "Default": "passthrough",                 // passthrough | deepl | llm
    "DeepL": {
      "ApiKey": "...",
      "BaseUrl": "https://api.deepl.com/"     // or https://api-free.deepl.com/
    },
    "Llm": {
      "ApiKey": "sk-...",
      "BaseUrl": "https://api.openai.com/",
      "Model": "gpt-4o-mini"
    }
  },
  "Mcp": {
    "CatalogFile": "catalog/mcp-servers.json"
  },
  "PublicBaseUrl": "https://llmtrans.example.com"
}
```

Env-var form for docker-compose / Kubernetes:

```env
Database__Provider=Postgres
Database__ConnectionString=Host=pg;Database=llmtrans;Username=...
Translators__Default=deepl
Translators__DeepL__ApiKey=...
```

## Deployment

### Docker compose

The bundled [deploy/docker-compose.yml](deploy/docker-compose.yml) runs the
API, the admin UI, and a SQLite volume. Postgres, Redis, and OTLP collector
blocks are pre-wired and commented out. Uncomment them and wire the matching
env vars when you outgrow SQLite. A self-contained chat demo
([examples/chat-demo/](examples/chat-demo/)) ships in the same compose file
under the `demo` profile, so `docker compose --profile demo up` spins up a
small .NET backend plus a Vue chat UI you can point an OpenAI key at. Full
walkthrough in [deploy/README.md](deploy/README.md).

### Helm

```bash
helm install llmtrans ./deploy/helm \
  --set database.provider=Postgres \
  --set database.existingSecret.name=llmtrans-db \
  --set translators.existingSecret=llmtrans-translators \
  --set ingress.host=llmtrans.yourdomain.com
```

The chart provisions two Deployments (API + UI), a Service each, an Ingress with
SSE-friendly annotations, an HPA on CPU (2→10 replicas), a PDB with `minAvailable:
1`, and a PVC for SQLite when you haven't pointed at an external database. Full
values reference in [deploy/helm/README.md](deploy/helm/README.md).

## Extending llmtrans

The public repo covers the translation proxy, admin API, and UI. If you need
organisation management, SCIM provisioning, or metered billing, extend the host
via a plugin.

### Plugin interface

Any assembly loaded next to the API binary can contribute services and
endpoints by implementing `LlmTrans.Core.Plugins.IWebPlugin`:

```csharp
using LlmTrans.Core.Plugins;

public sealed class MyPlugin : IWebPlugin
{
    public string Name => "my-plugin";

    public void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IMyThing, MyThing>();
    }

    public void Map(WebApplication app)
    {
        app.MapGet("/my/endpoint", () => "hello");
    }
}
```

`LlmTrans.Api.PluginLoader` scans the base directory for `LlmTrans.*.dll`, plus
the opt-in `plugins/` subfolder, at startup. Discovered plugins run
`ConfigureServices` before `builder.Build()` and `Map` afterwards. No
compile-time coupling between the host and your plugin.

### Plugin deployment shapes

| Shape | How |
| --- | --- |
| **Same image** | Build a downstream image `FROM ghcr.io/deejaytc/llmtrans-api` and `COPY your-plugin.dll /app/`. |
| **Plugin mount** | Mount a volume containing your plugin DLL at `/app/plugins/`. |
| **Sidecar** | Run the API and a plugin-management container side by side in the same pod. |

### What ships as a plugin from DeepL

The SaaS-only features (organisations, invites, SCIM 2.0, Stripe metered
billing, the usage-aggregation worker) live in a separate private sidecar
that implements this same plugin interface. Deploying it is a matter of
adding the SaaS image tag or mounting the plugin DLL; no changes to the
public API are needed.

## FAQ

**Do you store my OpenAI / Anthropic / Linear token?**
No. Upstream credentials ride in request headers, are forwarded byte-identical
to the upstream, and are never persisted or logged. Only a SHA-256 fingerprint
of the `Authorization` header goes into the audit log for abuse correlation,
never the value itself.

**What does llmtrans store?**
Its own route tokens (hashed with Argon2id), its own translator-backend keys
(DeepL and/or an LLM translator, encrypted at rest), and audit metadata (status,
language pair, char counts, duration, integrity failures). Never request or
response bodies by default.

**What happens if a translation loses a placeholder?**
Each translation round-trip has a hard invariant: every `<llmtrans id="TAG_n"/>`
tag emitted into the source must appear exactly once in the translator's output.
If it doesn't, the pipeline falls back to the source text, increments
`llmtrans_placeholder_integrity_failures_total`, and logs the failure. Alerts
should fire above 0.5% over the observation window.

**Does streaming actually stream?**
Yes. The pipeline buffers per `choices[i].delta.content`, flushes at sentence
boundaries once the buffer reaches 80 chars (configurable), translates the
completed segment through the full placeholder round-trip, and re-emits a
synthetic SSE delta. Clients see token-by-token UI with a modest per-sentence
latency cost. For short answers where cumulative buffering is cheaper than
per-sentence dispatch, set `stream_mode: "post"` per route.

**What about tool calls?**
OpenAI's `tool_calls[*].function.arguments` is a JSON string containing JSON. The
pipeline parses the inner JSON, walks it with a key-aware denylist (default:
`id`, `uuid`, `email`, `slug`, `key`, `locale`, `tz`, `url`, `href`, `path`,
plus patterns `*_id`, `*_code`, `*Id`, `*Code`), translates string leaves that
aren't identifiers, and re-serialises. `tool_call_id`, `function.name`, and every
argument key are preserved verbatim. Extend the denylist per tenant via proxy
rules (`{"toolArgKeys":["internal_note"]}`).

**Can I run llmtrans without any translator?**
Yes. The default `passthrough` translator leaves text unchanged, so the proxy
becomes a header-auditing / route-token-gated forwarder. Useful while getting
the routing set up or for passing-through specific routes with
`X-LlmTrans-Mode: off`.

**PII redaction?**
Opt-in via a proxy rule (`redactPii: true`). Detectors cover email,
Luhn-validated credit cards, US SSN, IBAN, IPv4, and phone numbers. Matched
spans are replaced with opaque substitutes (`[redacted-email]`,
`[redacted-card]`, …) before the upstream LLM sees any bytes. The LLM's
response is translated normally. Since it never saw the original PII, it
can't emit it.

For higher recall (names, locations, organisations, driver licences, etc.)
set `PiiRedactor:Provider=presidio` and run the Microsoft Presidio Analyzer
as an HTTP sidecar. llmtrans falls back to the regex redactor if Presidio is
unreachable, so a temporary sidecar outage never opens the door for PII to
reach the upstream.

## Roadmap

Everything below is running today in the public repo. The list is here so you
can see what the product actually covers, not as a promise of things that
might arrive later.

- OpenAI Chat Completions + Responses APIs, Anthropic Messages, MCP (both
  route-proxy and stdio bridge), and a declarative Generic HTTP+JSON adapter
  for anything else.
- Streaming in two strategies: sentence-boundary (default) and progressive
  (lower first-token latency), selectable per request.
- Tool-call support end to end, including the JSON-in-JSON `arguments`
  payloads OpenAI uses.
- Glossaries (mapped to DeepL v3), style rules with custom instructions,
  proxy rules with visual editor, audit log and logs viewer, playground.
- PII redaction, with an optional Presidio sidecar for higher recall.
- OIDC authentication on the admin surface, or open dev mode.
- Full admin UI in Vue 3 plus a private SaaS sidecar for multi-org, SCIM, and
  Stripe metered billing.
- Docker compose and Helm deployments, plus a self-contained chat demo app
  you can spin up alongside the stack.

Things we would still like to build:

- PII detectors for additional jurisdictions (BRSN, JMBG, UK NINO, etc.) and
  more thorough identifier coverage.
- A Grafana dashboard and alert rule bundle on top of the existing OTLP spans
  and metrics.
- Contract tests against live OpenAI, Anthropic, Cohere, and the big MCP
  servers, to catch vendor wire-format drift.
- A UI for the DeepL Document API so non-technical users can translate whole
  files without touching `curl`.

## License

TBD.
