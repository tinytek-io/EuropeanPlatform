using System.Security.Claims;
using System.Security.Cryptography;
using BackOffice.Authentication;
using BackOffice.Database;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using SharedKernel.Authentication;
using SharedKernel.Configuration;
using SharedKernel.SinglePageApp;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace BackOffice.StaffIdentity;

/// <summary>
///     The staff OIDC provider: other tools (Forgejo, ...) sign staff in through the back-office, on OpenIddict. The
///     authorization code flow only, for the confidential clients listed in the committed staff identity file; a
///     signed-in staff member is asked nothing (the client list is the consent), and someone outside a client's allowed
///     groups is refused. Codes and access tokens are protected with the shared Data Protection keys, so any replica
///     redeems them; ID tokens are RS256 JWTs signed with the environment's staff identity signing key.
/// </summary>
public static class StaffIdentityProvider
{
    public const string GroupsScope = "groups";

    public const string AuthorizePath = "/connect/authorize";

    public const string TokenPath = "/connect/token";

    public const string UserInfoPath = "/connect/userinfo";

    public const string EndSessionPath = "/connect/logout";

    /// <summary>The RSA private key (PKCS#8 PEM) that signs ID tokens; Terraform generates it per environment.</summary>
    public const string SigningKeyConfigurationKey = "StaffIdentity:SigningKeyPem";

    /// <summary>Each client's secret, by client id (StaffIdentity:ClientSecrets:&lt;clientId&gt;); Terraform generates them.</summary>
    public const string ClientSecretsSection = "StaffIdentity:ClientSecrets";

    /// <summary>
    ///     The provider's store and the staff directory, without the server: what the back-office workers need to sync
    ///     the committed client list into the store.
    /// </summary>
    public static IServiceCollection AddStaffIdentityClientSync(this IServiceCollection services)
    {
        services.AddStaffDirectory();
        services.AddOpenIddict().AddCore(options => options.UseEntityFrameworkCore().UseDbContext<BackOfficeDbContext>());
        return services.AddTransient<StaffIdentityClientReconciler>();
    }

