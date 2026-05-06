using AdaptiveApi.Infrastructure.Secrets;
using AdaptiveApi.Translators.DeepL;
using AdaptiveApi.Translators.Llm;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AdaptiveApi.Api.Admin;

/// CRUD-ish endpoints for the encrypted secret store. Plaintext values are
/// only ever written in (PUT) — never read back. The list endpoint returns
/// metadata only, so even an admin UI session can't dump existing secrets.
public static class SecretEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/secrets");
        g.MapGet("", List);
        g.MapPut("/{key}", Put);
        g.MapDelete("/{key}", Delete);
    }

    public sealed record SecretSummaryDto(string Key, DateTimeOffset UpdatedAt, int KeyVersion);
    public sealed record SecretListDto(bool StoreAvailable, IReadOnlyList<SecretSummaryDto> Secrets);
    public sealed record PutSecretDto(string Value);

    private static async Task<IResult> List(ISecretStore store, CancellationToken ct)
    {
        var secrets = await store.ListAsync(ct);
        return Results.Ok(new SecretListDto(
            store.IsAvailable,
            secrets.Select(s => new SecretSummaryDto(s.Key, s.UpdatedAt, s.KeyVersion)).ToList()));
    }

    private static async Task<IResult> Put(
        string key, [FromBody] PutSecretDto body,
        ISecretStore store,
        IOptionsMonitorCache<DeepLOptions> deeplCache,
        IOptionsMonitorCache<AdaptiveApilatorOptions> llmCache,
        CancellationToken ct)
    {
        if (!store.IsAvailable)
            return Results.BadRequest(new { error = "store_unavailable",
                message = "configure AdaptiveApi:Secrets:Kek to enable the secret store" });
        if (string.IsNullOrEmpty(body.Value))
            return Results.BadRequest(new { error = "empty_value" });

        await store.SetAsync(key, body.Value, ct);

        // Bust the options monitor cache so the next translator resolve picks
        // up the new key without a process restart. Keys we know about are
        // listed in SecretKeys.
        if (key == SecretKeys.DeeplApiKey) deeplCache.TryRemove(Options.DefaultName);
        else if (key == SecretKeys.LlmTranslatorApiKey) llmCache.TryRemove(Options.DefaultName);

        return Results.NoContent();
    }

    private static async Task<IResult> Delete(
        string key, ISecretStore store,
        IOptionsMonitorCache<DeepLOptions> deeplCache,
        IOptionsMonitorCache<AdaptiveApilatorOptions> llmCache,
        CancellationToken ct)
    {
        await store.DeleteAsync(key, ct);
        if (key == SecretKeys.DeeplApiKey) deeplCache.TryRemove(Options.DefaultName);
        else if (key == SecretKeys.LlmTranslatorApiKey) llmCache.TryRemove(Options.DefaultName);
        return Results.NoContent();
    }
}
