export type Tenant = { id: string; name: string; createdAt: string };

export type RouteKind =
  | 'OpenAiChat'
  | 'OpenAiResponses'
  | 'AnthropicMessages'
  | 'Mcp'
  | 'McpTranslate'
  | 'Generic';

export type Route = {
  id: string;
  tenantId: string;
  kind: RouteKind;
  upstreamBaseUrl: string;
  userLanguage: string;
  llmLanguage: string;
  direction: 'Off' | 'Bidirectional' | 'RequestOnly' | 'ResponseOnly';
  translatorId: string | null;
  glossaryId: string | null;
  /** Style rule applied to user → LLM (request) translations. */
  requestStyleRuleId: string | null;
  /** Style rule applied to LLM → user (response) translations. */
  responseStyleRuleId: string | null;
  proxyRuleId: string | null;
  configJson: string | null;
};

export type McpServer = {
  id: string;
  tenantId: string;
  name: string;
  transport: 'remote' | 'stdio-local';
  remoteUpstreamUrl: string | null;
  userLanguage: string;
  llmLanguage: string;
  translatorId: string | null;
  glossaryId: string | null;
  styleRuleId: string | null;
  proxyRuleId: string | null;
  catalogEntryId: string | null;
  createdAt: string;
  disabledAt: string | null;
};

export type McpCatalogEntry = {
  id: string;
  slug: string;
  displayName: string;
  description: string;
  transport: 'remote' | 'stdio-local';
  upstreamUrl: string | null;
  upstreamCommandHint: string | null;
  docsUrl: string | null;
  iconUrl: string | null;
  publisher: string;
  verified: boolean;
};

export type Glossary = {
  id: string;
  tenantId: string;
  name: string;
  deeplGlossaryId: string | null;
  createdAt: string;
  updatedAt: string;
};

export type StyleRule = {
  id: string;
  tenantId: string;
  name: string;
  language: string;
  deeplStyleId: string | null;
  version: number;
};

export type ProxyRule = {
  id: string;
  tenantId: string;
  name: string;
  scopeJson: string;
  allowlistJson: string | null;
  denylistJson: string | null;
  formality: string | null;
  priority: number;
};
