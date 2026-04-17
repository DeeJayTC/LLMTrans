# llmtrans-mcp-bridge (Go)

Static-binary port of [`@llmtrans/mcp-bridge`](../LlmTrans.McpBridge/). Behavior parity
with the Node CLI — same flags, same `doctor` subcommand, same fail-open semantics —
minus any Node / npm dependency.

## Build

```bash
# single host binary
go build -o llmtrans-mcp-bridge ./...

# minimal cross-platform release
GOOS=linux   GOARCH=amd64  go build -trimpath -ldflags "-s -w" -o dist/llmtrans-mcp-bridge-linux-amd64  ./...
GOOS=linux   GOARCH=arm64  go build -trimpath -ldflags "-s -w" -o dist/llmtrans-mcp-bridge-linux-arm64  ./...
GOOS=darwin  GOARCH=arm64  go build -trimpath -ldflags "-s -w" -o dist/llmtrans-mcp-bridge-darwin-arm64 ./...
GOOS=windows GOARCH=amd64  go build -trimpath -ldflags "-s -w" -o dist/llmtrans-mcp-bridge.exe           ./...
```

The typical release is 3–4 MB per platform, fully static, no libc dependencies.

## Use

Identical to the Node version:

```bash
llmtrans-mcp-bridge --route rt_abc --endpoint https://api.llmtrans.example.com \
  -- npx -y @modelcontextprotocol/server-github
```

MCP client configs can reference the binary path directly:

```jsonc
{
  "mcpServers": {
    "github-de": {
      "command": "/usr/local/bin/llmtrans-mcp-bridge",
      "args": [
        "--route", "rt_your_token",
        "--endpoint", "https://api.llmtrans.example.com",
        "--",
        "npx", "-y", "@modelcontextprotocol/server-github"
      ],
      "env": { "GITHUB_TOKEN": "ghp_..." }
    }
  }
}
```

## When to pick this over Node

- You ship MCP servers to non-developer machines where npm isn't available.
- Cold start matters and the Node runtime is too slow.
- You already use a Go-heavy toolchain and want one less language in play.

The Node version remains canonical for `npx @llmtrans/mcp-bridge` (zero-install)
distribution.
