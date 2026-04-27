namespace AdaptiveApi.Core.Pipeline;

/// <summary>
/// Optional cache for completed translations. Implementations are expected to be
/// safe under concurrent access. Keys are opaque, stable, deterministic strings
/// composed by <see cref="TranslationPipeline"/> from the translator id, language
/// pair, glossary, style, formality, model type, and the tokenized source text.
/// Cache values are translated strings (still containing placeholder markers that
/// the pipeline reinjects after the lookup).
/// </summary>
public interface ITranslationCache
{
    /// Returns the translated text for the given key, or <c>null</c> on cache miss.
    Task<string?> GetAsync(string key, CancellationToken ct);

    /// Stores the translated text under the given key. Implementations decide TTL.
    Task SetAsync(string key, string translatedText, CancellationToken ct);
}

/// <summary>No-op cache. Wired by default when no distributed cache is configured.</summary>
public sealed class NoopTranslationCache : ITranslationCache
{
    public Task<string?> GetAsync(string key, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task SetAsync(string key, string translatedText, CancellationToken ct) => Task.CompletedTask;
}
