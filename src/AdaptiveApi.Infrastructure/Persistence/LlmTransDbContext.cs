using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Infrastructure.Persistence;

public sealed class AdaptiveApiDbContext : DbContext
{
    public AdaptiveApiDbContext(DbContextOptions<AdaptiveApiDbContext> options) : base(options) { }

    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<RouteEntity> Routes => Set<RouteEntity>();
    public DbSet<RouteTokenEntity> RouteTokens => Set<RouteTokenEntity>();
    public DbSet<GlossaryEntity> Glossaries => Set<GlossaryEntity>();
    public DbSet<GlossaryEntryEntity> GlossaryEntries => Set<GlossaryEntryEntity>();
    public DbSet<StyleRuleEntity> StyleRules => Set<StyleRuleEntity>();
    public DbSet<CustomInstructionEntity> CustomInstructions => Set<CustomInstructionEntity>();
    public DbSet<ProxyRuleEntity> ProxyRules => Set<ProxyRuleEntity>();
    public DbSet<McpServerEntity> McpServers => Set<McpServerEntity>();
    public DbSet<McpCatalogEntity> McpCatalog => Set<McpCatalogEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<OrganizationEntity> Organizations => Set<OrganizationEntity>();
    public DbSet<OrganizationTenantEntity> OrganizationTenants => Set<OrganizationTenantEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<OrganizationMemberEntity> OrganizationMembers => Set<OrganizationMemberEntity>();
    public DbSet<InviteEntity> Invites => Set<InviteEntity>();
    public DbSet<BillingUsageEntity> BillingUsage => Set<BillingUsageEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TenantEntity>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
        });

        b.Entity<RouteEntity>(e =>
        {
            e.ToTable("routes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(32).IsRequired();
            e.Property(x => x.UpstreamBaseUrl).HasMaxLength(1024).IsRequired();
            e.Property(x => x.UserLanguage).HasMaxLength(16).IsRequired();
            e.Property(x => x.LlmLanguage).HasMaxLength(16).IsRequired();
            e.Property(x => x.Direction).HasMaxLength(16).IsRequired();
            e.HasIndex(x => x.TenantId);
        });

        b.Entity<RouteTokenEntity>(e =>
        {
            e.ToTable("route_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.RouteId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Scope).HasMaxLength(32).IsRequired();
            e.Property(x => x.Prefix).HasMaxLength(32).IsRequired();
            e.Property(x => x.Hash).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.Prefix);
            e.HasIndex(x => x.Hash).IsUnique();
        });

        b.Entity<GlossaryEntity>(e =>
        {
            e.ToTable("glossaries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.DeeplGlossaryId).HasMaxLength(128);
            e.HasIndex(x => x.TenantId);
        });

        b.Entity<GlossaryEntryEntity>(e =>
        {
            e.ToTable("glossary_entries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.GlossaryId).HasMaxLength(64).IsRequired();
            e.Property(x => x.SourceLanguage).HasMaxLength(16).IsRequired();
            e.Property(x => x.TargetLanguage).HasMaxLength(16).IsRequired();
            e.Property(x => x.SourceTerm).HasMaxLength(512).IsRequired();
            e.Property(x => x.TargetTerm).HasMaxLength(512).IsRequired();
            e.HasIndex(x => new { x.GlossaryId, x.SourceLanguage, x.TargetLanguage });
        });

        b.Entity<StyleRuleEntity>(e =>
        {
            e.ToTable("style_rules");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Language).HasMaxLength(16).IsRequired();
            e.Property(x => x.DeeplStyleId).HasMaxLength(128);
            e.HasIndex(x => x.TenantId);
        });

        b.Entity<CustomInstructionEntity>(e =>
        {
            e.ToTable("custom_instructions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.StyleRuleId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Label).HasMaxLength(128).IsRequired();
            e.Property(x => x.Prompt).HasMaxLength(300).IsRequired();
            e.HasIndex(x => x.StyleRuleId);
        });

        b.Entity<ProxyRuleEntity>(e =>
        {
            e.ToTable("proxy_rules");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.TenantId);
        });

        b.Entity<OrganizationEntity>(e =>
        {
            e.ToTable("organizations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Plan).HasMaxLength(32).IsRequired();
            e.Property(x => x.StripeCustomerId).HasMaxLength(128);
            e.Property(x => x.StripeSubscriptionId).HasMaxLength(128);
        });

        b.Entity<OrganizationTenantEntity>(e =>
        {
            e.ToTable("organization_tenants");
            e.HasKey(x => new { x.OrganizationId, x.TenantId });
            e.Property(x => x.OrganizationId).HasMaxLength(64);
            e.Property(x => x.TenantId).HasMaxLength(64);
            e.HasIndex(x => x.TenantId).IsUnique();
        });

        b.Entity<UserEntity>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.ExternalId).HasMaxLength(256);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.ExternalId);
        });

        b.Entity<OrganizationMemberEntity>(e =>
        {
            e.ToTable("organization_members");
            e.HasKey(x => new { x.OrganizationId, x.UserId });
            e.Property(x => x.OrganizationId).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.Role).HasMaxLength(16).IsRequired();
        });

        b.Entity<InviteEntity>(e =>
        {
            e.ToTable("invites");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.OrganizationId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.Role).HasMaxLength(16).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.Email });
        });

        b.Entity<BillingUsageEntity>(e =>
        {
            e.ToTable("billing_usage");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.OrganizationId).HasMaxLength(64).IsRequired();
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Meter).HasMaxLength(32).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.Day, x.Meter });
            e.HasIndex(x => new { x.Reported, x.Day });
        });

        b.Entity<McpServerEntity>(e =>
        {
            e.ToTable("mcp_servers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Transport).HasMaxLength(32).IsRequired();
            e.Property(x => x.RemoteUpstreamUrl).HasMaxLength(1024);
            e.Property(x => x.UserLanguage).HasMaxLength(16).IsRequired();
            e.Property(x => x.LlmLanguage).HasMaxLength(16).IsRequired();
            e.Property(x => x.RouteTokenId).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.RouteTokenId).IsUnique();
        });

        b.Entity<McpCatalogEntity>(e =>
        {
            e.ToTable("mcp_catalog");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Slug).HasMaxLength(128).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2048).IsRequired();
            e.Property(x => x.Transport).HasMaxLength(32).IsRequired();
            e.Property(x => x.Publisher).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
        });

        b.Entity<AuditEventEntity>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.RouteId).HasMaxLength(64);
            e.Property(x => x.Method).HasMaxLength(16).IsRequired();
            e.Property(x => x.Path).HasMaxLength(512).IsRequired();
            e.Property(x => x.UserLanguage).HasMaxLength(16).IsRequired();
            e.Property(x => x.LlmLanguage).HasMaxLength(16).IsRequired();
            e.Property(x => x.Direction).HasMaxLength(16).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
        });
    }
}
