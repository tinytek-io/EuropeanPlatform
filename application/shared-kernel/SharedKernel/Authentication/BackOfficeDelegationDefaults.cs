namespace SharedKernel.Authentication;

/// <summary>
///     The vocabulary of the back-office delegation contract: the JWT bearer scheme that validates
///     the short-lived tokens the back-office BFF mints from its signed-in staff member, the
///     authorization policies the feature systems' internal APIs pin their routes to, and the claim
///     types both sides of the contract share. This is the cross-system seam — the staff identity
///     machinery itself (the identity handler, the mock selector, the OIDC login) lives in the
///     back-office system, not here.
/// </summary>
public static class BackOfficeDelegationDefaults
{
    // The JWT bearer scheme validating delegation tokens on internal API routes. A distinct scheme
    // with its own key pair, issuer, and audience keeps the two token populations apart: application
    // tokens (HMAC, platform issuer) are rejected here, and delegation tokens (ES256) are rejected by
    // the application scheme.
    public const string DelegationScheme = "BackOfficeDelegation";

    // Issuer stamped into delegation tokens and the only issuer the delegation scheme accepts.
    public const string DelegationIssuer = "back-office";

    // The delegation key pair: only the back-office holds the private key (it signs), the feature
    // systems hold the public key (they validate). A compromised back-office can therefore mint
    // delegation tokens but never application tokens, and a feature system cannot mint either.
    public const string SigningKeyConfigurationKey = "BackOfficeDelegation:SigningKeyPem";

    public const string PublicKeyConfigurationKey = "BackOfficeDelegation:PublicKeyPem";

    // Audience stamped into delegation tokens and the only audience the delegation scheme accepts.
    public const string DelegationAudience = "back-office-staff";

    // Authorization policy for internal API endpoints called by the back-office BFF: requires
    // authentication through the delegation scheme.
    public const string DelegationPolicyName = "BackOfficeDelegationPolicy";

    // Authorization policy for MUTATING internal API endpoints gated to back-office admins: the
    // delegation token must additionally carry the admins group.
    public const string DelegationAdminPolicyName = "BackOfficeDelegationAdminPolicy";

    // The groups claim the admin gate matches against BackOffice:AdminsGroupId — present both in
    // the staff identity's claims and, via the delegation token, on the internal API's principal.
    public const string GroupsClaimType = "groups";

    // The friendly display name claim, as carried in the staff identity payload and re-stamped into
    // the delegation token.
    public const string NameClaimType = "name";
}
