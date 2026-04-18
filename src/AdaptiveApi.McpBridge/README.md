# `@adaptiveapi/mcp-bridge`

Local stdio ↔ adaptiveapi translate-API bridge for MCP clients (Claude Desktop, Cursor,
Zed, VS Code + Continue, …).

## What it does

Your MCP client already knows how to spawn the stdio MCP server it wants and set the
right env vars. `@adaptiveapi/mcp-bridge` sits between the two, translating every
JSON-RPC message in flight:

```
 MCP client  ──stdin──▶  bridge  ──stdin──▶  MCP server (subprocess, your env)
 MCP client  ◀─stdout─   bridge  ◀─stdout─   MCP server
                           │
                           ▼
                     adaptiveapi /mcp-translate
                     (translates message bodies only;
                      never sees your credentials)
```

The upstream MCP server is spawned as a child of the bridge with **your inherited
env** — `GITHUB_TOKEN`, `SLACK_TOKEN`, database URLs, whatever it needs — and none of
those values cross our boundary. Only the JSON-RPC message bodies flow through
adaptiveapi for translation.

## Install

Usually you don't install it; MCP clients run `npx @adaptiveapi/mcp-bridge …` on demand.

## Config

Take your existing MCP client config and wrap the `command` with the bridge. The
`env` block stays exactly as it was.

### Before (GitHub MCP in Claude Desktop)

```jsonc
{
  "mcpServers": {
    "github": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-github"],
      "env": { "GITHUB_TOKEN": "ghp_..." }
    }
  }
}
```

### After (bridged through adaptiveapi)

```jsonc
{
  "mcpServers": {
    "github-de": {
      "command": "npx",
      "args": [
        "-y", "@adaptiveapi/mcp-bridge",
        "--route", "rt_your_tenant_token",
        "--endpoint", "https://api.adaptiveapi.example.com",
        "--",
        "npx", "-y", "@modelcontextprotocol/server-github"
      ],
      "env": { "GITHUB_TOKEN": "ghp_..." }
    }
  }
}
```

`GITHUB_TOKEN` stays on your machine. Ask the GitHub MCP "liste alle offenen Pull
Requests" in German — the bridge sends your question to adaptiveapi, adaptiveapi
translates it to English, forwards it to the GitHub MCP, translates the English
response back to German, and your client sees German output.

## Flags

| Flag | Default | Purpose |
| --- | --- | --- |
| `--route` / `-r` | *required* | Your adaptiveapi route token. |
| `--endpoint` / `-e` | `https://api.adaptiveapi.example.com` | Base URL of adaptiveapi. |
| `--passthrough` | off | Skip translation — forward stdio verbatim. Debug aid to isolate bridge-vs-translation issues. |
| `--log <path>` | none | Append per-message timings (duration, direction, method, bytes) to a file. **Never logs body content.** |
| `--timeout <ms>` | `15000` | Translate API call timeout. Falls through with source text on failure. |

## `doctor` subcommand

Before committing your client config, verify the bridge can reach adaptiveapi and
that your route token resolves:

```bash
npx @adaptiveapi/mcp-bridge doctor --route rt_your_token --endpoint https://api.adaptiveapi.example.com
```

Sample output:

```
adaptiveapi-mcp-bridge doctor: PASS
  ✓ endpoint reachable           23ms  HTTP 200
  ✓ route token resolves         41ms  HTTP 200
```

Failure cases: endpoint not reachable (DNS / firewall / typo), route token
invalid (401), or the tenant is disabled.

## Failure mode — fail open

If adaptiveapi is temporarily unreachable or returns a non-2xx response, the bridge
**forwards the untranslated message** and logs a one-line warning to stderr. Your
MCP server keeps working; you just won't get translation until adaptiveapi is back.

If the upstream subprocess crashes or exits, the bridge exits with the same code
so your MCP client's retry logic stays intact.

## Development

```bash
npm install
npm run dev -- --route rt_x --endpoint http://localhost:8080 -- echo '{"jsonrpc":"2.0","id":1,"result":{}}'
npm run build          # → dist/cli.js (single esbuild bundle, #!/usr/bin/env node)
npm test               # vitest
npm run typecheck
```

## License

MIT.
