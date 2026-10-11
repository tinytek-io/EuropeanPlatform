using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using SharedKernel.Authentication.BackOfficeIdentity;

namespace BackOffice.Authentication;

/// <summary>
///     Attaches the delegation token to every internal API call: a short-lived platform-signed JWT
///     minted from the signed-in staff principal, validated by the feature systems' internal
///     endpoints (BackOfficeIdentityDefaults.DelegationScheme). The token is minted per request —
///     no caching — so group membership changes take effect on the next call.
/// </summary>
public sealed class BackOfficeDelegationHandler(
    IHttpContextAccessor httpContextAccessor,
    BackOfficeDelegationTokenGenerator tokenGenerator
) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal is not { Identity.IsAuthenticated: true })
        {
            throw new InvalidOperationException(
                "The back-office BFF may only call internal APIs on behalf of an authenticated staff principal.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenGenerator.Generate(principal));
        return base.SendAsync(request, cancellationToken);
    }
}
