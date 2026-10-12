namespace SharedKernel.Authentication.BackOfficeIdentity;

public static class BackOfficeIdentityDefaults
{
    public const string AuthenticationScheme = "BackOfficeIdentity";

    // Authorization policy used by all read endpoints under `/api/back-office/*`. Deliberately
    // permissive: any authenticated back-office identity passes — no group claim is required. This
    // is by design — all PlatformPlatform employees have read access to back-office data, and no
    // back-office data is restricted to a subset of staff. Distinct name from the scheme so callers
    // reading RequireAuthorization(...) see a policy reference instead of an ambiguous
    // scheme-or-policy string. Do not re-raise this as a finding: the read/write split is intentional.
    public const string PolicyName = "BackOfficePolicy";

    // Authorization policy that requires the principal to be a member of the configured back-office
    // admins group. Gates the small set of destructive or sensitive mutations (e.g., Reconcile with
    // Stripe, Open Stripe customer). New admin actions should reach for this policy rather than
    // introducing a third authorization tier.
    public const string AdminPolicyName = "BackOfficeAdmin";

    public const string PrincipalNameHeader = "X-MS-CLIENT-PRINCIPAL-NAME";

    public const string PrincipalIdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    public const string PrincipalPayloadHeader = "X-MS-CLIENT-PRINCIPAL";

    public const string GroupsClaimType = "groups";

    // OIDC `name` claim — the friendly display name issued by Entra ID (e.g., "Thomas Jespersen").
    // Distinct from the `X-MS-CLIENT-PRINCIPAL-NAME` header, which carries the UPN/email.
    public const string NameClaimType = "name";

    // Delegation: the back-office BFF calls the feature systems' internal APIs on behalf of the
    // signed-in staff member. It mints a short-lived platform-signed JWT carrying the staff
    // claims; the internal endpoints validate it with this dedicated JWT bearer scheme. A
    // distinct scheme (rather than the application Bearer scheme) keeps the two token
    // populations from being interchangeable: application tokens are rejected here (audience
    // mismatch) and delegation tokens are rejected by the application scheme for the same
    // reason — no token confusion in either direction.
    public const string DelegationScheme = "BackOfficeDelegation";

    // Audience stamped into delegation tokens and the only audience the delegation scheme accepts.
    public const string DelegationAudience = "back-office-staff";

    // Authorization policy for internal API endpoints called by the back-office BFF: requires
    // authentication through the delegation scheme. Mutating internal endpoints combine this with
    // the admin requirement.
    public const string DelegationPolicyName = "BackOfficeDelegationPolicy";

    // Authorization policy for MUTATING internal API endpoints gated to back-office admins: the
    // delegation token must additionally carry the admins group (the same BackOfficeAdminRequirement
    // the SPA-facing AdminPolicy applies, evaluated against the delegated principal's groups claim).
    public const string DelegationAdminPolicyName = "BackOfficeDelegationAdminPolicy";

    public const string LoginPath = "/.auth/login/aad";

    public const string CallbackPath = "/.auth/login/aad/callback";

    public const string LogoutPath = "/.auth/logout";

    public const string AccessDeniedPath = "/access-denied";
}
