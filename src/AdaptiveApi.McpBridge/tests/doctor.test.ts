import { describe, expect, it } from 'vitest';
import { formatDoctorResult, runDoctor } from '../src/doctor.js';

function fakeFetch(handler: (url: string, init?: RequestInit) => Response | Promise<Response>): typeof fetch {
  return (async (input: Request | URL | string, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString();
    return handler(url, init);
  }) as typeof fetch;
}

describe('runDoctor', () => {
  it('reports PASS when both endpoint and route resolve', async () => {
    const fetchFn = fakeFetch((url) => {
      if (url.endsWith('/healthz')) return new Response('{"status":"ok"}', { status: 200 });
      if (url.includes('/mcp-translate/')) return new Response('{"message":{}}', { status: 200 });
      return new Response('not found', { status: 404 });
    });
    const result = await runDoctor({
      mode: 'doctor', route: 'rt_ok', endpoint: 'http://x',
      passthrough: false, requestTimeoutMs: 1000, upstreamArgs: [],
    }, { fetchFn });

    expect(result.ok).toBe(true);
    expect(result.checks.map((c) => c.name)).toEqual([
      'endpoint reachable', 'route token resolves',
    ]);
  });

  it('reports FAIL when route token is rejected', async () => {
    const fetchFn = fakeFetch((url) => {
      if (url.endsWith('/healthz')) return new Response('{"status":"ok"}', { status: 200 });
      return new Response('{"error":{"type":"invalid_route_token"}}', { status: 401 });
    });
    const result = await runDoctor({
      mode: 'doctor', route: 'rt_bad', endpoint: 'http://x',
      passthrough: false, requestTimeoutMs: 1000, upstreamArgs: [],
    }, { fetchFn });

    expect(result.ok).toBe(false);
    expect(result.checks[1]!.detail).toContain('401');
  });

  it('endpoint unreachable is a FAIL not a throw', async () => {
    const fetchFn = fakeFetch(() => { throw new TypeError('fetch failed'); });
    const result = await runDoctor({
      mode: 'doctor', route: 'rt', endpoint: 'http://offline',
      passthrough: false, requestTimeoutMs: 1000, upstreamArgs: [],
    }, { fetchFn });

    expect(result.ok).toBe(false);
    expect(result.checks[0]!.ok).toBe(false);
  });

  it('formatDoctorResult renders all checks', async () => {
    const r = {
      ok: false,
      checks: [
        { name: 'endpoint reachable', ok: true,  detail: 'HTTP 200', elapsedMs: 12 },
        { name: 'route token resolves', ok: false, detail: 'HTTP 401', elapsedMs: 7 },
      ],
    };
    const text = formatDoctorResult(r);
    expect(text).toContain('FAIL');
    expect(text).toContain('endpoint reachable');
    expect(text).toContain('route token resolves');
    expect(text).toContain('HTTP 401');
  });
});
