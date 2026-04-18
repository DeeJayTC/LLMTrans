using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Routing;
using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveApi.Integration.Tests;

public sealed class ProxyTestFactory : WebApplicationFactory<Program>
{
    public const string FixedToken = "rt_dev_TESTTOKEN1234567890";
    public FakeUpstreamHandler Upstream { get; } = new();
    public FakeBracketTranslator FakeTranslator { get; } = new();
    public RecordingTranslator Recording { get; } = new();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
        $"adaptiveapi-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(cfg =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath,
                ["Dev:FixedRouteToken"] = FixedToken,
                ["Translators:Default"] = "fake-brackets",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("openai-upstream")
                .ConfigurePrimaryHttpMessageHandler(() => Upstream);

            // Register both fakes; DefaultTranslatorRouter picks by id via config.
            services.AddSingleton<ITranslator>(FakeTranslator);
            services.AddSingleton<ITranslator>(Recording);
        });

        builder.UseEnvironment("Testing");
    }

    public async Task ConfigureRouteAsync(string userLanguage, string llmLanguage, string direction = "Bidirectional")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdaptiveApiDbContext>();
        var r = await db.Routes.FirstAsync(r => r.Id == Seeder.DevRouteId);
        r.UserLanguage = userLanguage;
        r.LlmLanguage = llmLanguage;
        r.Direction = direction;
        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}
