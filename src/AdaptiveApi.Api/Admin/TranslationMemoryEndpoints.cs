using AdaptiveApi.Translators.DeepL;

namespace AdaptiveApi.Api.Admin;

/// <summary>
/// Read-only listing of DeepL Translation Memories. TMs are created and edited
/// in the DeepL portal — DeepL does not yet expose CRUD endpoints, so this is
/// list-only. When DeepL ships create/upload/delete, those should be added here
/// alongside the existing list endpoint.
/// </summary>
public static class TranslationMemoryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/translation-memories");
        g.MapGet("/", List);
    }

    public sealed record TranslationMemoryDto(
        string Id,
        string Name,
        string? SourceLanguage,
        IReadOnlyList<string>? TargetLanguages,
        int? SegmentCount);

    private static async Task<IResult> List(DeepLApiClient client, CancellationToken ct)
    {
        TranslationMemoryList? response;
        try
        {
            response = await client.ListTranslationMemoriesAsync(ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("API key", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                title: "deepl_not_configured",
                detail: "Translators:DeepL:ApiKey is not configured. Set the DeepL API key to list translation memories.",
                statusCode: 503);
        }

        if (response is null)
            return Results.Problem(
                title: "deepl_upstream_error",
                detail: "DeepL /v3/translation_memories returned a non-success status. See server logs for details.",
                statusCode: 502);

        var dtos = response.TranslationMemories
            .Select(t => new TranslationMemoryDto(
                t.TranslationMemoryId, t.Name, t.SourceLanguage, t.TargetLanguages, t.SegmentCount))
            .ToList();

        return Results.Ok(new { translationMemories = dtos, totalCount = response.TotalCount ?? dtos.Count });
    }
}
