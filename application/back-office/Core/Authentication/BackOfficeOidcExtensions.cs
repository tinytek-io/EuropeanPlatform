using System.Security.Claims;
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

        var staffGroupId = options.StaffGroupId;
        if (string.IsNullOrWhiteSpace(staffGroupId))
        {
            throw new InvalidOperationException("BackOffice:Oidc:StaffGroupId is required when a staff identity provider is configured: signing in to the provider alone must not grant staff access.");
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
                    // Current providers (Authelia 4.39+, Entra ID for large group sets) deliver
                    // groups, name, and email through the userinfo endpoint rather than the ID
                    // token. The claim actions land them under the claim types the staff context
                    // and the staff/admin gates read; the unique mappings never duplicate a value
                    // the ID token already carried.
                    oidc.GetClaimsFromUserInfoEndpoint = true;
                    oidc.ClaimActions.MapJsonKey(BackOfficeIdentityDefaults.GroupsClaimType, "groups");
                    oidc.ClaimActions.MapUniqueJsonKey(ClaimTypes.Name, "name");
                    oidc.ClaimActions.MapUniqueJsonKey(ClaimTypes.Email, "email");
                    oidc.Scope.Clear();
                    oidc.Scope.Add(OpenIdConnectScope.OpenIdProfile);
                    oidc.Scope.Add(OpenIdConnectScope.Email);
                    // The staff gate below and the admin gate match the 'groups' claim (providers
                    // that do not know the scope ignore it).
                    oidc.Scope.Add("groups");

                    // Only members of the staff group get a back-office session: anyone else is sent
                    // to the access-denied page before the session cookie is issued.
                    oidc.Events.OnTicketReceived = context =>
                    {
                        var isStaff = context.Principal?.Claims.Any(claim => claim.Type == BackOfficeIdentityDefaults.GroupsClaimType && claim.Value == staffGroupId) == true;
                        if (!isStaff)
                        {
                            context.Response.Redirect(BackOfficeIdentityDefaults.AccessDeniedPath);
                            context.HandleResponse();
                        }

                        return Task.CompletedTask;
                    };

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
    ///     Maps the login/logout endpoints that redirect into the provider. Only
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
