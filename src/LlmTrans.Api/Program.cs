using LlmTrans.Api;
using LlmTrans.Api.Admin;
using LlmTrans.Api.Auth;
using LlmTrans.Api.Proxy;
using LlmTrans.Core.Abstractions;
using LlmTrans.Core.Routing;
using LlmTrans.Infrastructure.Audit;
using LlmTrans.Infrastructure.Persistence;
using LlmTrans.Infrastructure.Routing;
using LlmTrans.Infrastructure.Rules;
using LlmTrans.Mcp.TranslateApi;
using LlmTrans.Providers.Anthropic;
using LlmTrans.Providers.Generic;
using LlmTrans.Providers.Mcp;
using LlmTrans.Providers.OpenAI;
using LlmTrans.Core.Pipeline;
using LlmTrans.Pii.Presidio;
using LlmTrans.Translators.DeepL;
using LlmTrans.Translators.Llm;
using LlmTrans.Translators.Passthrough;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<LlmTransDbContext>((sp, o) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var path = cfg.GetValue<string>("Database:Path") ?? "llmtrans.db";
    o.UseSqlite($"Data Source={path}");
});

builder.Services.AddHttpClient("openai-upstream");
builder.Services.AddHttpClient("anthropic-upstream");
builder.Services.AddHttpClient("mcp-upstream");
builder.Services.AddHttpClient("generic-upstream");
builder.Services.AddHttpClient("deepl");
builder.Services.AddHttpClient("llm-translator");
builder.Services.AddHttpClient("presidio");

builder.Services.Configure<DeepLOptions>(builder.Configuration.GetSection("Translators:DeepL"));
builder.Services.Configure<LlmTranslatorOptions>(builder.Configuration.GetSection("Translators:Llm"));
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
builder.Services.AddSingleton<ITranslator, DeepLTranslator>();
builder.Services.AddSingleton<ITranslator, LlmTranslator>();
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
    var db = scope.ServiceProvider.GetRequiredService<LlmTransDbContext>();
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

app.MapGet("/", () => Results.Ok(new { service = "llmtrans", status = "ok" }));
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

ProxyEndpoints.Map(app);

// Admin + SaaS endpoints go inside the `admin-policy` group so unauthenticated callers
// get a 401. When `LlmTrans:Auth:Mode=none` (the dev default), the policy grants access
// to everyone — existing behaviour preserved.
var adminGroup = app.MapGroup(string.Empty).RequireAuthorization(AuthSetup.AdminPolicy);
AdminEndpoints.Map(adminGroup);
GlossaryEndpoints.Map(adminGroup);
StyleRuleEndpoints.Map(adminGroup);
ProxyRuleEndpoints.Map(adminGroup);
McpEndpoints.Map(adminGroup);
DocumentTranslationEndpoints.Map(adminGroup);
AuditEndpoints.Map(adminGroup);

TranslateEndpoint.Map(app);

AuthSetup.MapAuthEndpoints(app, authMode, app.Services.GetRequiredService<AuthOptions>());

foreach (var plugin in plugins)
    plugin.Map(app);

app.Run();

public partial class Program;
