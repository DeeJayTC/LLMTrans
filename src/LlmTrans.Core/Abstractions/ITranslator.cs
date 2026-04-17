using LlmTrans.Core.Model;

namespace LlmTrans.Core.Abstractions;

public interface ITranslator
{
    string TranslatorId { get; }
    TranslatorCapabilities Capabilities { get; }

    Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> requests,
        CancellationToken ct);
}

public sealed record TranslationRequest(
    string Text,
    LanguageCode Source,
    LanguageCode Target,
    string? GlossaryId = null,
    Formality Formality = Formality.Default,
    TagHandling TagHandling = TagHandling.Xml,
    IReadOnlyCollection<string>? DoNotTranslate = null,
    string? Context = null,
    string? StyleRuleId = null,
    IReadOnlyList<string>? CustomInstructions = null,
    string? ModelType = null);

public sealed record TranslationResult(
    string Text,
    LanguageCode? DetectedSource = null);

public enum Formality { Default, More, Less, PreferMore, PreferLess }
public enum TagHandling { None, Xml, Html }

[Flags]
public enum TranslatorCapabilities
{
    None = 0,
    Glossary = 1 << 0,
    Formality = 1 << 1,
    TagHandling = 1 << 2,
    Batching = 1 << 3,
    AutoDetect = 1 << 4,
    DocumentApi = 1 << 5,
    StyleRules = 1 << 6,
    CustomInstructions = 1 << 7,
    MultilingualGlossary = 1 << 8,
}
