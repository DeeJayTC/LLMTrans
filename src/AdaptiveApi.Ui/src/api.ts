import type {
  Glossary, McpCatalogEntry, McpServer, PiiPack, PiiRule, PiiRuleFlags,
  PiiTestRequest, PiiTestResponse, ProxyRule, Route, StyleRule, Tenant,
} from './types';

class ApiError extends Error {
  constructor(public status: number, public body: unknown, message: string) {
    super(message);
  }
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
  },
  styleRules: {
    list: () => get<StyleRule[]>('/admin/style-rules'),
    create: (body: unknown) => post<StyleRule>('/admin/style-rules', body),
    setInstructions: (id: string, instructions: Array<{ label: string; prompt: string; ordinal: number }>) =>
      post<{ count: number; version: number }>(`/admin/style-rules/${id}/instructions`, instructions),
    listInstructions: (id: string) => get<Array<{ label: string; prompt: string; ordinal: number }>>(
      `/admin/style-rules/${id}/instructions`),
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
  health: () => get<{ status: string }>('/healthz'),
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
