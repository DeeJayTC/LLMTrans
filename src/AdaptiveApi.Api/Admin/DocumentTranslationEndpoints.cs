using AdaptiveApi.Core.Model;
using AdaptiveApi.Translators.DeepL;

namespace AdaptiveApi.Api.Admin;

public static class DocumentTranslationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Default Kestrel request size (30 MB) is below DeepL's /v2/document limit (50 MB).
        // Operators needing the full 50 MB should raise `Kestrel:Limits:MaxRequestBodySize`.
        app.MapPost("/admin/translate-document", Translate).DisableAntiforgery();
    }

    private static async Task<IResult> Translate(
        HttpContext ctx,
        DeepLDocumentTranslator translator,
        CancellationToken ct)
    {
        if (!ctx.Request.HasFormContentType)
            return Results.BadRequest(new { error = "multipart_form_required" });

        var form = await ctx.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "file_field_required" });

        var targetLang = form["targetLang"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(targetLang))
            return Results.BadRequest(new { error = "target_lang_required" });

        var sourceLang = form["sourceLang"].FirstOrDefault();
        var glossaryId = form["glossaryId"].FirstOrDefault();
        var formality = form["formality"].FirstOrDefault();

        await using var input = file.OpenReadStream();
        using var output = new MemoryStream();

        var result = await translator.TranslateAsync(
            input,
            file.FileName,
            output,
            target: new LanguageCode(targetLang!.Trim().ToLowerInvariant()),
            source: string.IsNullOrWhiteSpace(sourceLang)
                ? null
                : new LanguageCode(sourceLang!.Trim().ToLowerInvariant()),
            glossaryId: glossaryId,
            formality: formality,
            ct: ct);

        if (!result.Ok)
        {
            return Results.Problem(
                title: "document_translation_failed",
                detail: result.ErrorMessage ?? "unknown error",
                statusCode: StatusCodes.Status502BadGateway);
        }

        output.Position = 0;
        var contentType = file.ContentType switch
        {
            "" or null => "application/octet-stream",
            var v => v,
        };

        ctx.Response.Headers["X-AdaptiveApi-Billed-Chars"] = result.BilledCharacters.ToString();
        return Results.File(
            output.ToArray(),
            contentType: contentType,
            fileDownloadName: AddLanguageSuffix(file.FileName, targetLang!));
    }

    private static string AddLanguageSuffix(string filename, string lang)
    {
        var dot = filename.LastIndexOf('.');
        if (dot < 0) return $"{filename}.{lang}";
        var name = filename[..dot];
        var ext = filename[dot..];
        return $"{name}.{lang}{ext}";
    }
}
