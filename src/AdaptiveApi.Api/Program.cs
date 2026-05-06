using AdaptiveApi.Api;
using AdaptiveApi.Api.Admin;
using AdaptiveApi.Api.Auth;
using AdaptiveApi.Api.Proxy;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Plugins;
using AdaptiveApi.Core.Routing;
using AdaptiveApi.Api.Secrets;
using AdaptiveApi.Infrastructure.Audit;
using AdaptiveApi.Infrastructure.Caching;
using AdaptiveApi.Infrastructure.Persistence;
using AdaptiveApi.Infrastructure.Plugins;
using AdaptiveApi.Infrastructure.Routing;
using AdaptiveApi.Infrastructure.Rules;
using AdaptiveApi.Infrastructure.Secrets;
using AdaptiveApi.Plugins.SDK;
using AdaptiveApi.Mcp.TranslateApi;
using AdaptiveApi.Providers.Anthropic;
using AdaptiveApi.Providers.Generic;
using AdaptiveApi.Providers.Mcp;
using AdaptiveApi.Providers.OpenAI;
using AdaptiveApi.Core.Pipeline;
using AdaptiveApi.Pii.Presidio;
using AdaptiveApi.Translators.DeepL;
using AdaptiveApi.Translators.Llm;
using AdaptiveApi.Translators.Passthrough;
using Microsoft.Extensions.Options;
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAdaptiveApiDb();
builder.Services.AddAdaptiveApiDistributedCache(builder.Configuration);
builder.Services.AddAdaptiveApiTranslationCache();

builder.Services.AddHttpClient("openai-upstream");
builder.Services.AddHttpClient("anthropic-upstream");
builder.Services.AddHttpClient("mcp-upstream");
builder.Services.AddHttpClient("generic-upstream");
builder.Services.AddHttpClient("deepl");
builder.Services.AddHttpClient("llm-translator");
builder.Services.AddHttpClient("presidio");

builder.Services.Configure<DeepLOptions>(builder.Configuration.GetSection("Translators:DeepL"));
builder.Services.Configure<AdaptiveApilatorOptions>(builder.Configuration.GetSection("Translators:Llm"));
builder.Services.Configure<PresidioOptions>(builder.Configuration.GetSection("PiiRedactor:Presidio"));

// Encrypted secret store. When AdaptiveApi:Secrets:Kek is set the DB-backed
// store is active and translator keys can come from it; otherwise the
// fallback resolves to env config only. The post-configure hooks below
// override DeepL / LLM ApiKey with the stored value when present.
builder.Services.AddScoped<ISecretStore, DbSecretStore>();
builder.Services.AddSingleton<IPostConfigureOptions<DeepLOptions>, DeepLOptionsPostConfigure>();
builder.Services.AddSingleton<IPostConfigureOptions<AdaptiveApilatorOptions>, AdaptiveApilatorOptionsPostConfigure>();

builder.Services.AddSingleton<IPiiRedactor>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var provider = cfg.GetValue<string>("PiiRedactor:Provider") ?? "regex";
    return provider.ToLowerInvariant() switch
    {
        "presidio" => ActivatorUtilities.CreateInstance<PresidioPiiRedactor>(sp),
        _ => new RegexPiiRedactor(),
    };
});

builder.Services.AddScoped<IRouteResolver, DbRouteResolver>();
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<IRuleResolver, DbRuleResolver>();
builder.Services.AddSingleton<IAuditSink, DbAuditSink>();

builder.Services.AddSingleton<ISaasFeatures, SelfHostFeatures>();

builder.Services.AddSingleton<ITranslator, PassthroughTranslator>();
builder.Services.AddSingleton<DeepLApiClient>();
builder.Services.AddSingleton<ITranslator, DeepLTranslator>();
builder.Services.AddSingleton<ITranslator, AdaptiveApilator>();
builder.Services.AddSingleton<DeepLDocumentTranslator>();

builder.Services.AddSingleton<ITranslatorRouter>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var defaultId = cfg.GetValue<string>("Translators:Default") ?? "passthrough";
    return new DefaultTranslatorRouter(sp.GetServices<ITranslator>(), defaultId);
});

builder.Services.AddScoped<OpenAiChatAdapter>();
builder.Services.AddScoped<AnthropicMessagesAdapter>();
builder.Services.AddScoped<McpRouteAdapter>();
builder.Services.AddScoped<GenericAdapter>();

builder.Services.AddLogging();

var authMode = AuthSetup.Configure(builder);

// Build a bootstrap logger from the configuration so plugin discovery
// warnings (missing deps, ctor mismatches, anonymous-endpoint opt-in) reach
// the same sinks the app uses. We can't use builder.Services here without
// building a throwaway provider, so we route through Console explicitly.
using var bootstrapLoggerFactory = LoggerFactory.Create(b =>
{
    b.AddConfiguration(builder.Configuration.GetSection("Logging"));
    b.AddConsole();
});
var bootstrapLogger = bootstrapLoggerFactory.CreateLogger("AdaptiveApi.PluginLoader");
var discovered = PluginLoader.Discover(bootstrapLogger);
foreach (var plugin in discovered.WebPlugins)
    plugin.ConfigureServices(builder);