    public static IServiceCollection AddStaffIdentityProvider(this IServiceCollection services)
    {
        services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<BackOfficeDbContext>())
            .AddServer(options =>
                {
                    options.SetAuthorizationEndpointUris(AuthorizePath[1..])
                        .SetTokenEndpointUris(TokenPath[1..])
                        .SetUserInfoEndpointUris(UserInfoPath[1..])
                        .SetEndSessionEndpointUris(EndSessionPath[1..]);

                    // The authorization code flow, with PKCE (S256 only) honored when a client sends it; the clients
                    // are confidential, so the code also needs the client secret.
                    options.AllowAuthorizationCodeFlow();
                    options.Configure(serverOptions => serverOptions.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));

                    options.RegisterScopes(Scopes.Email, Scopes.Profile, GroupsScope);

                    // Codes and access tokens use the shared Data Protection keys, so every replica redeems them.
                    // OpenIddict still needs an encryption key to start; nothing is encrypted with it.
                    options.UseDataProtection();
                    options.AddEphemeralEncryptionKey();

                    options.UseAspNetCore()
                        .EnableAuthorizationEndpointPassthrough()
                        .EnableTokenEndpointPassthrough()
                        .EnableUserInfoEndpointPassthrough()
                        .EnableEndSessionEndpointPassthrough();
                }
            );

        // Deferred to first use, so a process that serves nothing (the build-time OpenAPI generator) never needs the
        // signing key, and every configuration source (Secret Manager, a test host's) is in place.
        services.AddOptions<OpenIddictServerOptions>().Configure<IConfiguration, IOptions<BackOfficeHostOptions>>((options, configuration, hostOptions) =>
            {
                options.Issuer = new Uri(Environment.GetEnvironmentVariable(SinglePageAppConfiguration.PublicUrlKey) ?? $"https://{hostOptions.Value.Host}");
                options.SigningCredentials.Add(new SigningCredentials(LoadSigningKey(configuration), SecurityAlgorithms.RsaSha256));
            }
        );

        return services;
    }

    public static IEndpointRouteBuilder MapStaffIdentityProviderEndpoints(this IEndpointRouteBuilder routes, string backOfficeHost)
    {
        routes.MapMethods(AuthorizePath, [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync).RequireHost(backOfficeHost).AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();
        routes.MapPost(TokenPath, ExchangeAsync).RequireHost(backOfficeHost).AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();
        routes.MapMethods(UserInfoPath, [HttpMethods.Get, HttpMethods.Post], UserInfoAsync).RequireHost(backOfficeHost).AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();
        // Cast: a handler taking only HttpContext would otherwise bind as a RequestDelegate, which discards the result.
        routes.MapMethods(EndSessionPath, [HttpMethods.Get, HttpMethods.Post], (Delegate)EndSessionAsync).RequireHost(backOfficeHost).AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();
        return routes;
    }

    private static async Task<IResult> AuthorizeAsync(HttpContext context, StaffDirectory staffDirectory)
    {
        var request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // Not signed in: the back-office sign-in, which returns here (with the same request) once a session exists.
        var session = await context.AuthenticateAsync(StaffSessionDefaults.Scheme);
        var staffMemberId = session.Succeeded ? session.Principal.FindFirstValue(ClaimTypes.NameIdentifier) : null;
        var member = staffMemberId is null ? null : staffDirectory.FindActiveById(staffMemberId);
        if (member is null)
        {
            var returnPath = $"{context.Request.Path}{context.Request.QueryString}";
            return Results.Redirect($"/login?returnPath={Uri.EscapeDataString(returnPath)}");
        }

        var client = staffDirectory.FindClient(request.ClientId!);
        if (client is null || !member.Groups.Intersect(client.AllowedGroups).Any())
        {
            return Forbid(Errors.AccessDenied, "You are not allowed to sign in to this application.");
        }

        return Results.SignIn(CreatePrincipal(member, request.GetScopes()), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> ExchangeAsync(HttpContext context, StaffDirectory staffDirectory)
    {
        var request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");
        if (!request.IsAuthorizationCodeGrantType())
        {
            return Forbid(Errors.UnsupportedGrantType, "Only the authorization code grant is supported.");
        }

        // The code is valid (OpenIddict checked it, its client, and PKCE); the staff identity file is asked again, so
        // someone deactivated since the code was issued gets no tokens, and the claims are current.
        var code = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var member = code.Principal?.GetClaim(Claims.Subject) is { } subject ? staffDirectory.FindActiveById(subject) : null;
        var client = staffDirectory.FindClient(request.ClientId!);
        if (member is null || client is null || !member.Groups.Intersect(client.AllowedGroups).Any())
        {
            return Forbid(Errors.InvalidGrant, "The staff member may no longer sign in to this application.");
        }

        return Results.SignIn(CreatePrincipal(member, code.Principal!.GetScopes()), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> UserInfoAsync(HttpContext context, StaffDirectory staffDirectory)
    {
        var token = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var member = token.Principal?.GetClaim(Claims.Subject) is { } subject ? staffDirectory.FindActiveById(subject) : null;
        if (member is null)
        {
            return Results.Challenge(
                new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The staff member is no longer active."
                    }
                ),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]
            );
        }

        var scopes = token.Principal!.GetScopes();
        var claims = new Dictionary<string, object> { [Claims.Subject] = member.Id };
        if (scopes.Contains(Scopes.Profile))
        {
            claims[Claims.Name] = member.Name;
            claims[Claims.PreferredUsername] = member.Id;
        }

        if (scopes.Contains(Scopes.Email))
        {
            claims[Claims.Email] = member.Email;
            claims[Claims.EmailVerified] = true;
        }

        if (scopes.Contains(GroupsScope))
        {
            claims[GroupsScope] = member.Groups;
        }

        return Results.Ok(claims);
    }

    private static async Task<IResult> EndSessionAsync(HttpContext context)
    {
        await context.SignOutAsync(StaffSessionDefaults.Scheme);
        return Results.SignOut(new AuthenticationProperties { RedirectUri = "/login" }, [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    /// <summary>
    ///     The identity a client receives: the stable staff id as subject, and the profile, email, and groups claims
    ///     when their scopes were requested, in both the ID token and the access token (for userinfo).
    /// </summary>
    private static ClaimsPrincipal CreatePrincipal(StaffMember member, IEnumerable<string> requestedScopes)
    {
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        var scopes = requestedScopes.Intersect([Scopes.OpenId, Scopes.Profile, Scopes.Email, GroupsScope]).ToArray();

        identity.SetClaim(Claims.Subject, member.Id);
        if (scopes.Contains(Scopes.Profile))
        {
            identity.SetClaim(Claims.Name, member.Name).SetClaim(Claims.PreferredUsername, member.Id);
        }

        if (scopes.Contains(Scopes.Email))
        {
            identity.SetClaim(Claims.Email, member.Email).SetClaim(Claims.EmailVerified, true);
        }

        if (scopes.Contains(GroupsScope))
        {
            identity.SetClaims(GroupsScope, [..member.Groups]);
        }

        identity.SetScopes(scopes);
        identity.SetDestinations(_ => [Destinations.AccessToken, Destinations.IdentityToken]);
        return new ClaimsPrincipal(identity);
    }

    private static IResult Forbid(string error, string description)
    {
        return Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
                }
            ),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]
        );
    }

    // Deployed environments sign with the Terraform-generated key every replica shares (and clients cache through the
    // JWKS endpoint). Local development, which has none, signs with a key that lives as long as the process.
    private static RsaSecurityKey LoadSigningKey(IConfiguration configuration)
    {
        var rsa = RSA.Create(2048);
        var pem = configuration[SigningKeyConfigurationKey];
        if (!string.IsNullOrWhiteSpace(pem))
        {
            rsa.ImportFromPem(pem);
        }
        else if (SharedInfrastructureConfiguration.IsRunningInCloud)
        {
            throw new InvalidOperationException($"'{SigningKeyConfigurationKey}' is required: the staff OIDC provider signs ID tokens with it.");
        }

        var modulus = rsa.ExportParameters(false).Modulus!;
        return new RsaSecurityKey(rsa) { KeyId = Base64UrlEncoder.Encode(SHA256.HashData(modulus))[..16] };
    }
}
