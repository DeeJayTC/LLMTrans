using AdaptiveApi.Core.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveApi.Infrastructure.Caching;

public static class TranslationCacheRegistration
{
    /// <summary>
    /// Wires <see cref="ITranslationCache"/>. When a Redis connection string is
    /// configured under <c>Redis:ConnectionString</c>, registers a Redis-backed
    /// distributed cache. Otherwise registers a no-op cache (translations always
    /// hit the upstream translator). The TTL defaults to 7 days and can be
    /// overridden via <c>Redis:TranslationCacheTtl</c> (TimeSpan).
    /// </summary>
    public static IServiceCollection AddAdaptiveApiTranslationCache(this IServiceCollection services)
    {
        services.AddSingleton<ITranslationCache>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var redisCs = cfg.GetValue<string>("Redis:ConnectionString");
            if (string.IsNullOrWhiteSpace(redisCs)) return new NoopTranslationCache();

            var ttl = cfg.GetValue<TimeSpan?>("Redis:TranslationCacheTtl") ?? TimeSpan.FromDays(7);
            var distributed = sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DistributedTranslationCache>>();
            return new DistributedTranslationCache(distributed, log, new DistributedTranslationCacheOptions { TimeToLive = ttl });
        });

        return services;
    }

    /// <summary>
    /// Conditionally registers <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>:
    /// uses Redis when <c>Redis:ConnectionString</c> is set, otherwise leaves the
    /// service unregistered. Call <see cref="AddAdaptiveApiTranslationCache"/>
    /// after this so the cache adapter sees whatever was registered.
    /// </summary>
    public static IServiceCollection AddAdaptiveApiDistributedCache(this IServiceCollection services, IConfiguration configuration)
    {
        var redisCs = configuration.GetValue<string>("Redis:ConnectionString");
        if (!string.IsNullOrWhiteSpace(redisCs))
        {
            var instance = configuration.GetValue<string>("Redis:InstanceName") ?? "adaptiveapi:";
            services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = redisCs;
                o.InstanceName = instance;
            });
        }
        return services;
    }
}
