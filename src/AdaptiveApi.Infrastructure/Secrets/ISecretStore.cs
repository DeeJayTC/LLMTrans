namespace AdaptiveApi.Infrastructure.Secrets;

/// At-rest-encrypted key/value secret store. Used to keep translator API
/// keys (DeepL, LLM translator) out of <c>.env</c> files / process
/// environment variables. The host's KEK (Key Encryption Key) wraps each
/// stored secret via AES-GCM; the KEK lives outside the DB (env or KMS).
///
/// When the KEK is not configured, the store reports
/// <see cref="IsAvailable"/> = false and writes throw — the host still
/// works because translator config falls back to <c>IConfiguration</c>
/// values from env / appsettings.
public interface ISecretStore
{
    bool IsAvailable { get; }

    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    Task<IReadOnlyList<SecretSummary>> ListAsync(CancellationToken ct);
}

/// Listing entry — never includes the plaintext or ciphertext, just enough
/// to render an admin UI ("which keys exist, when were they last set").
public sealed record SecretSummary(string Key, DateTimeOffset UpdatedAt, int KeyVersion);

/// Stable secret keys. Centralised so the admin UI, the post-configure
/// hooks, and the readiness panel agree on names. Translator-side keys
/// live under <c>translator.</c> so other secret kinds (auth signing keys,
/// KMS handles) can take their own prefix later without colliding.
public static class SecretKeys
{
    public const string DeeplApiKey = "translator.deepl.apikey";
    public const string LlmTranslatorApiKey = "translator.llm.apikey";
}
