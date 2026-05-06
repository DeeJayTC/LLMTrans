using AdaptiveApi.Api.Auth;
using Microsoft.Extensions.Configuration;

namespace AdaptiveApi.Api.Admin;

/// Tiny system / status endpoints the admin UI uses to render banners and the
/// production-readiness panel. Read-only; no secrets returned.
public static class SystemEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/system");
        g.MapGet("/auth-status", AuthStatus);
        g.MapGet("/readiness", Readiness);
        g.MapPost("/oidc-validate", ValidateOidc);
    }

    public sealed record ValidateOidcRequest(string Authority, string? ClientId, string? Audience);
    public sealed record ValidateOidcResponse(
        bool Ok, string? Issuer, string? AuthorizationEndpoint, string? TokenEndpoint,
        string? JwksUri, string[] ScopesSupported, string? Error);

    /// Hits <c>{authority}/.well-known/openid-configuration</c> and reports
    /// whether the discovery document looks usable. Doesn't store anything;
    /// purely a "does this URL respond?" probe so an admin can validate
    /// their config before flipping <c>AdaptiveApi:Auth:Mode</c> to
    /// <c>oidc</c> and restarting.
    private static async Task<IResult> ValidateOidc(
        ValidateOidcRequest req,
        IHttpClientFactory http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Authority))
            return Results.Ok(new ValidateOidcResponse(false, null, null, null, null, Array.Empty<string>(),
                "authority required"));

        var url = req.Authority.TrimEnd('/') + "/.well-known/openid-configuration";
        var client = http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        try
        {
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return Results.Ok(new ValidateOidcResponse(false, null, null, null, null,
                    Array.Empty<string>(), $"discovery document returned {(int)resp.StatusCode}"));

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            var issuer = root.TryGetProperty("issuer", out var iss) ? iss.GetString() : null;
            var authEp = root.TryGetProperty("authorization_endpoint", out var ae) ? ae.GetString() : null;
            var tokenEp = root.TryGetProperty("token_endpoint", out var te) ? te.GetString() : null;
            var jwks = root.TryGetProperty("jwks_uri", out var jw) ? jw.GetString() : null;
            var scopes = Array.Empty<string>();
            if (root.TryGetProperty("scopes_supported", out var ss) && ss.ValueKind == System.Text.Json.JsonValueKind.Array)
                scopes = ss.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToArray();

            // The minimum we need to drive sign-in is issuer + token_endpoint
            // + jwks_uri. Authorization_endpoint is OIDC-required but not
            // strictly needed for the bearer path.
            if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(tokenEp) || string.IsNullOrEmpty(jwks))
                return Results.Ok(new ValidateOidcResponse(false, issuer, authEp, tokenEp, jwks, scopes,
                    "discovery document missing one of issuer / token_endpoint / jwks_uri"));

            return Results.Ok(new ValidateOidcResponse(true, issuer, authEp, tokenEp, jwks, scopes, null));
        }
        catch (TaskCanceledException)
        {
            return Results.Ok(new ValidateOidcResponse(false, null, null, null, null,
                Array.Empty<string>(), "timeout fetching discovery document"));
        }
        catch (HttpRequestException ex)
        {
            return Results.Ok(new ValidateOidcResponse(false, null, null, null, null,
                Array.Empty<string>(), $"http error: {ex.Message}"));
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.Ok(new ValidateOidcResponse(false, null, null, null, null,
                Array.Empty<string>(), "discovery document was not valid JSON"));
        }
    }

    public sealed record AuthStatusDto(string Mode, bool IsOpen);

    /// Surfaces the active auth mode so the UI can show a "Authentication is
    /// off" banner when the host is wide-open. <c>IsOpen</c> is what the UI
    /// branches on; <c>Mode</c> is for display.
    private static IResult AuthStatus(AuthOptions opts)
    {
        var mode = string.IsNullOrEmpty(opts.Mode) ? "none" : opts.Mode.ToLowerInvariant();
        var isOpen = string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase);
        return Results.Ok(new AuthStatusDto(mode, isOpen));
    }

    public sealed record ReadinessCheck(string Id, string Name, string Severity, string Status, string? Detail);

    public sealed record ReadinessDto(IReadOnlyList<ReadinessCheck> Checks);

    /// Production-readiness checklist. Each check returns a <c>Status</c> of
    /// "ok" / "warn" / "error" plus a one-liner. The UI shows them with
    /// red / yellow / green indicators on the Settings page so a user moving
    /// from local-dev to production can see what's still in dev posture
    /// without grepping config files. No secrets in any field.
    private static IResult Readiness(AuthOptions auth, IConfiguration cfg)
    {
        var checks = new List<ReadinessCheck>();

        // Auth mode.
        var mode = string.IsNullOrEmpty(auth.Mode) ? "none" : auth.Mode.ToLowerInvariant();
        checks.Add(new ReadinessCheck(
            "auth-mode",
            "Authentication mode",
            Severity: "blocker",
            Status: mode == "none" ? "error" : "ok",
            Detail: mode == "none"
                ? "Auth is off — anyone reaching the admin URL has admin access."
                : $"OIDC mode active (authority: {auth.Authority})."));

        // Default translator. `passthrough` is a no-op — fine for routing-only,
        // surfaced as a warning since the LLM language gate doesn't apply.
        var defaultTranslator = cfg.GetValue<string>("Translators:Default") ?? "passthrough";
        checks.Add(new ReadinessCheck(
            "default-translator",
            "Default translator",
            Severity: "warning",
            Status: defaultTranslator == "passthrough" ? "warn" : "ok",
            Detail: defaultTranslator == "passthrough"
                ? "Passthrough — bodies are forwarded unchanged. Set Translators:Default to 'deepl' or 'llm' to translate."
                : $"Configured: {defaultTranslator}."));

        // Dev-fixed route token.
        var fixedToken = cfg.GetValue<string>("Dev:FixedRouteToken");
        checks.Add(new ReadinessCheck(
            "dev-route-token",
            "Dev route token",
            Severity: "warning",
            Status: string.IsNullOrEmpty(fixedToken) ? "ok" : "warn",
            Detail: string.IsNullOrEmpty(fixedToken)
                ? "Not set — seeder generates a strong token at startup."
                : "Dev:FixedRouteToken is set. Unset it in production so the seeder generates a per-deployment token."));

        // Demo payload logging — the demo profile may inject this.
        var demoIncludePayloads = cfg.GetValue<bool?>("Demo:IncludePayloads");
        if (demoIncludePayloads is true)
        {
            checks.Add(new ReadinessCheck(
                "demo-payloads",
                "Demo payload logging",
                Severity: "warning",
                Status: "warn",
                Detail: "Demo:IncludePayloads is true — raw bodies are surfaced in pipeline logs. Set to false before exposing the demo to non-dev traffic."));
        }
        else
        {
            checks.Add(new ReadinessCheck(
                "demo-payloads",
                "Demo payload logging",
                Severity: "warning",
                Status: "ok",
                Detail: "Off."));
        }

        // ASPNETCORE_ENVIRONMENT — informational; flag Development as warn.
        var env = cfg.GetValue<string>("ASPNETCORE_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        checks.Add(new ReadinessCheck(
            "environment",
            "ASP.NET environment",
            Severity: "info",
            Status: env.Equals("Production", StringComparison.OrdinalIgnoreCase) ? "ok" : "warn",
            Detail: $"ASPNETCORE_ENVIRONMENT={env}."));

        return Results.Ok(new ReadinessDto(checks));
    }
}
