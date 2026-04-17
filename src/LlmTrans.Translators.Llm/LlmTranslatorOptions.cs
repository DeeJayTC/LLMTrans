namespace LlmTrans.Translators.Llm;

public sealed class LlmTranslatorOptions
{
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://api.openai.com/";
    public string Model { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0;
    public int MaxAttempts { get; set; } = 2;
}
