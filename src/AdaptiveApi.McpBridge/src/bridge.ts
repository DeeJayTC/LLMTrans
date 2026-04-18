import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { createInterface, type Interface as ReadlineInterface } from 'node:readline';
import type { Readable, Writable } from 'node:stream';
import type { FileHandle } from 'node:fs/promises';
import type { BridgeArgs, Direction, JsonRpcMessage } from './types.js';
import { TranslateClient } from './translate.js';
import * as fs from 'node:fs/promises';

export interface BridgeIo {
  clientIn: Readable;
  clientOut: Writable;
  stderr: Writable;
  spawnUpstream: (cmd: string, args: readonly string[]) => ChildProcessWithoutNullStreams;
  translator: TranslateClient;
  now?: () => number;
  openLog?: (path: string) => Promise<FileHandle>;
}

/// Bridges an MCP client's stdio to an upstream MCP server spawned as a child process,
/// translating each JSON-RPC message in flight via adaptiveapi's stateless translate API.
///
/// Resolution order for a single message, client → server (and mirrored for server → client):
///   1. Read a newline-terminated JSON-RPC message from the client.
///   2. Parse. If parsing fails, forward verbatim — adaptiveapi's contract is JSON-RPC only.
///   3. Either skip translation (passthrough) or call translate API with direction.
///   4. Write the (possibly translated) message + newline to the upstream's stdin.
///   5. Log duration + size to the optional log file. Body content is never logged.
export async function runBridge(args: BridgeArgs, io: BridgeIo): Promise<number> {
  if (!args.upstreamCmd) throw new Error('runBridge requires upstreamCmd');

  const now = io.now ?? (() => Date.now());
  const log = args.logPath ? await (io.openLog ?? defaultOpenLog)(args.logPath) : undefined;

  const upstream = io.spawnUpstream(args.upstreamCmd, args.upstreamArgs);

  let shuttingDown = false;
  const shutdown = async (code: number) => {
    if (shuttingDown) return;
    shuttingDown = true;
    try { upstream.kill(); } catch { /* ignore */ }
    try { await log?.close(); } catch { /* ignore */ }
    return code;
  };

  upstream.on('error', (err) => {
    io.stderr.write(`[adaptiveapi-mcp-bridge] failed to spawn upstream: ${err.message}\n`);
  });

  const exitPromise = new Promise<number>((resolve) => {
    upstream.on('exit', async (code, signal) => {
      await shutdown(code ?? (signal ? 128 : 0));
      resolve(code ?? (signal ? 128 : 0));
    });
  });

  pumpStdio(io.clientIn, upstream.stdin, 'client-to-server', args, io, now, log);
  pumpStdio(upstream.stdout, io.clientOut, 'server-to-client', args, io, now, log);

  // Propagate upstream stderr to our stderr so operators can see what their MCP server logs.
  upstream.stderr.on('data', (chunk: Buffer) => io.stderr.write(chunk));

  return exitPromise;
}

function pumpStdio(
  input: Readable,
  output: Writable,
  direction: Direction,
  args: BridgeArgs,
  io: BridgeIo,
  now: () => number,
  log: FileHandle | undefined,
) {
  const reader = createInterface({ input });
  reader.on('line', (line) => {
    void handleLine(line, direction, args, io, output, now, log);
  });
  reader.on('close', () => {
    // Half-close the downstream write end so the peer sees EOF.
    try { output.end(); } catch { /* ignore */ }
  });
  input.on('error', (err) => {
    io.stderr.write(`[adaptiveapi-mcp-bridge] ${direction} read error: ${err.message}\n`);
  });
}

async function handleLine(
  line: string,
  direction: Direction,
  args: BridgeArgs,
  io: BridgeIo,
  output: Writable,
  now: () => number,
  log: FileHandle | undefined,
): Promise<void> {
  if (line.length === 0) return;

  const started = now();
  let message: JsonRpcMessage | null = null;
  try { message = JSON.parse(line) as JsonRpcMessage; } catch { message = null; }

  if (message === null) {
    // Non-JSON line (probably a log message that escaped the stderr channel).
    // Forward untouched so we never eat protocol data on a parse mistake.
    output.write(line + '\n');
    return;
  }

  const translated = args.passthrough
    ? message
    : await io.translator.translate(direction, message);

  output.write(JSON.stringify(translated) + '\n');

  if (log) {
    const elapsed = now() - started;
    const record = {
      ts: new Date().toISOString(),
      direction,
      method: typeof message.method === 'string' ? message.method : undefined,
      id: message.id ?? null,
      elapsedMs: elapsed,
      bytes: line.length,
      passthrough: args.passthrough,
    };
    try { await log.write(JSON.stringify(record) + '\n'); } catch { /* ignore */ }
  }
}

async function defaultOpenLog(path: string): Promise<FileHandle> {
  return fs.open(path, 'a');
}
