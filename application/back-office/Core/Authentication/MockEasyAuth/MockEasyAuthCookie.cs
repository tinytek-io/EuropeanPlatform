using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace BackOffice.Authentication.MockEasyAuth;

public static class MockEasyAuthCookie
{
    public const string CookieName = "__Host-DevEasyAuth";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string EncodePayload(MockEasyAuthIdentity identity)
    {
        var claims = new List<BackOfficeClientPrincipalClaim>
        {
            new() { Type = BackOfficeIdentityDefaults.NameClaimType, Value = identity.Name },
            new() { Type = ClaimTypes.Email, Value = identity.Email },
            new() { Type = ClaimTypes.NameIdentifier, Value = identity.ObjectId }
        };
        claims.AddRange(identity.Groups.Select(group => new BackOfficeClientPrincipalClaim
                {
                    Type = BackOfficeIdentityDefaults.GroupsClaimType,
                    Value = group
                }
            )
        );

        var principal = new BackOfficeClientPrincipal
        {
            AuthenticationType = "aad",
            NameType = ClaimTypes.Name,
            RoleType = ClaimTypes.Role,
            Claims = claims.ToArray()
        };
        var json = JsonSerializer.Serialize(principal, JsonOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
