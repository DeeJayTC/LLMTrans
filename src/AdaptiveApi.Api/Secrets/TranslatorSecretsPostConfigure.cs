using AdaptiveApi.Infrastructure.Secrets;
using AdaptiveApi.Translators.DeepL;
using AdaptiveApi.Translators.Llm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AdaptiveApi.Api.Secrets;

/// Pulls the DeepL / LLM translator API keys out of the secret store and
/// overrides the corresponding options after they're bound from
/// <c>IConfiguration</c>. PostConfigure runs every time options are
/// resolved through <see cref="IOptionsMonitor{T}"/>, so invalidating the
/// monitor cache after a secret write makes the next resolve see the new
/// value (see <see cref="Admin.SecretEndpoints"/>).
///
/// If the store has no value for a key, the env / appsettings value passes
/// through untouched. If the store is unavailable (no KEK), this is a no-op.
///
/// We resolve <see cref="ISecretStore"/> per call from a fresh scope rather
/// than capturing it in the constructor, because the store is itself
/// scoped (it owns a <c>DbContext</c>) while the post-configure host is
/// singleton.
public sealed class DeepLOptionsPostConfigure : IPostConfigureOptions<DeepLOptions>
{
    private readonly IServiceProvider _services;
    public DeepLOptionsPostConfigure(IServiceProvider services) => _services = services;

    public void PostConfigure(string? name, DeepLOptions options)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        if (!store.IsAvailable) return;
        // Synchronous because IPostConfigureOptions is sync-only. The store
        // hits one indexed row in SQLite — fine to block briefly during
        // options resolution (which itself is rare with monitor caching).
        var fromStore = store.GetAsync(SecretKeys.DeeplApiKey, CancellationToken.None)
            .ConfigureAwait(false).GetAwaiter().GetResult();
        if (!string.IsNullOrEmpty(fromStore)) options.ApiKey = fromStore;
    }
}

public sealed class AdaptiveApilatorOptionsPostConfigure : IPostConfigureOptions<AdaptiveApilatorOptions>
{
    private readonly IServiceProvider _services;
    public AdaptiveApilatorOptionsPostConfigure(IServiceProvider services) => _services = services;

    public void PostConfigure(string? name, AdaptiveApilatorOptions options)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        if (!store.IsAvailable) return;
        var fromStore = store.GetAsync(SecretKeys.LlmTranslatorApiKey, CancellationToken.None)
            .ConfigureAwait(false).GetAwaiter().GetResult();
        if (!string.IsNullOrEmpty(fromStore)) options.ApiKey = fromStore;
    }
}
