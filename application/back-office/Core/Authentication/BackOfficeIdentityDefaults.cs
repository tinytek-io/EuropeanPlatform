using SharedKernel.Authentication;

namespace BackOffice.Authentication;

/// <summary>
///     The back-office's staff identity vocabulary: the authentication scheme, the SPA-facing
///     authorization policies, the EasyAuth-style header names, and the login routes. The
///     cross-system delegation contract (the internal API's token scheme and policies, the shared
///     claim types) lives in SharedKernel's BackOfficeDelegationDefaults.
/// </summary>
public static class BackOfficeIdentityDefaults
{
    public const string AuthenticationScheme = "BackOfficeIdentity";

    // Authorization policy used by all read endpoints under `/api/back-office/*`. Deliberately
    // permissive: any authenticated back-office identity passes — no group claim is required. This
    // is by design — all EuropeanPlatform employees have read access to back-office data, and no
    // back-office data is restricted to a subset of staff. The read/write split is intentional.
    public const string PolicyName = "BackOfficePolicy";

    // Authorization policy that requires the principal to be a member of the configured back-office
    // admins group. Gates the small set of destructive or sensitive mutations.
    public const string AdminPolicyName = "BackOfficeAdmin";

    public const string PrincipalNameHeader = "X-MS-CLIENT-PRINCIPAL-NAME";

    public const string PrincipalIdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    public const string PrincipalPayloadHeader = "X-MS-CLIENT-PRINCIPAL";

    // The groups claim the staff identity carries and the admin gates match (both the SPA-facing
    // admin policy here and, via the delegation token, the internal API's admin gate).
    public const string GroupsClaimType = BackOfficeDelegationDefaults.GroupsClaimType;

    // OIDC `name` claim — the friendly display name, preferred over the principal name header
    // (which carries the UPN/email) when present.
    public const string NameClaimType = BackOfficeDelegationDefaults.NameClaimType;

    public const string LoginPath = "/.auth/login/aad";

    public const string CallbackPath = "/.auth/login/aad/callback";

    public const string LogoutPath = "/.auth/logout";

    public const string AccessDeniedPath = "/access-denied";
}
