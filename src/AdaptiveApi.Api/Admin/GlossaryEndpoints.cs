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
        g.MapPost("/{id}/entries/import", ImportEntries);
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

    public sealed record ImportRequest(
        string Format,
        string SourceLanguage,
        string TargetLanguage,
        string Body,
        bool? CaseSensitive,
        bool? DoNotTranslate);

    public sealed record ImportResponse(int Added, int Skipped, IReadOnlyList<string> Errors);

    /// Bulk import. Format: <c>csv</c> (header row: source,target) or
    /// <c>tbx</c> (TermBase eXchange — minimal subset that picks
    /// <c>&lt;langSet xml:lang="…"&gt;&lt;tig&gt;&lt;term&gt;</c> pairs out
    /// of TBX 2008/3.0 files). Languages on the request are the fallback when
    /// the format itself doesn't carry them.
    private static async Task<IResult> ImportEntries(
        string id,
        ImportRequest req,
        AdaptiveApiDbContext db,
        CancellationToken ct)
    {
        var g = await db.Glossaries.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return Results.NotFound();

        var caseSensitive = req.CaseSensitive ?? false;
        var doNotTranslate = req.DoNotTranslate ?? false;
        var src = req.SourceLanguage;
        var tgt = req.TargetLanguage;
        var errors = new List<string>();
        var pairs = req.Format?.ToLowerInvariant() switch
        {
            "csv" => ParseCsv(req.Body, errors),
            "tbx" => ParseTbx(req.Body, src, tgt, errors),
            _ => null,
        };
        if (pairs is null)
            return Results.BadRequest(new { error = "unsupported_format", supported = new[] { "csv", "tbx" } });

        var added = 0;
        var skipped = 0;
        foreach (var (s, t) in pairs)
        {
            if (string.IsNullOrWhiteSpace(s) || string.IsNullOrWhiteSpace(t))
            {
                skipped++;
                continue;
            }
            db.GlossaryEntries.Add(new GlossaryEntryEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                GlossaryId = id,
                SourceLanguage = src,
                TargetLanguage = tgt,
                SourceTerm = s,
                TargetTerm = t,
                CaseSensitive = caseSensitive,
                DoNotTranslate = doNotTranslate,
            });
            added++;
        }
        if (added > 0)
        {
            g.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new ImportResponse(added, skipped, errors));
    }

    /// Minimal CSV: comma-separated, optional double-quoted fields, first row
    /// is the header (must contain "source" and "target" columns in any
    /// order). Comments / blank lines are skipped.
    private static List<(string, string)> ParseCsv(string body, List<string> errors)
    {
        var pairs = new List<(string, string)>();
        if (string.IsNullOrEmpty(body)) return pairs;

        using var reader = new StringReader(body);
        var headerLine = reader.ReadLine();
        if (headerLine is null)
        {
            errors.Add("empty CSV");
            return pairs;
        }
        var header = SplitCsvRow(headerLine);
        var srcIdx = header.FindIndex(h => h.Equals("source", StringComparison.OrdinalIgnoreCase));
        var tgtIdx = header.FindIndex(h => h.Equals("target", StringComparison.OrdinalIgnoreCase));
        if (srcIdx < 0 || tgtIdx < 0)
        {
            errors.Add("CSV header must include 'source' and 'target' columns");
            return pairs;
        }

        string? line;
        var lineNo = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var cells = SplitCsvRow(line);
            if (srcIdx >= cells.Count || tgtIdx >= cells.Count)
            {
                errors.Add($"line {lineNo}: not enough columns");
                continue;
            }
            pairs.Add((cells[srcIdx], cells[tgtIdx]));
        }
        return pairs;
    }

    private static List<string> SplitCsvRow(string row)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < row.Length; i++)
        {
            var c = row[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < row.Length && row[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else current.Append(c);
        }
        cells.Add(current.ToString().Trim());
        return cells;
    }

    /// Minimal TBX reader: walks <c>&lt;termEntry&gt;</c> elements and pulls
    /// out the term text for the requested source / target languages. We do
    /// not attempt full TBX validation; bad files surface as parse errors
    /// in the response and the import as a whole still continues.
    private static List<(string, string)> ParseTbx(string body, string src, string tgt, List<string> errors)
    {
        var pairs = new List<(string, string)>();
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(body);
            foreach (var entry in doc.Descendants("termEntry"))
            {
                string? srcTerm = null;
                string? tgtTerm = null;
                foreach (var langSet in entry.Elements("langSet"))
                {
                    var lang = (string?)langSet.Attribute(System.Xml.Linq.XNamespace.Xml + "lang") ?? "";
                    var term = langSet.Descendants("term").FirstOrDefault()?.Value;
                    if (term is null) continue;
                    if (lang.Equals(src, StringComparison.OrdinalIgnoreCase)) srcTerm = term;
                    else if (lang.Equals(tgt, StringComparison.OrdinalIgnoreCase)) tgtTerm = term;
                }
                if (srcTerm is not null && tgtTerm is not null)
                    pairs.Add((srcTerm, tgtTerm));
            }
        }
        catch (System.Xml.XmlException ex)
        {
            errors.Add($"TBX parse error: {ex.Message}");
        }
        return pairs;
    }
}
