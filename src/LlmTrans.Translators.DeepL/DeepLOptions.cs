namespace LlmTrans.Translators.DeepL;

public sealed class DeepLOptions
{
    public string? ApiKey { get; set; }
    /// "https://api.deepl.com/" (Pro) or "https://api-free.deepl.com/" (Free).
    public string BaseUrl { get; set; } = "https://api.deepl.com/";
    /// Global default system context prepended to DeepL's `context` parameter.
    /// Overridden per-route by ProxyRule.SystemContext. Max 4 000 characters.
    public string? SystemContext { get; set; }
}
