namespace Scaleway.Sdk;

/// <summary>
///     The identity a generated client fills into requests that declare it — so a caller never repeats
///     <c>project_id</c> / <c>organization_id</c>, and forgetting one can't produce a 400. Passed to a generated
///     <c>&lt;Service&gt;Api</c>; the generated Create/Update/List methods apply it before sending. Mirrors how
///     Scaleway's own SDKs default these from client configuration.
///     <para>
///         An account-scoped client has no <see cref="ProjectId" />. A project-scoped one has both; where a request
///         accepts either (Scaleway's mutually-exclusive <c>ProjectIdentifier</c> pair) the project wins and the
///         organization is left unset, so the pair can never be violated by injection.
///     </para>
///     <para>
///         <see cref="Region" /> / <see cref="Zone" /> are the defaults for the URL path segment of
///         region/zone-scoped operations. A generated method's own <c>region</c>/<c>zone</c> argument always wins;
///         the scope only fills in when the caller passes none, and a raw client (no scope) requires the argument.
///     </para>
/// </summary>
public sealed class ScalewayRequestScope(string organizationId, string? projectId = null, string? region = null, string? zone = null)
{
    /// <summary>Always present: every scope belongs to an organization, even one with no project.</summary>
    public string OrganizationId { get; } = organizationId;

    /// <summary>Null on an account-scoped client, which is what makes project-scoped operations unreachable from one.</summary>
    public string? ProjectId { get; } = projectId;

    /// <summary>Default for region-scoped paths. Null means every such call must name its own region.</summary>
    public string? Region { get; } = region;

    /// <summary>Default for zone-scoped paths, which are the minority — most Scaleway APIs scope by region.</summary>
    public string? Zone { get; } = zone;

    /// <summary>Fills <c>project_id</c> when this scope has a project and the request has not set one.</summary>
    public TRequest ApplyProject<TRequest>(TRequest request) where TRequest : IProjectScopedRequest<TRequest>
    {
        return ProjectId is not null && request.ProjectId is null ? request.WithProjectId(ProjectId) : request;
    }

    /// <summary>
    ///     Fills <c>organization_id</c> when the request has not set one — unless it is already project-scoped, in
    ///     which case the two are mutually exclusive and the project takes precedence.
    /// </summary>
    public TRequest ApplyOrganization<TRequest>(TRequest request) where TRequest : IOrganizationScopedRequest<TRequest>
    {
        if (request.OrganizationId is not null)
        {
            return request;
        }

        if (request is IProjectScopedRequest { ProjectId: not null })
        {
            return request;
        }

        return request.WithOrganizationId(OrganizationId);
    }
}
