using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SharedKernel.Authentication.TokenSigning;

namespace SharedKernel.Authentication.BackOfficeIdentity;

/// <summary>
///     Mints the short-lived delegation token the back-office BFF presents to the feature systems'
///     internal APIs: the staff principal (established by the BackOfficeIdentity scheme — mock Easy
///     Auth or a staff OIDC provider) is re-signed as a platform JWT carrying the staff claims.
///     The internal endpoints validate it with the BackOfficeDelegation scheme, so in-cluster
///     callers cannot forge a staff identity by injecting EasyAuth-style headers.
/// </summary>
public sealed class BackOfficeDelegationTokenGenerator(ITokenSigningClient tokenSigningClient, TimeProvider timeProvider)
{
    // One request → one token: the lifetime only has to cover clock skew and the in-flight call.
    private const int ValidForMinutes = 2;

    public string Generate(ClaimsPrincipal principal)
    {
        var claims = new List<Claim>();

        var objectId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(objectId))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, objectId));
        }

        var displayName = principal.FindFirstValue(ClaimTypes.Name);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            claims.Add(new Claim(BackOfficeIdentityDefaults.NameClaimType, displayName));
        }

        var email = principal.FindFirstValue(ClaimTypes.Email);
        if (!string.IsNullOrWhiteSpace(email))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, email));
        }

        claims.AddRange(principal.Claims
            .Where(claim => claim.Type == BackOfficeIdentityDefaults.GroupsClaimType)
            .Select(claim => new Claim(BackOfficeIdentityDefaults.GroupsClaimType, claim.Value)));

        var now = timeProvider.GetUtcNow();
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            NotBefore = now.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Expires = now.AddMinutes(ValidForMinutes).UtcDateTime,
            Issuer = tokenSigningClient.Issuer,
            Audience = BackOfficeIdentityDefaults.DelegationAudience,
            SigningCredentials = tokenSigningClient.GetSigningCredentials()
        };

        return new JsonWebTokenHandler().CreateToken(tokenDescriptor);
    }
}
