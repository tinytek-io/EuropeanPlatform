using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BackOffice.Authentication.MockEasyAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BackOffice.Authentication;

public sealed class BackOfficeIdentityHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (PrefersHtml(Request))
        {
            var redirectTarget = $"{Request.Path}{Request.QueryString}";
            var loginUrl = $"{BackOfficeIdentityDefaults.LoginPath}?post_login_redirect_uri={Uri.EscapeDataString(redirectTarget)}";
            Response.Redirect(loginUrl);
            return Task.CompletedTask;
        }

        Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        if (PrefersHtml(Request))
        {
            Response.Redirect(BackOfficeIdentityDefaults.AccessDeniedPath);
            return Task.CompletedTask;
        }

        Response.StatusCode = (int)HttpStatusCode.Forbidden;
        return Task.CompletedTask;
    }

    // Staff identity never comes from request headers: the mock selector hands its cookie-resolved
    // identity over in-process (HttpContext.Items, unreachable from the network), and otherwise the
    // staff session cookie authenticates. Any client-supplied identity header is ignored.
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var mockIdentity = MockEasyAuthIdentities.GetAttached(Context);
        if (mockIdentity is not null)
        {
            return AuthenticateResult.Success(new AuthenticationTicket(BuildMockPrincipal(mockIdentity), Scheme.Name));
        }

        return await TryAuthenticateStaffSessionAsync();
    }

    private async Task<AuthenticateResult> TryAuthenticateStaffSessionAsync()
    {
        // RequestServices is null in bare unit-test contexts (the nullable contract claims
        // otherwise): nothing to fall back to there.
        var services = (IServiceProvider?)Context.RequestServices;
        if (services is null)
        {
            return AuthenticateResult.NoResult();
        }

        var schemeProvider = services.GetRequiredService<IAuthenticationSchemeProvider>();
        if (await schemeProvider.GetSchemeAsync(StaffSessionDefaults.Scheme) is null)
        {
            return AuthenticateResult.NoResult();
        }

        var result = await Context.AuthenticateAsync(StaffSessionDefaults.Scheme);
        return result.Succeeded
            ? AuthenticateResult.Success(new AuthenticationTicket(result.Principal!, Scheme.Name))
            : AuthenticateResult.NoResult();
    }

    private static ClaimsPrincipal BuildMockPrincipal(MockEasyAuthIdentity identity)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, identity.Name),
            new(ClaimTypes.NameIdentifier, identity.ObjectId),
            new(ClaimTypes.Email, identity.Email)
        };
        claims.AddRange(identity.Groups.Select(group => new Claim(BackOfficeIdentityDefaults.GroupsClaimType, group)));

        var claimsIdentity = new ClaimsIdentity(claims, BackOfficeIdentityDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(claimsIdentity);
    }

    private static bool PrefersHtml(HttpRequest request)
    {
        var accept = request.Headers.Accept.ToString();
        if (string.IsNullOrEmpty(accept)) return false;
        return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }
}
