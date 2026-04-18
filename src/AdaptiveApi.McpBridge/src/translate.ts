import type { Writable } from 'node:stream';
import type { Direction, JsonRpcMessage, TranslateApiResponse } from './types.js';

export interface TranslateOptions {
  endpoint: string;
  route: string;
  timeoutMs: number;
  fetchFn?: typeof fetch;
  /// Optional sink for the single-line warnings the client emits on failure.
  /// Defaults to `process.stderr`; tests can inject a `PassThrough`.
  stderr?: Writable;
}

export class TranslateClient {
  private readonly stderr: Writable;

  constructor(private readonly options: TranslateOptions) {
    this.stderr = options.stderr ?? process.stderr;
  }

  async translate(direction: Direction, message: JsonRpcMessage): Promise<JsonRpcMessage> {
    const url = `${this.options.endpoint}/mcp-translate/${encodeURIComponent(this.options.route)}`;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), this.options.timeoutMs);
    try {
      const fetchFn = this.options.fetchFn ?? fetch;
      const resp = await fetchFn(url, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ direction, message }),
        signal: controller.signal,
      });
      if (!resp.ok) {
        // Fail-open: return the source message so the MCP client keeps working
        // even if adaptiveapi is temporarily unavailable.
        const body = await resp.text().catch(() => '');
        this.stderr.write(
          `[adaptiveapi-mcp-bridge] translate ${resp.status} — forwarding untranslated (${body.slice(0, 120)})\n`,
        );
        return message;
      }
      const data = (await resp.json()) as TranslateApiResponse;
      return data.message ?? message;
    } catch (err) {
      const reason = err instanceof Error ? err.message : String(err);
      this.stderr.write(
        `[adaptiveapi-mcp-bridge] translate failed — forwarding untranslated: ${reason}\n`,
      );
      return message;
    } finally {
      clearTimeout(timeout);
    }
  }
}
