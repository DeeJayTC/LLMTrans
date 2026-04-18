import type { BridgeArgs } from './types.js';

export interface DoctorCheck {
  name: string;
  ok: boolean;
  detail: string;
  elapsedMs: number;
}

export interface DoctorResult {
  ok: boolean;
  checks: DoctorCheck[];
}

export interface DoctorIo {
  fetchFn?: typeof fetch;
  now?: () => number;
}

/// Runs a short battery of health checks so operators can confirm that their bridge
/// can reach adaptiveapi and that the route token resolves. Emits a structured result;
/// the CLI wrapper formats it for humans.
export async function runDoctor(args: BridgeArgs, io: DoctorIo = {}): Promise<DoctorResult> {
  const fetchFn = io.fetchFn ?? fetch;
  const now = io.now ?? (() => Date.now());
  const checks: DoctorCheck[] = [];

  // 1. /healthz reachable + returns 2xx.
  {
    const t0 = now();
    let ok = false;
    let detail = '';
    try {
      const resp = await fetchFn(`${args.endpoint}/healthz`);
      ok = resp.ok;
      detail = ok ? `HTTP ${resp.status}` : `HTTP ${resp.status} ${await resp.text().catch(() => '')}`;
    } catch (err) {
      detail = err instanceof Error ? err.message : String(err);
    }
    checks.push({ name: 'endpoint reachable', ok, detail, elapsedMs: now() - t0 });
  }

  // 2. Route token resolves — a canned tools/list request to the translate API.
  {
    const t0 = now();
    let ok = false;
    let detail = '';
    try {
      const url = `${args.endpoint}/mcp-translate/${encodeURIComponent(args.route)}`;
      const resp = await fetchFn(url, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          direction: 'server-to-client',
          message: { jsonrpc: '2.0', id: 1, result: { tools: [] } },
        }),
      });
      if (resp.status === 401) {
        detail = 'route token not recognized (401)';
      } else if (!resp.ok) {
        detail = `HTTP ${resp.status} ${await resp.text().catch(() => '')}`;
      } else {
        ok = true;
        detail = `HTTP ${resp.status}`;
      }
    } catch (err) {
      detail = err instanceof Error ? err.message : String(err);
    }
    checks.push({ name: 'route token resolves', ok, detail, elapsedMs: now() - t0 });
  }

  return { ok: checks.every((c) => c.ok), checks };
}

export function formatDoctorResult(r: DoctorResult): string {
  const lines: string[] = [];
  lines.push(r.ok ? 'adaptiveapi-mcp-bridge doctor: PASS' : 'adaptiveapi-mcp-bridge doctor: FAIL');
  for (const c of r.checks) {
    const mark = c.ok ? '✓' : '✗';
    lines.push(`  ${mark} ${c.name.padEnd(26)} ${c.elapsedMs.toString().padStart(5)}ms  ${c.detail}`);
  }
  return lines.join('\n');
}
