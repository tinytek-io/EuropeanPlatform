using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using SharedKernel.OpenIdConnect;

namespace BackOffice.Authentication;

public static class BackOfficeOidcDefaults
{
    // The session cookie the OIDC sign-in lands in. The back-office authorization policies include
    // this scheme alongside the header scheme, so a principal from either source authorizes.
    public const string CookieScheme = "BackOfficeOidcCookie";

    public const string OidcScheme = "BackOfficeOidc";

    public const string CookieName = "__Host-BackOfficeOidc";
}

public static class BackOfficeOidcExtensions
{
    /// <summary>
    ///     True when a staff identity provider is configured. Takes precedence over the mock identity
    ///     selector: when an OIDC provider exists, the mock is disabled regardless of MockEasyAuth:Enabled.
    /// </summary>
    public static bool IsBackOfficeOidcConfigured(this IConfiguration configuration)
    {
        return configuration.GetSection(BackOfficeOidcOptions.SectionName).Get<BackOfficeOidcOptions>()?.IsConfigured == true;
    }

    /// <summary>
    ///     Registers the OIDC authentication handler and its session cookie for the back-office.
    ///     No-op unless BackOffice:Oidc is fully configured. The callback path reuses the Easy
    ///     Auth-style path the SPA already redirects through (/.auth/login/aad/callback), so the
    ///     SPA needs no changes: an unauthenticated browser visit is redirected to /.auth/login/aad
    ///     by the back-office identity handler, which challenges the provider and returns. The
    ///     identity handler authenticates the session cookie, so the back-office authorization
    ///     policies stay pinned to the single identity scheme.
    /// </summary>
    public static IServiceCollection AddBackOfficeOidcIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(BackOfficeOidcOptions.SectionName).Get<BackOfficeOidcOptions>();
        if (options?.IsConfigured != true)
        {
            return services;
        }

        services
            .AddAuthentication()
            .AddCookie(BackOfficeOidcDefaults.CookieScheme, ConfigureBackOfficeOidcSessionCookie)
            .AddOpenIdConnect(BackOfficeOidcDefaults.OidcScheme, oidc =>
                {
                    oidc.Authority = options.Authority;
                    oidc.ClientId = options.ClientId;
                    oidc.ClientSecret = options.ClientSecret;
                    oidc.ResponseType = OpenIdConnectResponseType.Code;
                    oidc.UsePkce = true;
                    oidc.CallbackPath = BackOfficeIdentityDefaults.CallbackPath;
                    oidc.SignInScheme = BackOfficeOidcDefaults.CookieScheme;
                    oidc.SaveTokens = false;
                    oidc.Scope.Clear();
                    oidc.Scope.Add(OpenIdConnectScope.OpenIdProfile);
                    oidc.Scope.Add(OpenIdConnectScope.Email);
                    // The admin gate matches the 'groups' claim against BackOffice:AdminsGroupId;
                    // request the groups scope when that gate is configured (providers that do not
                    // know the scope ignore it).
                    if (!string.IsNullOrWhiteSpace(configuration["BackOffice:AdminsGroupId"]))
                    {
                        oidc.Scope.Add("groups");
                    }

                    // Dev-only escape hatch for the local Authelia harness, whose certificate is
                    // self-signed: the backchannel (discovery, token, userinfo) then tolerates it.
                    // Production providers have trusted certificates and leave this unset.
                    if (configuration.GetSection("BackOffice:Oidc:DevAllowUntrustedCertificate").Get<bool>())
                    {
                        oidc.BackchannelHttpHandler = new HttpClientHandler
                        {
                            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                        };
                    }
                }
            );

        return services;
    }

    /// <summary>
    ///     Options for the back-office OIDC session cookie. Only registered when an OIDC provider
    ///     is configured; without a sign-in no cookie exists, and the identity handler falls back
    ///     to NoResult.
    /// </summary>
    public static void ConfigureBackOfficeOidcSessionCookie(CookieAuthenticationOptions cookie)
    {
        cookie.Cookie.Name = BackOfficeOidcDefaults.CookieName;
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        cookie.Cookie.SameSite = SameSiteMode.Lax; // the IdP redirect back is a cross-site top-level GET
        cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
        cookie.SlidingExpiration = true;
        // Challenge/forbidden behavior stays with the back-office identity handler
        // (friendly redirects for browsers); the cookie scheme stays silent.
        cookie.LoginPath = null;
        cookie.AccessDeniedPath = null;
    }

    /// <summary>
    ///     Maps the Easy Auth-style login/logout endpoints that redirect into the provider. Only
    ///     mapped when the provider is configured; otherwise the mock identity selector owns these
    ///     paths. The callback itself is consumed by the OIDC handler, not an endpoint.
    /// </summary>
    public static IEndpointRouteBuilder MapBackOfficeOidcEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(BackOfficeIdentityDefaults.LoginPath,
                (string? postLoginRedirectUri) =>
                {
                    var candidate = postLoginRedirectUri ?? "/";
                    var redirectUri = ReturnPathHelper.IsValidRelativePath(candidate) ? candidate : "/";
                    return Results.Challenge(
                        new AuthenticationProperties { RedirectUri = redirectUri },
                        [BackOfficeOidcDefaults.OidcScheme]
                    );
                }
            )
            .DisableAntiforgery();

        routes.MapGet(BackOfficeIdentityDefaults.LogoutPath,
                async (HttpContext context, string? postLogoutRedirectUri) =>
                {
                    var candidate = postLogoutRedirectUri ?? "/";
                    var redirectUri = ReturnPathHelper.IsValidRelativePath(candidate) ? candidate : "/";
                    await context.SignOutAsync(BackOfficeOidcDefaults.CookieScheme);
                    await context.SignOutAsync(BackOfficeOidcDefaults.OidcScheme,
                        new AuthenticationProperties { RedirectUri = redirectUri }
                    );
                }
            )
            .DisableAntiforgery();

        return routes;
    }
}
