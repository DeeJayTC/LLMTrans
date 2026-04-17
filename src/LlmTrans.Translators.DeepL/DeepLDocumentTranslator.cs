using DeepL;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CoreLanguageCode = LlmTrans.Core.Model.LanguageCode;
using SdkFormality = DeepL.Formality;

namespace LlmTrans.Translators.DeepL;

/// Exposes DeepL's document translation (`/v2/document`) surface via the official
/// SDK. Use this for batch translation of DOCX / PPTX / PDF / HTML / plain-text / XLIFF
/// files where a whole-document round-trip is more ergonomic than pipeline-based
/// translation. The API endpoint (`POST /admin/translate-document`) thin-wraps this.
public sealed class DeepLDocumentTranslator
{
    private readonly IOptions<DeepLOptions> _options;
    private readonly ILogger<DeepLDocumentTranslator> _log;
    private readonly Lazy<DeepLClient> _client;

    public DeepLDocumentTranslator(IOptions<DeepLOptions> options, ILogger<DeepLDocumentTranslator> log)
    {
        _options = options;
        _log = log;
        _client = new Lazy<DeepLClient>(CreateClient, isThreadSafe: true);
    }

    /// Uploads the supplied document, polls until translation completes, and writes
    /// the result to `outputStream`. Caller owns both streams.
    public async Task<DocumentTranslationResult> TranslateAsync(
        Stream input,
        string filename,
        Stream output,
        CoreLanguageCode target,
        CoreLanguageCode? source = null,
        string? glossaryId = null,
        string? formality = null,
        CancellationToken ct = default)
    {
        var client = _client.Value;

        var options = new DocumentTranslateOptions();
        if (!string.IsNullOrEmpty(glossaryId)) options.GlossaryId = glossaryId;
        if (!string.IsNullOrEmpty(formality)
            && Enum.TryParse<SdkFormality>(formality, true, out var f))
            options.Formality = f;

        try
        {
            await client.TranslateDocumentAsync(
                input,
                filename,
                output,
                source is null ? null : source.Value.Value.ToUpperInvariant(),
                NormalizeDeeplTarget(target.Value),
                options,
                cancellationToken: ct);

            return new DocumentTranslationResult(Ok: true, BilledCharacters: 0, ErrorMessage: null);
        }
        catch (DocumentTranslationException ex)
        {
            // Exposes status.BilledCharacters + status.DocumentHandle so operators can debug.
            _log.LogError(ex,
                "DeepL document translate failed: status={Status} billed={Billed}",
                ex.DocumentHandle?.DocumentId, ex.InnerException?.Message);
            return new DocumentTranslationResult(Ok: false, BilledCharacters: 0, ErrorMessage: ex.Message);
        }
        catch (DeepLException ex)
        {
            _log.LogError(ex, "DeepL document translate failed for target={Target}", target.Value);
            return new DocumentTranslationResult(Ok: false, BilledCharacters: 0, ErrorMessage: ex.Message);
        }
    }

    /// DeepL deprecated bare two-letter codes where a regional variant matters.
    /// Mirrors `DeepLTranslator.NormalizeTarget`.
    private static string NormalizeDeeplTarget(string value)
    {
        if (string.IsNullOrEmpty(value)) return "EN-US";
        var upper = value.ToUpperInvariant();
        return upper switch
        {
            "EN" => "EN-US",
            "PT" => "PT-BR",
            _ => upper,
        };
    }

    private DeepLClient CreateClient()
    {
        var apiKey = _options.Value.ApiKey
            ?? throw new InvalidOperationException("DeepL API key not configured");

        if (!string.IsNullOrEmpty(_options.Value.BaseUrl))
        {
            var clientOptions = new DeepLClientOptions { ServerUrl = _options.Value.BaseUrl };
            return new DeepLClient(apiKey, clientOptions);
        }
        return new DeepLClient(apiKey);
    }
}

public sealed record DocumentTranslationResult(bool Ok, long BilledCharacters, string? ErrorMessage);