// Plugin module hooks: each module registers its hooks/services in DI, then
// the dispatcher resolves them via IEnumerable<THook>. Module instances also
// register as IAdaptiveApiPlugin so the registry can list manifests.
foreach (var module in discovered.Modules)
{
    module.RegisterServices(builder.Services);
    builder.Services.AddSingleton<IAdaptiveApiPlugin>(module);
}
builder.Services.AddAdaptiveApiPluginHooks();
builder.Services.AddSingleton<IPluginRegistry>(sp =>
    new PluginRegistry(sp.GetServices<IAdaptiveApiPlugin>(), discovered.Disabled));
builder.Services.AddScoped<IPluginSettingsStore, DbPluginSettingsStore>();
builder.Services.AddScoped<AdaptiveApi.Core.Plugins.IPluginEnablement,
    AdaptiveApi.Infrastructure.Plugins.DbPluginEnablement>();

// Built-in regex request rules — same hook contract a plugin would use, so
// they fan out across every adapter via the existing dispatcher. Scoped
// because the source caches loaded rules per-request.
builder.Services.AddScoped<AdaptiveApi.Infrastructure.RequestRules.IRequestRuleSource,
    AdaptiveApi.Infrastructure.RequestRules.DbRequestRuleSource>();
builder.Services.AddScoped<AdaptiveApi.Plugins.SDK.Hooks.IRequestTranslationHook,
    AdaptiveApi.Infrastructure.RequestRules.RequestRuleRequestHook>();
builder.Services.AddScoped<AdaptiveApi.Plugins.SDK.Hooks.IResponseTranslationHook,
    AdaptiveApi.Infrastructure.RequestRules.RequestRuleResponseHook>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AdaptiveApiDbContext>();
    var fixed_ = app.Configuration.GetValue<string>("Dev:FixedRouteToken");
    var token = await Seeder.EnsureSeededAsync(db, fixed_, default);
    if (token is not null)
        app.Logger.LogInformation("seeded dev route token: {Token}", token);

    var catalogPath = app.Configuration.GetValue<string>("Mcp:CatalogFile")
                      ?? Path.Combine(AppContext.BaseDirectory, "catalog", "mcp-servers.json");
    if (!File.Exists(catalogPath))
    {
        var repoPath = Path.Combine(Directory.GetCurrentDirectory(), "catalog", "mcp-servers.json");
        if (File.Exists(repoPath)) catalogPath = repoPath;
    }
    await McpCatalogSeeder.EnsureSeededAsync(db, catalogPath, default);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { service = "adaptiveapi", status = "ok" }));
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

ProxyEndpoints.Map(app);

// Admin + SaaS endpoints go inside the `admin-policy` group so unauthenticated callers
// get a 401. When `AdaptiveApi:Auth:Mode=none` (the dev default), the policy grants access
// to everyone — existing behaviour preserved.
var adminGroup = app.MapGroup(string.Empty).RequireAuthorization(AuthSetup.AdminPolicy);
AdminEndpoints.Map(adminGroup);
GlossaryEndpoints.Map(adminGroup);
StyleRuleEndpoints.Map(adminGroup);
ProxyRuleEndpoints.Map(adminGroup);
PiiPackEndpoints.Map(adminGroup);
PiiRuleEndpoints.Map(adminGroup);
McpEndpoints.Map(adminGroup);
DocumentTranslationEndpoints.Map(adminGroup);
TranslationMemoryEndpoints.Map(adminGroup);
AuditEndpoints.Map(adminGroup);
SystemEndpoints.Map(adminGroup);
RouteProfileEndpoints.Map(adminGroup);
RequestRuleEndpoints.Map(adminGroup);
SecretEndpoints.Map(adminGroup);

TranslateEndpoint.Map(app);

AuthSetup.MapAuthEndpoints(app, authMode, app.Services.GetRequiredService<AuthOptions>());

PluginEndpoints.Map(adminGroup);

foreach (var plugin in discovered.WebPlugins)
    plugin.Map(app);

// Each plugin module gets its own /plugins/{id} group. By default the host
// wraps the group with the admin policy so a plugin can't accidentally open
// the host. Plugins that need anonymous endpoints (webhooks, OAuth callbacks)
// must opt in via PluginManifest.AllowAnonymousEndpoints; the loader logs a
// startup warning every time that opt-in is in effect.
foreach (var module in discovered.Modules)
{
    var groupBase = app.MapGroup($"/plugins/{module.Manifest.Id}");
    var group = module.Manifest.AllowAnonymousEndpoints
        ? groupBase
        : groupBase.RequireAuthorization(AuthSetup.AdminPolicy);
    module.MapRoutes(group);
}

app.Run();

public partial class Program;
