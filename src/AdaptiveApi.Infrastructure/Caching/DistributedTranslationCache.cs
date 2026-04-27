using System.Text;
using AdaptiveApi.Core.Pipeline;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace AdaptiveApi.Infrastructure.Caching;

public sealed class DistributedTranslationCacheOptions
{
    /// Default sliding-or-absolute TTL for cached translations. Defaults to 7 days.
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Adapts <see cref="IDistributedCache"/> (Redis, in-memory, etc.) to the
/// <see cref="ITranslationCache"/> contract used by <see cref="TranslationPipeline"/>.
/// Cache failures are swallowed and logged so a degraded cache never breaks
/// translation requests.
/// </summary>
public sealed class DistributedTranslationCache : ITranslationCache
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<DistributedTranslationCache> _log;
    private readonly DistributedTranslationCacheOptions _options;

    public DistributedTranslationCache(
        IDistributedCache cache,
        ILogger<DistributedTranslationCache> log,
        DistributedTranslationCacheOptions? options = null)
    {
        _cache = cache;
        _log = log;
        _options = options ?? new DistributedTranslationCacheOptions();
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            var bytes = await _cache.GetAsync(key, ct);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "translation cache get failed; treating as miss");
            return null;
        }
    }

    public async Task SetAsync(string key, string translatedText, CancellationToken ct)
    {
        try
        {
            var entryOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _options.TimeToLive
            };
            await _cache.SetAsync(key, Encoding.UTF8.GetBytes(translatedText), entryOptions, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "translation cache set failed; continuing without cache");
        }
    }
}
