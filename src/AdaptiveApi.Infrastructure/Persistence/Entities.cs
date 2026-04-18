namespace AdaptiveApi.Infrastructure.Persistence;

public sealed class TenantEntity
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RouteEntity
{
    public string Id { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string Kind { get; set; } = default!;
    public string UpstreamBaseUrl { get; set; } = default!;
    public string UserLanguage { get; set; } = "en";
    public string LlmLanguage { get; set; } = "en";
    public string Direction { get; set; } = "Off";
    public string? TranslatorId { get; set; }
    public string? GlossaryId { get; set; }
    /// Style rule applied to the user → LLM (request) direction. Usually a rule that
    /// normalises English for the model.
    public string? RequestStyleRuleId { get; set; }
    /// Style rule applied to the LLM → user (response) direction. Usually the brand
    /// voice / tone used when talking to the human.
    public string? ResponseStyleRuleId { get; set; }
    public string? ProxyRuleId { get; set; }
    /// Kind-specific declarative config. For Generic routes: JSONPath translation spec
    /// (§2.4). For other kinds: null.
    public string? ConfigJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RouteTokenEntity
{
    public string Id { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    /// Identifier of the target row. Meaning depends on `Scope`:
    /// `route` → routes.id, `mcp-server` → mcp_servers.id.
    public string RouteId { get; set; } = default!;
    public string Scope { get; set; } = "route";
    public string Prefix { get; set; } = default!;
    public string Hash { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class GlossaryEntity
{
    public string Id { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? DeeplGlossaryId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class GlossaryEntryEntity
{
    public string Id { get; set; } = default!;
    public string GlossaryId { get; set; } = default!;
    public string SourceLanguage { get; set; } = default!;
    public string TargetLanguage { get; set; } = default!;
    public string SourceTerm { get; set; } = default!;
    public string TargetTerm { get; set; } = default!;
    public bool CaseSensitive { get; set; }
    public bool DoNotTranslate { get; set; }
}

public sealed class StyleRuleEntity
{
    public string Id { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Language { get; set; } = default!;
    public string? DeeplStyleId { get; set; }
    public string RulesJson { get; set; } = "{}";
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CustomInstructionEntity
{
    public string Id { get; set; } = default!;
    public string StyleRuleId { get; set; } = default!;
    public string Label { get; set; } = default!;
    public string Prompt { get; set; } = default!;
    public int Ordinal { get; set; }
}

public sealed class ProxyRuleEntity
{
    public string Id { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string ScopeJson { get; set; } = "{}";
    public string? AllowlistJson { get; set; }
    public string? DenylistJson { get; set; }
    public string? PlaceholderPatternsJson { get; set; }
    public string? Formality { get; set; }
    public int Priority { get; set; }
    /// When true, PII (emails, phones, credit cards, …) is redacted to opaque tokens
    /// before the translator — and therefore the upstream — sees the text.
    public bool RedactPii { get; set; }
    /// Admin-defined context that is prepended to DeepL's `context` parameter on every
    /// translation call. Helps the translator pick domain-appropriate terms.
    /// Max 4 000 characters (DeepL limit shared with accumulated conversation context).
    public string? SystemContext { get; set; }
}

public sealed class OrganizationEntity
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Plan { get; set; } = "free";
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class OrganizationTenantEntity
{
    public string OrganizationId { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class UserEntity
{
    public string Id { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? DisplayName { get; set; }
    /// SCIM external id when provisioned by an IdP.
    public string? ExternalId { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class OrganizationMemberEntity
{
    public string OrganizationId { get; set; } = default!;
    public string UserId { get; set; } = default!;
    /// `owner` | `admin` | `editor` | `viewer`.
    public string Role { get; set; } = "viewer";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class InviteEntity
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string Role { get; set; } = "viewer";
    public string TokenHash { get; set; } = default!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class BillingUsageEntity
{
    public long Id { get; set; }
    public string OrganizationId { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string Meter { get; set; } = default!;  // `requests` | `chars_translated`
    public long Amount { get; set; }
    /// UTC midnight of the counted day.
    public DateOnly Day { get; set; }
    public bool Reported { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class McpServerEntity
{
    public string Id { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string Name { get; set; } = default!;
    /// `remote` (Flow A) | `stdio-local` (Flow B, bridged locally).
    public string Transport { get; set; } = "remote";
    /// Non-secret upstream URL for remote MCP servers. Null for stdio-local.
    public string? RemoteUpstreamUrl { get; set; }
    public string UserLanguage { get; set; } = "en";
    public string LlmLanguage { get; set; } = "en";
    public string? TranslatorId { get; set; }
    public string? GlossaryId { get; set; }
    public string? StyleRuleId { get; set; }
    public string? ProxyRuleId { get; set; }
    public string RouteTokenId { get; set; } = default!;
    public string? CatalogEntryId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
}

public sealed class McpCatalogEntity
{
    public string Id { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string Description { get; set; } = default!;
    public string Transport { get; set; } = "remote";
    public string? UpstreamUrl { get; set; }
    public string? UpstreamCommandHint { get; set; }
    public string? DocsUrl { get; set; }
    public string? IconUrl { get; set; }
    public string Publisher { get; set; } = "community";
    public bool Verified { get; set; }
}

public sealed class AuditEventEntity
{
    public long Id { get; set; }
    public string TenantId { get; set; } = default!;
    public string? RouteId { get; set; }
    public string Method { get; set; } = default!;
    public string Path { get; set; } = default!;
    public int Status { get; set; }
    public string UserLanguage { get; set; } = default!;
    public string LlmLanguage { get; set; } = default!;
    public string Direction { get; set; } = default!;
    public string? TranslatorId { get; set; }
    public string? GlossaryId { get; set; }
    public string? RequestStyleRuleId { get; set; }
    public string? ResponseStyleRuleId { get; set; }
    public int RequestChars { get; set; }
    public int ResponseChars { get; set; }
    public int IntegrityFailures { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
