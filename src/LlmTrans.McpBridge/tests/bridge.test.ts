import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { describe, expect, it } from 'vitest';
import { runBridge } from '../src/bridge.js';
import { TranslateClient } from '../src/translate.js';
import type { BridgeArgs, JsonRpcMessage } from '../src/types.js';

/// Minimal fake that looks enough like `ChildProcessWithoutNullStreams` for the bridge.
class FakeUpstream extends EventEmitter {
  stdin = new PassThrough();
  stdout = new PassThrough();
  stderr = new PassThrough();

  kill() { /* no-op */ }
}

function makeArgs(overrides: Partial<BridgeArgs> = {}): BridgeArgs {
  return {
    mode: 'bridge',
    route: 'rt_test',
    endpoint: 'http://translate.local',
    passthrough: false,
    requestTimeoutMs: 1000,
    upstreamCmd: 'fake-upstream',
    upstreamArgs: [],
    ...overrides,
  };
}

describe('runBridge', () => {
  it('forwards client-to-server messages through the translate API', async () => {
    const seen: Array<{ url: string; body: unknown }> = [];
    const translator = new TranslateClient({
      endpoint: 'http://translate.local',
      route: 'rt_test',
      timeoutMs: 1000,
      fetchFn: async (url, init) => {
        seen.push({ url: String(url), body: init && init.body ? JSON.parse(String(init.body)) : null });
        const request = init && init.body ? JSON.parse(String(init.body)) : { message: {} };
        const msg = request.message as JsonRpcMessage;
        // "translator" echoes back with an extra params field so we can assert it ran.
        return new Response(JSON.stringify({
          message: { ...msg, params: { ...(msg.params as object ?? {}), translated: true } },
        }), { status: 200, headers: { 'content-type': 'application/json' } });
      },
    });

    const clientIn = new PassThrough();
    const clientOut = new PassThrough();
    const stderr = new PassThrough();
    const upstream = new FakeUpstream();

    const bridgeExit = runBridge(makeArgs(), {
      clientIn, clientOut, stderr,
      translator,
      spawnUpstream: () => upstream as unknown as import('node:child_process').ChildProcessWithoutNullStreams,
    });

    // Collect upstream stdin writes.
    const upstreamBytes: Buffer[] = [];
    upstream.stdin.on('data', (c: Buffer) => upstreamBytes.push(c));

    // Client sends a tools/call message.
    clientIn.write(JSON.stringify({
      jsonrpc: '2.0', id: 1, method: 'tools/call',
      params: { name: 'search', arguments: { query: 'hello' } },
    }) + '\n');

    // Give the event loop time to flush the stream + fetch + write.
    await new Promise((r) => setTimeout(r, 30));

    // Close everything so runBridge's exit promise can settle.
    clientIn.end();
    upstream.emit('exit', 0, null);
    await bridgeExit;

    const forwarded = Buffer.concat(upstreamBytes).toString('utf8').trim();
    const parsed = JSON.parse(forwarded) as JsonRpcMessage;
    expect((parsed.params as { translated: boolean }).translated).toBe(true);
    expect(parsed.method).toBe('tools/call');
    expect(seen).toHaveLength(1);
    expect(seen[0]!.url).toContain('/mcp-translate/rt_test');
    expect((seen[0]!.body as { direction: string }).direction).toBe('client-to-server');
  });

  it('passthrough mode does not call the translate API', async () => {
    let called = 0;
    const translator = new TranslateClient({
      endpoint: 'http://translate.local', route: 'rt_test', timeoutMs: 1000,
      fetchFn: async () => { called++; return new Response('', { status: 200 }); },
    });

    const clientIn = new PassThrough();
    const clientOut = new PassThrough();
    const stderr = new PassThrough();
    const upstream = new FakeUpstream();

    const bridgeExit = runBridge(makeArgs({ passthrough: true }), {
      clientIn, clientOut, stderr, translator,
      spawnUpstream: () => upstream as unknown as import('node:child_process').ChildProcessWithoutNullStreams,
    });

    const collected: Buffer[] = [];
    upstream.stdin.on('data', (c: Buffer) => collected.push(c));

    clientIn.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'ping' }) + '\n');
    await new Promise((r) => setTimeout(r, 20));
    clientIn.end();
    upstream.emit('exit', 0, null);
    await bridgeExit;

    expect(called).toBe(0);
    expect(Buffer.concat(collected).toString('utf8')).toContain('"method":"ping"');
  });

  it('translate API failure is logged and the untranslated message still forwards', async () => {
    const clientIn = new PassThrough();
    const clientOut = new PassThrough();
    const stderr = new PassThrough();
    const upstream = new FakeUpstream();

    const stderrBytes: Buffer[] = [];
    stderr.on('data', (c: Buffer) => stderrBytes.push(c));

    const translator = new TranslateClient({
      endpoint: 'http://translate.local', route: 'rt_test', timeoutMs: 1000,
      fetchFn: async () => new Response('oops', { status: 500 }),
      stderr,
    });

    const bridgeExit = runBridge(makeArgs(), {
      clientIn, clientOut, stderr, translator,
      spawnUpstream: () => upstream as unknown as import('node:child_process').ChildProcessWithoutNullStreams,
    });

    const forwarded: Buffer[] = [];
    upstream.stdin.on('data', (c: Buffer) => forwarded.push(c));

    clientIn.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/list' }) + '\n');
    await new Promise((r) => setTimeout(r, 30));
    clientIn.end();
    upstream.emit('exit', 0, null);
    await bridgeExit;

    expect(Buffer.concat(forwarded).toString('utf8')).toContain('"method":"tools/list"');
    expect(Buffer.concat(stderrBytes).toString('utf8')).toMatch(/translate 500/);
  });
});
