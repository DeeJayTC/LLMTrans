# AdaptiveAPI — docker compose guide

Everything you need to run AdaptiveAPI locally or on a single VM: API (.NET 10),
Admin UI (nginx), optional chat demo, optional Postgres / Redis / OpenTelemetry.
Default storage is SQLite on a named volume so there is nothing to set up on
first run.

## TL;DR

```bash
cp .env.example .env                       # edit as needed
docker compose up --build                  # API + Admin UI (~30s first build)
docker compose --profile demo up --build   # ...plus the chat demo
```

Once healthy:

| Service        | URL                                | Enabled by                        |
| -------------- | ---------------------------------- | --------------------------------- |
| Admin UI       | <http://localhost:8000>            | default                           |
| API            | <http://localhost:8080>            | default                           |
| Health check   | <http://localhost:8080/healthz>    | default                           |
| Chat demo UI   | <http://localhost:8100>            | `--profile demo`                  |
| Chat demo API  | <http://localhost:5100>            | `--profile demo`                  |

Tear it all down with `docker compose down`. Add `-v` to also drop the SQLite
volume (`adaptiveapi-data`) and start fresh.

## The chat demo

A small .NET backend + Vue UI that talks to OpenAI through AdaptiveAPI, so you
can see the full pipeline end-to-end (user prompt → translated request →
OpenAI → translated reply → user) with per-stage timings.

```bash
# from deploy/
docker compose --profile demo up --build
```

Then open <http://localhost:8100>, paste your OpenAI key into the field, and
start chatting. The default language pair is `en-US ↔ de`; change it in the
UI header. With `DEMO_INCLUDE_PAYLOADS=true` (on by default in `.env.example`)
you also see the raw OpenAI + DeepL request/response bodies in the pipeline
log — **turn this off before exposing the demo to anyone else's traffic**.

The demo reads an `rt_dev_LOCALDEMO` route token seeded by the API, so it
works without touching the admin UI. Swap `DEV_FIXED_ROUTE_TOKEN` for a
real token once you start configuring real routes.

## Try the proxy directly (no demo UI)

`.env.example` seeds the same `rt_dev_LOCALDEMO` route against OpenAI. With
`LLMTRANS_DEFAULT_TRANSLATOR=passthrough` (the default) translation is a no-op
so the proxy round-trips without a translator key. To actually translate, set
`LLMTRANS_DEFAULT_TRANSLATOR=deepl` + `DEEPL_API_KEY`, or `llm` +
`LLM_TRANSLATOR_API_KEY`, restart, then:

```bash
curl -s http://localhost:8080/v1/rt_dev_LOCALDEMO/chat/completions \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -H "X-AdaptiveApi-Target-Lang: de" \
  -H "Content-Type: application/json" \
  -d '{
        "model": "gpt-4o-mini",
        "messages": [{"role":"user","content":"Hello, world!"}]
      }' | jq .
```

The request body is forwarded byte-identical to OpenAI (your `Authorization`
never touches our log pipeline). The assistant's English reply is translated
to German before returning.

## MCP via the UI

1. Open <http://localhost:8000/mcp/catalog>, pick any catalog entry.
2. Or go to `/mcp` and add a custom server (remote URL or stdio-local).
3. On creation the UI shows the issued route token once — copy it.
4. Click **Snippet**, pick your client (Claude Desktop / Cursor / Zed /
   VS Code + Continue / raw), paste into that client's MCP config,
   replacing `<your-route-token>` with the copied token.

Stdio-local servers also need the `@adaptiveapi/mcp-bridge` npm package.
The bridge is not yet published; until it ships, the stdio path is a
Flow B API target only (`POST /mcp-translate/<token>`).

## Environment variables

See [.env.example](.env.example) for the full list with inline comments.
Highlights:

| Var                          | Purpose                                                                 |
| ---------------------------- | ----------------------------------------------------------------------- |
| `LLMTRANS_DEFAULT_TRANSLATOR`| `passthrough` \| `deepl` \| `llm`                                       |
| `DEEPL_API_KEY`              | DeepL key when `default=deepl`. Free-tier endpoint: set `DEEPL_BASE_URL=https://api-free.deepl.com/` |
| `LLM_TRANSLATOR_API_KEY`     | OpenAI-compatible key when `default=llm`                                |
| `DEV_FIXED_ROUTE_TOKEN`      | Seeds a known route token. **Unset in production.**                     |
| `PUBLIC_BASE_URL`            | What the UI advertises in MCP client snippets                            |
| `DEMO_INCLUDE_PAYLOADS`      | Demo only — surfaces raw payloads in the pipeline log. Off for prod.    |
| `OPENAI_API_KEY`             | Demo only — forwarded from demo backend to OpenAI                        |

## Scaling up

Uncomment `postgres` and `redis` in [docker-compose.yml](docker-compose.yml),
then on the `api` service set:

```yaml
environment:
  Database__Provider: Postgres
  Database__ConnectionString: Host=postgres;Database=adaptiveapi;Username=adaptiveapi;Password=${POSTGRES_PASSWORD}
  Redis__ConnectionString: redis:6379
```

Redis picks up translation cache, rate limits, and route/rule cache
automatically when the connection string is present. Postgres adds
JSONB-backed rule storage + row-level security policies (defense in depth
over the per-query tenant filter).

## Observability

Uncomment `otel-collector` and set `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317`
on the `api` service. The API emits spans per pipeline stage, metrics
(`adaptiveapi_requests_total`, `adaptiveapi_placeholder_integrity_failures_total`,
`adaptiveapi_stream_ttft_ms`), and structured logs without message content by
default.

## Production notes

- `ASPNETCORE_ENVIRONMENT=Production`.
- Unset `DEV_FIXED_ROUTE_TOKEN` so the seeder generates a strong token (and
  logs it once).
- `DEMO_INCLUDE_PAYLOADS=false` if the demo profile is running.
- Terminate TLS at an upstream load balancer, or edit `nginx.conf` (UI
  image) to add a TLS server block.
- Scale `api` horizontally — the data plane is stateless; Redis handles
  shared state.

For Kubernetes, see [helm/README.md](helm/README.md).
