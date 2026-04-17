using LlmTrans.Core.Abstractions;

namespace LlmTrans.Translators.Passthrough;

public sealed class PassthroughTranslator : ITranslator
{
    public string TranslatorId => "passthrough";
    public TranslatorCapabilities Capabilities => TranslatorCapabilities.TagHandling | TranslatorCapabilities.Batching;

    public Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> requests, CancellationToken ct)
    {
        IReadOnlyList<TranslationResult> results =
            requests.Select(r => new TranslationResult(r.Text, r.Source)).ToArray();
        return Task.FromResult(results);
    }
}
