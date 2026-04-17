using LlmTrans.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmTrans.Api.Admin;

public static class StyleRuleEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var sr = app.MapGroup("/admin/style-rules");
        sr.MapPost("/", Create);
        sr.MapGet("/", List);
        sr.MapPost("/{id}/instructions", SetInstructions);
        sr.MapGet("/{id}/instructions", ListInstructions);
    }

    public sealed record CreateDto(string Id, string TenantId, string Name, string Language,
        string? DeeplStyleId, string? RulesJson);
    public sealed record Dto(string Id, string TenantId, string Name, string Language,
        string? DeeplStyleId, int Version);
    public sealed record InstructionDto(string Label, string Prompt, int Ordinal);

    private static async Task<IResult> Create(CreateDto dto, LlmTransDbContext db, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == dto.TenantId, ct))
            return Results.BadRequest(new { error = "tenant_missing" });
        if (await db.StyleRules.AnyAsync(x => x.Id == dto.Id, ct))
            return Results.Conflict(new { error = "style_rule_exists" });

        var now = DateTimeOffset.UtcNow;
        db.StyleRules.Add(new StyleRuleEntity
        {
            Id = dto.Id, TenantId = dto.TenantId, Name = dto.Name, Language = dto.Language,
            DeeplStyleId = dto.DeeplStyleId,
            RulesJson = string.IsNullOrWhiteSpace(dto.RulesJson) ? "{}" : dto.RulesJson!,
            CreatedAt = now, UpdatedAt = now, Version = 1,
        });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/admin/style-rules/{dto.Id}",
            new Dto(dto.Id, dto.TenantId, dto.Name, dto.Language, dto.DeeplStyleId, 1));
    }

    private static async Task<IResult> List(LlmTransDbContext db, CancellationToken ct) =>
        Results.Ok(await db.StyleRules.AsNoTracking()
            .Select(x => new Dto(x.Id, x.TenantId, x.Name, x.Language, x.DeeplStyleId, x.Version))
            .ToListAsync(ct));

    private static async Task<IResult> SetInstructions(string id, InstructionDto[] instructions,
        LlmTransDbContext db, CancellationToken ct)
    {
        var sr = await db.StyleRules.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (sr is null) return Results.NotFound();

        if (instructions.Length > 10)
            return Results.BadRequest(new { error = "too_many_instructions", max = 10 });
        if (instructions.Any(i => i.Prompt.Length > 300))
            return Results.BadRequest(new { error = "instruction_too_long", max_chars = 300 });

        db.CustomInstructions.RemoveRange(db.CustomInstructions.Where(c => c.StyleRuleId == id));
        foreach (var i in instructions)
        {
            db.CustomInstructions.Add(new CustomInstructionEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                StyleRuleId = id,
                Label = i.Label,
                Prompt = i.Prompt,
                Ordinal = i.Ordinal,
            });
        }
        sr.UpdatedAt = DateTimeOffset.UtcNow;
        sr.Version++;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { count = instructions.Length, version = sr.Version });
    }

    private static async Task<IResult> ListInstructions(string id, LlmTransDbContext db, CancellationToken ct)
    {
        var list = await db.CustomInstructions.AsNoTracking()
            .Where(c => c.StyleRuleId == id)
            .OrderBy(c => c.Ordinal)
            .Select(c => new InstructionDto(c.Label, c.Prompt, c.Ordinal))
            .ToListAsync(ct);
        return Results.Ok(list);
    }
}
