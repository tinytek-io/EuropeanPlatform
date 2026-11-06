using ScwIam = Scaleway.Sdk.Iam.V1Alpha1;

namespace Scaleway.Sdk;

/// <summary>
///     Account-scoped entry point to Scaleway: the operations that need only an organization. Project-scoped
///     resources are reached through <see cref="WithProject" />, so an operation that requires a project cannot
///     be called without supplying one — including the chicken-and-egg case, where a project is created from here
///     and the returned ID is then scoped into.
///     <para>
///         This is the opinionated surface. The generated <c>Scaleway.Sdk.*</c> clients remain
///         available underneath for anything this deliberately narrows.
///     </para>
/// </summary>
public sealed class ScalewayClient : IDisposable
{
    private readonly ScalewayApiClient? _ownedTransport;

    /// <summary>Builds a client owning its own transport, which <see cref="Dispose" /> closes.</summary>
    /// <param name="credentials">Base address, secret key, organization and the default region.</param>
    public ScalewayClient(ScalewayCredentialConfig credentials)
        : this(new ScalewayApiClient(credentials), credentials.OrganizationId, credentials.DefaultRegion)
    {
        _ownedTransport = (ScalewayApiClient)Transport;
    }

    internal ScalewayClient(IScalewayApiClient transport, OrganizationId organizationId, ScalewayRegion defaultRegion = ScalewayRegion.FrPar)
    {
        Transport = transport;
        OrganizationId = organizationId;
        DefaultRegion = defaultRegion;
        // The account scope has no project, so it fills organization_id only. IAM is organization-global, so the
        // region default never reaches a URL from here, but it flows into project scopes narrowed from this client.
        Iam = new ScwIam.IamApi(transport, new ScalewayRequestScope(organizationId.Value, region: defaultRegion.ToApiString()));
    }

    internal IScalewayApiClient Transport { get; }

    /// <summary>The organization every call from this client is scoped to. Narrowing to a project does not change it.</summary>
    public OrganizationId OrganizationId { get; }

    /// <summary>The region filled into region-scoped operations when a call passes none.</summary>
    public ScalewayRegion DefaultRegion { get; }

    /// <summary>
    ///     Identity and Access Management — organization-scoped, so it lives on the account client. Requests that
    ///     declare <c>organization_id</c> have it filled from this client's organization automatically.
    /// </summary>
    public ScwIam.IamApi Iam { get; }

    /// <summary>Closes the transport when this client built it. A client given one shares it and leaves it open.</summary>
    public void Dispose()
    {
        _ownedTransport?.Dispose();
    }

    /// <summary>
    ///     Narrows this client to a single project. The returned client exposes the project-scoped resources and
    ///     fills <c>project_id</c> on the requests that declare it, the way Scaleway's own SDKs fill it from client
    ///     configuration.
    /// </summary>
    public ScalewayProjectClient WithProject(ProjectId projectId)
    {
        return new ScalewayProjectClient(this, projectId);
    }
}
