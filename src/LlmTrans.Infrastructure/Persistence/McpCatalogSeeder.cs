using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace LlmTrans.Infrastructure.Persistence;

public static class McpCatalogSeeder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task EnsureSeededAsync(LlmTransDbContext db, string catalogFilePath, CancellationToken ct)
    {
        if (!File.Exists(catalogFilePath)) return;
        if (await db.McpCatalog.AnyAsync(ct)) return;

        await using var stream = File.OpenRead(catalogFilePath);
        var doc = await JsonSerializer.DeserializeAsync<CatalogDoc>(stream, SerializerOptions, ct);
        if (doc?.Entries is null) return;

        foreach (var entry in doc.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Slug)) continue;

            db.McpCatalog.Add(new McpCatalogEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Slug = entry.Slug,
                DisplayName = entry.DisplayName ?? entry.Slug,
                Description = entry.Description ?? string.Empty,
                Transport = entry.Transport ?? "remote",
                UpstreamUrl = entry.UpstreamUrl,
                UpstreamCommandHint = entry.UpstreamCommandHint,
                DocsUrl = entry.DocsUrl,
                IconUrl = entry.IconUrl,
                Publisher = entry.Publisher ?? "community",
                Verified = entry.Verified,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private sealed class CatalogDoc
    {
        [JsonPropertyName("entries")] public List<CatalogEntry> Entries { get; set; } = new();
    }

    private sealed class CatalogEntry
    {
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("transport")] public string? Transport { get; set; }
        [JsonPropertyName("upstreamUrl")] public string? UpstreamUrl { get; set; }
        [JsonPropertyName("upstreamCommandHint")] public string? UpstreamCommandHint { get; set; }
        [JsonPropertyName("docsUrl")] public string? DocsUrl { get; set; }
        [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
        [JsonPropertyName("publisher")] public string? Publisher { get; set; }
        [JsonPropertyName("verified")] public bool Verified { get; set; }
    }
}
