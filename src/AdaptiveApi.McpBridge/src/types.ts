export type Direction = 'client-to-server' | 'server-to-client';

export interface JsonRpcMessage {
  jsonrpc: '2.0';
  id?: string | number | null;
  method?: string;
  params?: unknown;
  result?: unknown;
  error?: unknown;
  [key: string]: unknown;
}

export interface TranslateApiResponse {
  message: JsonRpcMessage;
  diagnostics?: {
    chars?: number;
    cacheHit?: boolean;
    translator?: string;
  };
}

export interface BridgeArgs {
  mode: 'bridge' | 'doctor' | 'help' | 'version';
  route: string;
  endpoint: string;
  passthrough: boolean;
  logPath?: string;
  requestTimeoutMs: number;
  upstreamCmd?: string;
  upstreamArgs: string[];
}
