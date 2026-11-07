namespace BackOffice.Authentication;

/// <summary>
///     Configuration for the back-office staff identity provider. Any standard OpenID Connect
///     provider works (Entra ID, Google Workspace, Auth0, ...): set Authority/ClientId/ClientSecret,
///     StaffGroupId to the 'groups' claim value every staff member carries, and, if admin actions
///     should be available, BackOffice:AdminsGroupId to the value the provider
///     issues in the 'groups' claim for staff administrators. When unconfigured, the mock identity
///     selector applies (local development and explicitly opted-in environments).
/// </summary>
public sealed class BackOfficeOidcOptions
{
    public const string SectionName = "BackOffice:Oidc";

    /// <summary>The provider's base URL with a discoverable .well-known/openid-configuration.</summary>
    public string? Authority { get; init; }

    public string? ClientId { get; init; }

    /// <summary>Comes from Secret Manager in deployed workloads (BackOffice__Oidc__ClientSecret).</summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    ///     The 'groups' claim value every staff member carries. Required with a provider: signing in
    ///     to the identity provider must not by itself grant staff access (a multi-tenant or consumer
    ///     provider authenticates people who are not staff).
    /// </summary>
    public string? StaffGroupId { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Authority)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);
}
