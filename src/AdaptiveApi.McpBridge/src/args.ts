import type { BridgeArgs } from './types.js';

export class ArgsError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ArgsError';
  }
}

const DEFAULT_ENDPOINT = 'https://api.adaptiveapi.example.com';
const DEFAULT_TIMEOUT_MS = 15000;

export function parseArgs(argv: readonly string[]): BridgeArgs {
  const first = argv[0];
  if (first === '--help' || first === '-h' || first === 'help') {
    return blankArgs({ mode: 'help' });
  }
  if (first === '--version' || first === '-v') {
    return blankArgs({ mode: 'version' });
  }

  const mode: BridgeArgs['mode'] = first === 'doctor' ? 'doctor' : 'bridge';
  const start = mode === 'doctor' ? 1 : 0;

  const args = blankArgs({ mode });
  let sawUpstreamSeparator = false;

  for (let i = start; i < argv.length; i++) {
    const token = argv[i]!;

    if (token === '--') {
      sawUpstreamSeparator = true;
      const rest = argv.slice(i + 1);
      args.upstreamCmd = rest[0];
      args.upstreamArgs = rest.slice(1);
      break;
    }

    switch (token) {
      case '--route':
      case '-r':
        args.route = requireNext(argv, ++i, token);
        break;
      case '--endpoint':
      case '-e':
        args.endpoint = stripTrailingSlash(requireNext(argv, ++i, token));
        break;
      case '--passthrough':
        args.passthrough = true;
        break;
      case '--log':
        args.logPath = requireNext(argv, ++i, token);
        break;
      case '--timeout':
        args.requestTimeoutMs = parseMsFlag(requireNext(argv, ++i, token));
        break;
      default:
        throw new ArgsError(`unknown flag: ${token}`);
    }
  }

  if (mode === 'bridge') {
    if (!sawUpstreamSeparator || !args.upstreamCmd) {
      throw new ArgsError(
        'missing upstream command. Use `-- <cmd> <args...>` to specify the MCP server to spawn.',
      );
    }
  }

  if (!args.route) {
    throw new ArgsError('missing --route (your adaptiveapi route token)');
  }

  return args;
}

function blankArgs(seed: Partial<BridgeArgs>): BridgeArgs {
  return {
    mode: 'bridge',
    route: '',
    endpoint: DEFAULT_ENDPOINT,
    passthrough: false,
    requestTimeoutMs: DEFAULT_TIMEOUT_MS,
    upstreamArgs: [],
    ...seed,
  };
}

function requireNext(argv: readonly string[], i: number, flag: string): string {
  const v = argv[i];
  if (v === undefined || v.startsWith('--') || v === '-') {
    throw new ArgsError(`${flag} expects a value`);
  }
  return v;
}

function parseMsFlag(raw: string): number {
  const n = Number.parseInt(raw, 10);
  if (!Number.isFinite(n) || n < 100) {
    throw new ArgsError(`--timeout must be ≥ 100ms (got ${raw})`);
  }
  return n;
}

function stripTrailingSlash(v: string): string {
  return v.endsWith('/') ? v.slice(0, -1) : v;
}

export const helpText = `
@adaptiveapi/mcp-bridge — local stdio ↔ adaptiveapi translate-API bridge

USAGE
  adaptiveapi-mcp-bridge --route <token> [--endpoint <url>] [--passthrough] [--log <path>] -- <upstream-command> [args...]
  adaptiveapi-mcp-bridge doctor --route <token> [--endpoint <url>]

FLAGS
  --route, -r       Your adaptiveapi route token (required).
  --endpoint, -e    Base URL of the adaptiveapi API. Default: ${DEFAULT_ENDPOINT}
  --passthrough     Skip translation calls; forward stdio verbatim. Debugging aid.
  --log <path>      Write per-message timings (never body content) to this file.
  --timeout <ms>    Translate-API call timeout in ms. Default: ${DEFAULT_TIMEOUT_MS}
  --help, -h        Show this help.
  --version, -v     Print version and exit.

EXAMPLE
  Existing Claude Desktop config for the GitHub MCP server:
    {
      "mcpServers": {
        "github": {
          "command": "npx",
          "args": ["-y", "@modelcontextprotocol/server-github"],
          "env": { "GITHUB_TOKEN": "ghp_..." }
        }
      }
    }

  Wrap it with adaptiveapi-mcp-bridge — env is inherited verbatim, no change:
    {
      "mcpServers": {
        "github-de": {
          "command": "npx",
          "args": [
            "-y", "@adaptiveapi/mcp-bridge",
            "--route", "rt_your_token",
            "--endpoint", "https://api.adaptiveapi.example.com",
            "--",
            "npx", "-y", "@modelcontextprotocol/server-github"
          ],
          "env": { "GITHUB_TOKEN": "ghp_..." }
        }
      }
    }

  Your GITHUB_TOKEN stays on your machine. The bridge spawns the GitHub MCP server
  as a child and only JSON-RPC message bodies pass through adaptiveapi for translation.
`;
