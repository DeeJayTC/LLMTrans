using LlmTrans.Infrastructure.Persistence;

namespace LlmTrans.Api.Admin;

/// Builds the copy-paste config snippet the user drops into their MCP client (§2.5.5).
/// For remote servers: a `url` entry. For stdio-local: the bridge-wrapped `command + args`
/// form where the user fills in their existing upstream command verbatim.
public static class McpSnippetBuilder
{
    public static string Build(McpServerEntity server, string publicBaseUrl, string routeToken, string clientKind)
    {
        return clientKind switch
        {
            "claude-desktop" or "cursor" or "zed" or "vscode" or "continue" or "raw" => RenderJson(server, publicBaseUrl, routeToken),
            _ => RenderJson(server, publicBaseUrl, routeToken),
        };
    }

    private static string RenderJson(McpServerEntity server, string publicBaseUrl, string routeToken)
    {
        var keyName = SanitizeKey(server.Name);
        if (server.Transport == "remote")
        {
            return $$"""
            {
              "mcpServers": {
                "{{keyName}}": {
                  "url": "{{publicBaseUrl}}/mcp/{{routeToken}}",
                  "headers": {
                    "Authorization": "Bearer <your-upstream-token-here>"
                  }
                }
              }
            }
            """;
        }

        // stdio-local — wrap the user's existing command with the bridge.
        return $$"""
        {
          "mcpServers": {
            "{{keyName}}": {
              "command": "npx",
              "args": [
                "-y", "@llmtrans/mcp-bridge",
                "--route", "{{routeToken}}",
                "--endpoint", "{{publicBaseUrl}}",
                "--",
                "<your existing command>",
                "<your existing args>"
              ],
              "env": {
                "<YOUR_EXISTING_ENV_KEY>": "<YOUR_EXISTING_ENV_VALUE>"
              }
            }
          }
        }
        """;
    }

    private static string SanitizeKey(string name)
    {
        var cleaned = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) cleaned.Append(c);
            else if (c is ' ' or '-' or '_') cleaned.Append('-');
        }
        return cleaned.Length == 0 ? "mcp-server" : cleaned.ToString();
    }
}
