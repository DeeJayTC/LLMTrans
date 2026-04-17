# llmtrans — self-host via docker compose

Smallest possible layout: API (.NET 10) + UI (nginx) + a SQLite volume. Redis and Postgres
are pre-wired but commented out; turn them on when you outgrow the single-container shape.

## Quick start

```bash
cd deploy
cp .env.example .env           # edit as needed
docker compose up --build
```

Once healthy:

- UI: <http://localhost:8000>
- API direct: <http://localhost:8080>
- Health: <http://localhost:8080/healthz>

## Try the proxy (passthrough default)

`.env.example` seeds a fixed dev route token (`DEV_FIXED_ROUTE_TOKEN`) pointing at OpenAI.
With `LLMTRANS_DEFAULT_TRANSLATOR=passthrough` translation is a no-op so the proxy round-trips
without an API key. To actually translate responses, set `LLMTRANS_DEFAULT_TRANSLATOR=deepl`
(plus `DEEPL_API_KEY`) or `llm` (plus `LLM_TRANSLATOR_API_KEY`), then hit:

```bash
curl -s http://localhost:8080/v1/rt_dev_LOCALDEMO/chat/completions \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -H "X-LlmTrans-Target-Lang: de" \
  -H "Content-Type: application/json" \
  -d '{
        "model": "gpt-4o-mini",
        "messages": [{"role":"user","content":"Hello, world!"}]
      }' | jq .
```

The request body is forwarded byte-identical to OpenAI (your `Authorization` never touches
our log pipeline). The assistant's English reply is translated to German before returning.

## MCP via the UI

1. Open the UI at <http://localhost:8000/mcp/catalog>, pick any catalog entry.
2. Or go to `/mcp` and add a custom server (remote URL or stdio-local).
3. On creation the UI shows the issued route token once — copy it.
4. Click **Snippet**, pick your client (Claude Desktop / Cursor / Zed / VS Code + Continue / raw),
   paste into that client's MCP config, replacing `<your-route-token>` with the copied token.

For stdio-local servers you also need the `@llmtrans/mcp-bridge` npm package. The bridge is
not yet published (see [README](../src/LlmTrans.Ui/README.md) → parked work). Until it ships,
the stdio path is a Flow B API target only (`POST /mcp-translate/<token>`).

## Scaling up

Uncomment `postgres` and `redis` in `docker-compose.yml`, then on the `api` service set:

```yaml
environment:
  Database__Provider: Postgres
  Database__ConnectionString: Host=postgres;Database=llmtrans;Username=llmtrans;Password=${POSTGRES_PASSWORD}
  Redis__ConnectionString: redis:6379
```

Redis picks up translation cache, rate limits, and route/rule cache automatically when the
connection string is present. Postgres adds JSONB-backed rule storage + row-level security
policies (defense in depth over the per-query tenant filter).

## Observability

Uncomment `otel-collector` and the `OTEL_EXPORTER_OTLP_ENDPOINT` env var. The API emits spans
per pipeline stage (plan §10.1), metrics (`llmtrans_requests_total`, `llmtrans_placeholder_integrity_failures_total`,
`llmtrans_stream_ttft_ms`), and structured logs without message content by default.

## Production notes

- Set `ASPNETCORE_ENVIRONMENT=Production`.
- Unset `DEV_FIXED_ROUTE_TOKEN` so the seeder generates a strong token (and logs it once).
- Terminate TLS at an upstream load balancer or edit `nginx.conf` to add a TLS server block.
- Scale `api` horizontally — the data plane is stateless; Redis handles shared state.
