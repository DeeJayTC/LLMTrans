namespace AdaptiveApi.Pii.Presidio;

public sealed class PresidioOptions
{
    /// Base URL of a Presidio Analyzer service — usually the sidecar container running
    /// `mcr.microsoft.com/presidio-analyzer`.
    public string AnalyzerUrl { get; set; } = "http://presidio-analyzer:3000";
    /// Language Presidio should analyse as (default English). BCP-47 code.
    public string Language { get; set; } = "en";
    /// Only redact spans whose score ≥ this threshold. Presidio returns 0.0 – 1.0.
    public double MinScore { get; set; } = 0.5;
    public int TimeoutMs { get; set; } = 5000;
}
