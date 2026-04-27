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
  redactPii?: boolean;
  systemContext?: string | null;
  piiPackSlugsJson?: string | null;
  piiRuleIdsJson?: string | null;
  piiDisabledDetectorsJson?: string | null;
};

export type PiiDetectorSpec = {
  kind: string;
  pattern: string;
  replacement: string;
  flags?: string[];
  luhnValidate: boolean;
};

export type PiiPack = {
  slug: string;
  name: string;
  description: string;
  isBuiltin: boolean;
  ordinal: number;
  detectors: PiiDetectorSpec[];
};

export type PiiRuleFlags = {
  caseInsensitive: boolean;
  multiline: boolean;
  luhnValidate: boolean;
};

export type PiiRule = {
  id: string;
  tenantId: string;
  name: string;
  description: string | null;
  pattern: string;
  replacement: string;
  flags: PiiRuleFlags;
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
};

export type PiiTestRequest = {
  text: string;
  packSlugs?: string[];
  ruleIds?: string[];
  disabledDetectors?: string[];
  adHocPattern?: string;
  adHocReplacement?: string;
  adHocKind?: string;
  adHocFlags?: PiiRuleFlags;
  tenantId?: string;
};

export type PiiTestMatch = {
  kind: string;
  replacement: string;
  start: number;
  length: number;
  match: string;
};

export type PiiTestResponse = {
  redactedText: string;
  matches: PiiTestMatch[];
  errors: string[];
};
