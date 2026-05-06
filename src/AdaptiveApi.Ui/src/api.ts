import type {
  Glossary, McpCatalogEntry, McpServer, PiiPack, PiiRule, PiiRuleFlags,
  PiiTestRequest, PiiTestResponse, ProxyRule, Route, StyleRule, Tenant,
} from './types';

class ApiError extends Error {
  constructor(public status: number, public body: unknown, message: string) {
    super(message);
  }
}

/// Map a thrown exception (typically `ApiError` from a fetch failure) into a
/// short sentence the user can act on. Falls back to the raw message in
/// `?debug=1` mode so the underlying problem is recoverable.
///
/// Goal is to stop dumping ProblemDetails / EF strings / stack traces into
/// form footers — every page used to do
/// `e instanceof Error ? e.message : String(e)`, which leaks backend
/// internals. Use `friendlyError(e)` everywhere instead.
export function friendlyError(e: unknown): string {
  const debug = typeof window !== 'undefined' &&
    new URLSearchParams(window.location.search).has('debug');
  if (e instanceof ApiError) {
    if (debug) return `${e.status} ${e.message} ${JSON.stringify(e.body)}`;
    if (e.status === 401) return 'Sign-in required. Check your authentication settings.';
    if (e.status === 403) return 'You do not have permission to do that.';
    if (e.status === 404) return 'Not found. The item may have been deleted.';
    if (e.status === 409) return 'Conflict — that ID is already in use, or another change won the race.';
    if (e.status === 422 || e.status === 400) {
      const body = e.body as { message?: string; error?: string; detail?: string } | null;
      const detail = body?.message ?? body?.detail ?? body?.error;
      return detail ? `Invalid input: ${detail}` : 'Invalid input. Check the form fields.';
    }
    if (e.status >= 500) return 'The server failed processing this request. Try again or check the logs.';
    return e.message;
  }
  if (e instanceof Error) return debug ? e.stack ?? e.message : e.message;
  return String(e);
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(path, {
    method,
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const contentType = res.headers.get('content-type') ?? '';
  const payload: unknown = contentType.includes('application/json')
    ? await res.json().catch(() => null)
    : await res.text();
  if (!res.ok) {
    throw new ApiError(res.status, payload, `${method} ${path} → ${res.status}`);
  }
  return payload as T;
}

const get  = <T>(path: string) => request<T>('GET', path);
const post = <T>(path: string, body?: unknown) => request<T>('POST', path, body);
const patch = <T>(path: string, body?: unknown) => request<T>('PATCH', path, body);
const del   = <T>(path: string) => request<T>('DELETE', path);

export const api = {
  tenants: {
    list: () => get<Tenant[]>('/admin/tenants'),
    create: (id: string, name: string) => post<Tenant>('/admin/tenants', { id, name }),
  },
  routes: {
    list: () => get<Route[]>('/admin/routes'),
    create: (r: Partial<Route> & { id: string; tenantId: string; kind: string; upstreamBaseUrl: string }) =>
      post<Route>('/admin/routes', r),
    update: (id: string, r: Partial<Route>) => patch<Route>(`/admin/routes/${id}`, r),
    delete: (id: string) => del<void>(`/admin/routes/${id}`),
    issueToken: (id: string) => post<{ tokenId: string; plaintextToken: string }>(`/admin/routes/${id}/tokens`),
    listTokens: (id: string) => get<Array<{ id: string; prefix: string; createdAt: string; revokedAt: string | null }>>(
      `/admin/routes/${id}/tokens`),
  },
  mcp: {
    listServers: () => get<McpServer[]>('/admin/mcp/servers'),
    createServer: (body: Record<string, unknown>) =>
      post<{ server: McpServer; routeToken: string }>('/admin/mcp/servers', body),
    deleteServer: (id: string) => del<void>(`/admin/mcp/servers/${id}`),
    snippet: (id: string, client = 'claude-desktop') =>
      get<{ client: string; snippet: string }>(`/admin/mcp/servers/${id}/snippet?client=${client}`),
    listCatalog: () => get<McpCatalogEntry[]>('/admin/mcp/catalog'),
  },
  glossaries: {
    list: () => get<Glossary[]>('/admin/glossaries'),
    create: (body: { id: string; tenantId: string; name: string; deeplGlossaryId?: string | null }) =>
      post<Glossary>('/admin/glossaries', body),
    delete: (id: string) => del<void>(`/admin/glossaries/${id}`),
    listEntries: (id: string) => get<Array<{
      sourceLanguage: string; targetLanguage: string; sourceTerm: string; targetTerm: string;
      caseSensitive: boolean; doNotTranslate: boolean;
    }>>(`/admin/glossaries/${id}/entries`),
    addEntries: (id: string, entries: unknown[]) =>
      post<{ added: number }>(`/admin/glossaries/${id}/entries`, entries),
    importEntries: (id: string, body: {
      format: 'csv' | 'tbx';
      sourceLanguage: string;
      targetLanguage: string;
      body: string;
      caseSensitive?: boolean;
      doNotTranslate?: boolean;
    }) => post<{ added: number; skipped: number; errors: string[] }>(
      `/admin/glossaries/${id}/entries/import`, body),
  },
  styleRules: {
    list: () => get<StyleRule[]>('/admin/style-rules'),
    create: (body: unknown) => post<StyleRule>('/admin/style-rules', body),
    setInstructions: (id: string, instructions: Array<{ label: string; prompt: string; ordinal: number }>) =>
      post<{ count: number; version: number }>(`/admin/style-rules/${id}/instructions`, instructions),
    listInstructions: (id: string) => get<Array<{ label: string; prompt: string; ordinal: number }>>(
      `/admin/style-rules/${id}/instructions`),
  },
  requestRules: {
    list: (tenantId?: string) => get<RequestRule[]>(
      tenantId ? `/admin/request-rules?tenantId=${encodeURIComponent(tenantId)}` : '/admin/request-rules'),
    create: (body: Partial<RequestRule> & { id: string; tenantId: string; name: string; pattern: string; scope: string; action: string }) =>
      post<RequestRule>('/admin/request-rules', body),
    update: (id: string, body: Partial<RequestRule>) =>
      patch<RequestRule>(`/admin/request-rules/${id}`, body),
    delete: (id: string) => del<void>(`/admin/request-rules/${id}`),
    test: (body: { pattern: string; flagsJson?: string | null; sampleText: string; replacement?: string | null }) =>
      post<{ match: boolean; replaced: string | null; error: string | null }>('/admin/request-rules/test', body),
  },
  routeProfiles: {
    list: () => get<RouteProfile[]>('/admin/route-profiles'),
    create: (body: Partial<RouteProfile> & { id: string; tenantId: string; name: string }) =>
      post<RouteProfile>('/admin/route-profiles', body),
    update: (id: string, body: Partial<RouteProfile>) =>
      patch<RouteProfile>(`/admin/route-profiles/${id}`, body),
    delete: (id: string) => del<void>(`/admin/route-profiles/${id}`),
  },
  proxyRules: {
    list: () => get<ProxyRule[]>('/admin/proxy-rules'),
    create: (body: unknown) => post<ProxyRule>('/admin/proxy-rules', body),
    delete: (id: string) => del<void>(`/admin/proxy-rules/${id}`),
  },
  piiPacks: {
    list: () => get<PiiPack[]>('/admin/pii-packs'),
    get: (slug: string) => get<PiiPack>(`/admin/pii-packs/${slug}`),
  },
  piiRules: {
    list: (tenantId?: string) => get<PiiRule[]>(
      tenantId ? `/admin/pii-rules?tenantId=${encodeURIComponent(tenantId)}` : '/admin/pii-rules'),
    get: (id: string) => get<PiiRule>(`/admin/pii-rules/${id}`),
    create: (body: {
      id: string; tenantId: string; name: string; pattern: string; replacement: string;
      description?: string | null; flags?: PiiRuleFlags; enabled?: boolean;
    }) => post<PiiRule>('/admin/pii-rules', body),
    update: (id: string, body: Partial<{
      name: string; pattern: string; replacement: string;
      description: string | null; flags: PiiRuleFlags; enabled: boolean;
    }>) => patch<PiiRule>(`/admin/pii-rules/${id}`, body),
    delete: (id: string) => del<void>(`/admin/pii-rules/${id}`),
    test: (body: PiiTestRequest) => post<PiiTestResponse>('/admin/pii-rules/test', body),
  },
  logs: {
    list: (params: { tenantId?: string; routeId?: string; status?: number; before?: number; limit?: number } = {}) => {
      const q = new URLSearchParams();
      if (params.tenantId) q.set('tenantId', params.tenantId);
      if (params.routeId) q.set('routeId', params.routeId);
      if (params.status !== undefined) q.set('status', String(params.status));
      if (params.before !== undefined) q.set('before', String(params.before));
      if (params.limit !== undefined) q.set('limit', String(params.limit));
      const query = q.toString();
      return get<{ items: AuditEvent[]; nextBefore: number }>(
        query ? `/admin/logs?${query}` : '/admin/logs');
    },
  },
  system: {
    authStatus: () => get<{ mode: string; isOpen: boolean }>('/admin/system/auth-status'),
    readiness: () => get<{ checks: ReadinessCheck[] }>('/admin/system/readiness'),
    validateOidc: (body: { authority: string; clientId?: string; audience?: string }) =>
      post<OidcValidation>('/admin/system/oidc-validate', body),
  },
  secrets: {
    list: () => get<SecretList>('/admin/secrets'),
    set: (key: string, value: string) =>
      request<void>('PUT', `/admin/secrets/${encodeURIComponent(key)}`, { value }),
    delete: (key: string) => del<void>(`/admin/secrets/${encodeURIComponent(key)}`),
  },
  plugins: {
    list: () => get<PluginList>('/admin/plugins'),
    getSettings: (id: string) => get<{ settingsJson: string }>(`/admin/plugins/${id}/settings`),
    updateSettings: (id: string, settingsJson: string) =>
      request<void>('PUT', `/admin/plugins/${id}/settings`, { settingsJson }),
    enable: (id: string) => post<void>(`/admin/plugins/${id}/enable`),
    disable: (id: string) => post<void>(`/admin/plugins/${id}/disable`),
  },
  health: () => get<{ status: string }>('/healthz'),
};

export type RequestRule = {
  id: string;
  tenantId: string;
  routeId: string | null;
  name: string;
  description: string | null;
  scope: 'request-body' | 'response-body' | 'request-header' | 'response-header' | 'path';
  headerName: string | null;
  pattern: string;
  flagsJson: string | null;
  action: 'block' | 'replace' | 'set-header' | 'log';
  blockStatus: number | null;
  actionPayload: string | null;
  actionHeader: string | null;
  priority: number;
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
};

export type RouteProfile = {
  id: string;
  tenantId: string;
  name: string;
  description: string | null;
  glossaryId: string | null;
  requestStyleRuleId: string | null;
  responseStyleRuleId: string | null;
  proxyRuleId: string | null;
  createdAt: string;
  updatedAt: string;
};

export type SecretSummary = {
  key: string;
  updatedAt: string;
  keyVersion: number;
};

export type SecretList = {
  storeAvailable: boolean;
  secrets: SecretSummary[];
};

export type OidcValidation = {
  ok: boolean;
  issuer: string | null;
  authorizationEndpoint: string | null;
  tokenEndpoint: string | null;
  jwksUri: string | null;
  scopesSupported: string[];
  error: string | null;
};

export type ReadinessCheck = {
  id: string;
  name: string;
  severity: 'blocker' | 'warning' | 'info';
  status: 'ok' | 'warn' | 'error';
  detail: string | null;
};

export type PluginInfo = {
  id: string;
  name: string;
  version: string;
  description: string;
  category: string;
  hasSettings: boolean;
  hasEndpoints: boolean;
  dependencies: string[];
  allowAnonymousEndpoints: boolean;
  enabled: boolean;
  metrics: PluginMetrics | null;
};

export type PluginMetrics = {
  calls: number;
  failures: number;
  totalMs: number;
  avgMs: number;
  lastMs: number;
};

export type DisabledPlugin = {
  pluginId: string;
  version: string;
  reason: string;
};

export type PluginList = {
  loaded: PluginInfo[];
  disabled: DisabledPlugin[];
};

export type AuditEvent = {
  id: number;
  tenantId: string;
  routeId: string | null;
  method: string;
  path: string;
  status: number;
  userLanguage: string;
  llmLanguage: string;
  direction: string;
  translatorId: string | null;
  glossaryId: string | null;
  styleRuleId: string | null;
  requestChars: number;
  responseChars: number;
  integrityFailures: number;
  durationMs: number;
  createdAt: string;
};

export { ApiError };
