using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Api.Admin;

public static class GlossaryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/glossaries");
        g.MapPost("/", Create);
        g.MapGet("/", List);
        g.MapGet("/{id}", Get);
        g.MapDelete("/{id}", Delete);
        g.MapPost("/{id}/entries", AddEntries);
        g.MapGet("/{id}/entries", ListEntries);
    }

    public sealed record CreateGlossaryDto(string Id, string TenantId, string Name, string? DeeplGlossaryId);
    public sealed record GlossaryDto(string Id, string TenantId, string Name, string? DeeplGlossaryId,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public sealed record GlossaryEntryDto(string SourceLanguage, string TargetLanguage,
        string SourceTerm, string TargetTerm, bool CaseSensitive, bool DoNotTranslate);

    private static async Task<IResult> Create(CreateGlossaryDto dto, AdaptiveApiDbContext db, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == dto.TenantId, ct))
            return Results.BadRequest(new { error = "tenant_missing" });
        if (await db.Glossaries.AnyAsync(x => x.Id == dto.Id, ct))
            return Results.Conflict(new { error = "glossary_exists" });

        var now = DateTimeOffset.UtcNow;
        db.Glossaries.Add(new GlossaryEntity
        {
            Id = dto.Id, TenantId = dto.TenantId, Name = dto.Name,
            DeeplGlossaryId = dto.DeeplGlossaryId, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/admin/glossaries/{dto.Id}",
            new GlossaryDto(dto.Id, dto.TenantId, dto.Name, dto.DeeplGlossaryId, now, now));
    }

    private static async Task<IResult> List(AdaptiveApiDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Glossaries.AsNoTracking()
            .Select(x => new GlossaryDto(x.Id, x.TenantId, x.Name, x.DeeplGlossaryId, x.CreatedAt, x.UpdatedAt))
            .ToListAsync(ct));

    private static async Task<IResult> Get(string id, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var g = await db.Glossaries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return g is null ? Results.NotFound()
            : Results.Ok(new GlossaryDto(g.Id, g.TenantId, g.Name, g.DeeplGlossaryId, g.CreatedAt, g.UpdatedAt));
    }

    private static async Task<IResult> Delete(string id, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var g = await db.Glossaries.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return Results.NotFound();
        db.GlossaryEntries.RemoveRange(db.GlossaryEntries.Where(e => e.GlossaryId == id));
        db.Glossaries.Remove(g);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AddEntries(string id, GlossaryEntryDto[] entries,
        AdaptiveApiDbContext db, CancellationToken ct)
    {
        var g = await db.Glossaries.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return Results.NotFound();

        foreach (var e in entries)
        {
            db.GlossaryEntries.Add(new GlossaryEntryEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                GlossaryId = id,
                SourceLanguage = e.SourceLanguage,
                TargetLanguage = e.TargetLanguage,
                SourceTerm = e.SourceTerm,
                TargetTerm = e.TargetTerm,
                CaseSensitive = e.CaseSensitive,
                DoNotTranslate = e.DoNotTranslate,
            });
        }
        g.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { added = entries.Length });
    }

    private static async Task<IResult> ListEntries(string id, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var list = await db.GlossaryEntries.AsNoTracking()
            .Where(e => e.GlossaryId == id)
            .Select(e => new GlossaryEntryDto(
                e.SourceLanguage, e.TargetLanguage,
                e.SourceTerm, e.TargetTerm,
                e.CaseSensitive, e.DoNotTranslate))
            .ToListAsync(ct);
        return Results.Ok(list);
    }
}
