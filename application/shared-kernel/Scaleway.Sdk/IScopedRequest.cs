namespace Scaleway.Sdk;

/// <summary>
///     A generated request whose body declares <c>project_id</c>. The non-generic form allows a caller to ask
///     whether the field is already set without knowing the concrete request type — needed to honour Scaleway's
///     mutually-exclusive <c>ProjectIdentifier</c> pairs.
/// </summary>
public interface IProjectScopedRequest
{
    /// <summary>The project already on the request, or null. A non-null value is the caller's and is never overwritten.</summary>
    string? ProjectId { get; }
}

/// <summary>
///     Scaleway requires <c>project_id</c> on these operations even where its schema does not say so, and its own
///     SDKs fill it from client configuration rather than making the caller repeat it — this is the seam that lets
///     a project-scoped client do the same. The self type parameter keeps injection type-safe: <c>WithProjectId</c>
///     returns the concrete request, so no reflection and no cast is involved.
/// </summary>
public interface IProjectScopedRequest<out TSelf> : IProjectScopedRequest where TSelf : IProjectScopedRequest<TSelf>
{
    /// <summary>Returns a copy carrying the project. The original is unchanged — these requests are records.</summary>
    /// <param name="projectId">The project to stamp on the copy.</param>
    TSelf WithProjectId(string projectId);
}

/// <summary>A generated request whose body declares <c>organization_id</c> — the org-scoped IAM operations.</summary>
public interface IOrganizationScopedRequest
{
    /// <summary>The organization already on the request, or null. A non-null value is the caller's and is never overwritten.</summary>
    string? OrganizationId { get; }
}

/// <summary>
///     A request declaring both this and <see cref="IProjectScopedRequest{TSelf}" /> is one of Scaleway's
///     mutually-exclusive <c>ProjectIdentifier</c> pairs, so exactly one of the two may be filled.
/// </summary>
public interface IOrganizationScopedRequest<out TSelf> : IOrganizationScopedRequest where TSelf : IOrganizationScopedRequest<TSelf>
{
    /// <summary>
    ///     Returns a copy carrying the organization. On a <c>ProjectIdentifier</c> pair the project is filled first
    ///     and this then no-ops, so the two are never both set.
    /// </summary>
    /// <param name="organizationId">The organization to stamp on the copy.</param>
    TSelf WithOrganizationId(string organizationId);
}
