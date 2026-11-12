using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SharedKernel.Authentication;

/// <summary>
///     Mints the short-lived delegation token the back-office BFF presents to the feature systems'
///     internal APIs: the staff principal (established by the BackOfficeIdentity scheme — the mock
///     identity selector or a staff OIDC provider) is re-signed as an ES256 JWT carrying the staff
///     claims, with the back-office's private delegation key. The internal endpoints validate it with
///     the BackOfficeDelegation scheme against the public key, so in-cluster callers cannot forge a
///     staff identity, and the back-office holds no key that could mint application tokens.
/// </summary>
public sealed class BackOfficeDelegationTokenGenerator(IConfiguration configuration, TimeProvider timeProvider)
{
    // One request → one token: the lifetime only has to cover clock skew and the in-flight call.
    private const int ValidForMinutes = 2;

    private readonly Lazy<SigningCredentials> _signingCredentials = new(() =>
        new SigningCredentials(BackOfficeDelegationKeys.LoadSigningKey(configuration), BackOfficeDelegationKeys.Algorithm)
    );

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
            claims.Add(new Claim(BackOfficeDelegationDefaults.NameClaimType, displayName));
        }

        var email = principal.FindFirstValue(ClaimTypes.Email);
        if (!string.IsNullOrWhiteSpace(email))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, email));
        }

        claims.AddRange(principal.Claims
            .Where(claim => claim.Type == BackOfficeDelegationDefaults.GroupsClaimType)
            .Select(claim => new Claim(BackOfficeDelegationDefaults.GroupsClaimType, claim.Value))
        );

        var now = timeProvider.GetUtcNow();
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            NotBefore = now.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Expires = now.AddMinutes(ValidForMinutes).UtcDateTime,
            Issuer = BackOfficeDelegationDefaults.DelegationIssuer,
            Audience = BackOfficeDelegationDefaults.DelegationAudience,
            SigningCredentials = _signingCredentials.Value
        };

        return new JsonWebTokenHandler().CreateToken(tokenDescriptor);
    }
}
