using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AdaptiveApi.Api.Auth;

public enum AuthMode { None, Oidc }

public sealed class AuthOptions
{
    /// `none` (default — open dev mode) | `oidc`
    public string Mode { get; set; } = "none";
    public string Authority { get; set; } = "";
    public string Audience { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// Claim name holding the tenant id, if the IdP carries it. Empty falls back to
    /// `Seeder.DevTenantId` (single-tenant dev behaviour).
    public string TenantClaim { get; set; } = "";
    public string AdminRole { get; set; } = "admin";
    public bool RequireHttpsMetadata { get; set; } = true;
    public string CookieName { get; set; } = "adaptiveapi.session";
    public string[] Scopes { get; set; } = new[] { "openid", "profile", "email" };
}

public static class AuthSetup
{
    public const string AdminPolicy = "adaptiveapi-admin";
    public const string CookieScheme = "adaptiveapi-cookie";

    public static AuthMode Configure(WebApplicationBuilder builder)
    {
        var opts = new AuthOptions();
        builder.Configuration.GetSection("AdaptiveApi:Auth").Bind(opts);
        var mode = string.Equals(opts.Mode, "oidc", StringComparison.OrdinalIgnoreCase)
            ? AuthMode.Oidc
            : AuthMode.None;

        builder.Services.AddSingleton(opts);

        if (mode == AuthMode.None)
        {
            // Still register the auth primitives so `[Authorize]` doesn't throw;
            // the admin-policy grants everything under this mode.
            builder.Services.AddAuthentication();
            builder.Services.AddAuthorizationBuilder()
                .AddPolicy(AdminPolicy, p => p.RequireAssertion(_ => true));
            return mode;
        }

        if (string.IsNullOrEmpty(opts.Authority))
            throw new InvalidOperationException("AdaptiveApi:Auth:Mode=oidc requires AdaptiveApi:Auth:Authority");

        builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(CookieScheme, o =>
            {
                o.Cookie.Name = opts.CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = opts.RequireHttpsMetadata
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;
            })
            .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                o.Authority = opts.Authority;
                o.ClientId = opts.ClientId;
                o.ClientSecret = string.IsNullOrEmpty(opts.ClientSecret) ? null : opts.ClientSecret;
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.UsePkce = true;
                o.SaveTokens = true;
                o.RequireHttpsMetadata = opts.RequireHttpsMetadata;
                o.GetClaimsFromUserInfoEndpoint = true;
                foreach (var s in opts.Scopes) o.Scope.Add(s);
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = "name",
                    RoleClaimType = "roles",
                };
            })
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                // Bearer scheme for programmatic admin clients (CI, scripts).
                // UI uses the cookie scheme via the OIDC code flow.
                o.Authority = opts.Authority;
                o.Audience = string.IsNullOrEmpty(opts.Audience) ? opts.ClientId : opts.Audience;
                o.RequireHttpsMetadata = opts.RequireHttpsMetadata;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = "sub",
                    RoleClaimType = "roles",
                };
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, p =>
            {
                p.RequireAuthenticatedUser();
                p.AddAuthenticationSchemes(CookieScheme, JwtBearerDefaults.AuthenticationScheme);
                if (!string.IsNullOrEmpty(opts.AdminRole))
                    p.RequireAssertion(ctx =>
                        ctx.User.IsInRole(opts.AdminRole)
                        || ctx.User.HasClaim("role", opts.AdminRole)
                        || ctx.User.HasClaim("roles", opts.AdminRole));
            });

        return mode;
    }

    /// Wires `/auth/me`, `/auth/login`, `/auth/logout`. Safe no-ops under AuthMode.None.
    public static void MapAuthEndpoints(WebApplication app, AuthMode mode, AuthOptions opts)
    {
        app.MapGet("/admin/auth/me", (HttpContext ctx) =>
        {
            if (mode == AuthMode.None)
                return Results.Ok(new MeDto(Authenticated: false, Mode: "none",
                    Name: null, Email: null, TenantId: null, Roles: Array.Empty<string>()));

            var user = ctx.User;
            if (!(user.Identity?.IsAuthenticated ?? false))
                return Results.Ok(new MeDto(Authenticated: false, Mode: "oidc",
                    Name: null, Email: null, TenantId: null, Roles: Array.Empty<string>()));

            var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value)
                .Concat(user.FindAll("role").Select(c => c.Value))
                .Concat(user.FindAll("roles").Select(c => c.Value))
                .Distinct().ToArray();
            var tenantId = !string.IsNullOrEmpty(opts.TenantClaim)
                ? user.FindFirst(opts.TenantClaim)?.Value
                : null;

            return Results.Ok(new MeDto(
                Authenticated: true,
                Mode: "oidc",
                Name: user.Identity?.Name,
                Email: user.FindFirst("email")?.Value ?? user.FindFirst(ClaimTypes.Email)?.Value,
                TenantId: tenantId,
                Roles: roles));
        });

        if (mode == AuthMode.Oidc)
        {
            app.MapGet("/admin/auth/login", (string? returnUrl) =>
                Results.Challenge(
                    new Microsoft.AspNetCore.Authentication.AuthenticationProperties
                        { RedirectUri = returnUrl ?? "/" },
                    new[] { OpenIdConnectDefaults.AuthenticationScheme }));

            app.MapPost("/admin/auth/logout", () =>
                Results.SignOut(
                    new Microsoft.AspNetCore.Authentication.AuthenticationProperties
                        { RedirectUri = "/" },
                    new[] { CookieScheme, OpenIdConnectDefaults.AuthenticationScheme }));
        }
    }

    public sealed record MeDto(
        bool Authenticated,
        string Mode,
        string? Name,
        string? Email,
        string? TenantId,
        IReadOnlyList<string> Roles);
}
