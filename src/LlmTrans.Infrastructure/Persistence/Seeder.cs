using LlmTrans.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LlmTrans.Infrastructure.Persistence;

public static class Seeder
{
    public const string DevTenantId = "t_dev";
    public const string DevRouteId = "r_openai_dev";
    public const string DevTokenPrefix = "rt_dev";

    /// Ensures the database is created and a single dev tenant + OpenAI route + route token exist.
    /// Returns the plaintext route token (only when freshly created; null if already seeded).
    public static async Task<string?> EnsureSeededAsync(LlmTransDbContext db, string? fixedTokenForTests, CancellationToken ct)
    {
        await db.Database.EnsureCreatedAsync(ct);

        if (!await db.Tenants.AnyAsync(t => t.Id == DevTenantId, ct))
        {
            db.Tenants.Add(new TenantEntity
            {
                Id = DevTenantId,
                Name = "Dev",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        if (!await db.Routes.AnyAsync(r => r.Id == DevRouteId, ct))
        {
            db.Routes.Add(new RouteEntity
            {
                Id = DevRouteId,
                TenantId = DevTenantId,
                Kind = nameof(RouteKind.OpenAiChat),
                UpstreamBaseUrl = "https://api.openai.com/",
                UserLanguage = "en",
                LlmLanguage = "en",
                Direction = "Off",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        string? plaintext = null;
        if (!await db.RouteTokens.AnyAsync(t => t.RouteId == DevRouteId, ct))
        {
            plaintext = fixedTokenForTests ?? RouteToken.Generate("dev");
            db.RouteTokens.Add(new RouteTokenEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                TenantId = DevTenantId,
                RouteId = DevRouteId,
                Scope = "route",
                Prefix = RouteToken.PrefixOf(plaintext),
                Hash = RouteToken.HashForStorage(plaintext),
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);
        return plaintext;
    }
}
