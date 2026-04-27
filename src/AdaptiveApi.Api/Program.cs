using AdaptiveApi.Api;
using AdaptiveApi.Api.Admin;
using AdaptiveApi.Api.Auth;
using AdaptiveApi.Api.Proxy;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Routing;
using AdaptiveApi.Infrastructure.Audit;
using AdaptiveApi.Infrastructure.Caching;
using AdaptiveApi.Infrastructure.Persistence;
using AdaptiveApi.Infrastructure.Routing;
using AdaptiveApi.Infrastructure.Rules;
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

var plugins = PluginLoader.Discover();
foreach (var plugin in plugins)
    plugin.ConfigureServices(builder);

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
AuditEndpoints.Map(adminGroup);

TranslateEndpoint.Map(app);

AuthSetup.MapAuthEndpoints(app, authMode, app.Services.GetRequiredService<AuthOptions>());

foreach (var plugin in plugins)
    plugin.Map(app);

app.Run();

public partial class Program;
