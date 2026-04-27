using AdaptiveApi.Core.Pipeline;
using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Api.Admin;

/// Read-only catalog of premade PII packs. Tenants reference packs by slug from
/// their proxy rule. The packs themselves are seeded at startup from
/// `BuiltinDetectors`; admins can hand-edit the JSON in the DB to tune patterns
/// without losing changes on next boot.
public static class PiiPackEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/pii-packs");
        g.MapGet("/", List);
        g.MapGet("/{slug}", Get);
    }

    public sealed record DetectorDto(string Kind, string Pattern, string Replacement,
        IReadOnlyList<string>? Flags, bool LuhnValidate);

    public sealed record PackDto(string Slug, string Name, string Description, bool IsBuiltin,
        int Ordinal, IReadOnlyList<DetectorDto> Detectors);

    private static async Task<IResult> List(AdaptiveApiDbContext db, CancellationToken ct)
    {
        var packs = await db.PiiPacks.AsNoTracking()
            .OrderBy(p => p.Ordinal)
            .ToListAsync(ct);
        return Results.Ok(packs.Select(ToDto).ToList());
    }

    private static async Task<IResult> Get(string slug, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var p = await db.PiiPacks.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug, ct);
        return p is null ? Results.NotFound() : Results.Ok(ToDto(p));
    }

    private static PackDto ToDto(PiiPackEntity p)
    {
        var detectors = PiiDetectorSerializer.Deserialize(p.DetectorsJson)
            .Select(d => new DetectorDto(d.Kind, d.Pattern, d.Replacement, d.Flags, d.LuhnValidate))
            .ToList();
        return new PackDto(p.Slug, p.Name, p.Description, p.IsBuiltin, p.Ordinal, detectors);
    }
}
