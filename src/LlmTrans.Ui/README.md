# llmtrans-ui

Vue 3 + Vite + TypeScript + Pinia + UnoCSS admin UI for the llmtrans API.

## Dev

```bash
# 1. Start the API (port 5000 assumed in the dev proxy)
cd ../LlmTrans.Api
dotnet run --urls http://localhost:5000

# 2. Install + run the UI
cd ../LlmTrans.Ui
npm install
npm run dev
# → http://localhost:5173
```

The Vite dev server proxies `/admin/*` and `/healthz` to `http://localhost:5000`, so the UI
can call admin endpoints without CORS. In prod, serve `dist/` behind the same origin as the API
(e.g. via an nginx sidecar or `app.UseStaticFiles`).

## Pages shipped in M7

- `/dashboard` — counts + health
- `/routes` — LLM routes CRUD + issue route tokens
- `/mcp` — MCP servers CRUD + config snippet generator (Claude Desktop / Cursor / Zed / VSCode / Continue / raw)
- `/mcp/catalog` — browsable curated catalog
- `/glossaries` — list only
- `/style-rules` — list only
- `/settings` — tenants + health

## Parked for later milestones

- Glossary entry editor (TBX/CSV import-export) — M7 stretch
- Style rule editor with custom-instruction form (max 10 × 300 chars) — M7 stretch
- Proxy rule visual editor (Monaco + visual form) — M7 stretch
- Playground (4-pane before/after) — M7
- Logs + trace viewer — M7
- Generic route wizard (paste sample request+response → pick fields) — M7
- Auth / OIDC integration — M7
- Full Vuetify0 migration — the current component layer uses UnoCSS shortcuts and is thin enough
  that swapping to Vuetify0 (or Radix-Vue, Headless UI) is mechanical per ADR §12.0.
