using Microsoft.AspNetCore.Http;
using SharedKernel.Authentication;

namespace SharedKernel.SinglePageApp;

/// <summary>
///     Describes a single SPA inside a multi-SPA host. Each entry maps a hostname to its bundle directory and
///     decides how to derive the <see cref="UserInfo" /> embedded in the served HTML.
///     One <see cref="HostScopedSinglePageApp" /> per <c>RequireHost</c>-scoped fallback registered by
///     <c>UseHostScopedSinglePageAppFallback</c>; the consolidated <c>account-api</c> hosts both
///     <c>account/WebApp</c> and the back-office system's <c>WebApp</c> through this mechanism.
/// </summary>
public sealed class HostScopedSinglePageApp(
    string host,
    string webAppProjectName,
    Func<HttpContext, UserInfo> userInfoFactory,
    string? publicUrl = null,
    string? cdnUrl = null,
    string? authorizationPolicy = null,
    Dictionary<string, string>? environmentVariables = null,
    string[]? unauthenticatedPaths = null
)
{
    /// <summary>The hostname this SPA is served on (used for <c>RequireHost</c>).</summary>
    public string Host { get; } = host;

    /// <summary>Folder name inside <c>application/&lt;scs&gt;/</c> that holds the SPA's <c>dist/</c>.</summary>
    public string WebAppProjectName { get; } = webAppProjectName;

    /// <summary>
    ///     Returns the <see cref="UserInfo" /> embedded into the HTML for this host. The account host uses
    ///     <c>IExecutionContext.UserInfo</c>; the back-office host derives display name and groups from the
    ///     staff identity claims (see <c>BackOfficeDelegationDefaults.GroupsClaimType</c>).
    /// </summary>
    public Func<HttpContext, UserInfo> UserInfoFactory { get; } = userInfoFactory;

    /// <summary>
    ///     Per-host <c>PUBLIC_URL</c> override embedded into <c>StaticRuntimeEnvironment</c>. Required when a
    ///     single process hosts multiple SPAs on different hostnames so each renders bundle URLs scoped to its
    ///     own host. Falls back to the process-wide <c>PUBLIC_URL</c> environment variable when null.
    /// </summary>
    public string? PublicUrl { get; } = publicUrl;

    /// <summary>
    ///     Per-host <c>CDN_URL</c> override (paired with <see cref="PublicUrl" />). Falls back to the
    ///     process-wide <c>CDN_URL</c> environment variable when null.
    /// </summary>
    public string? CdnUrl { get; } = cdnUrl;

    /// <summary>
    ///     Authorization policy applied to this host's <c>MapFallback</c>. Required for hosts whose SPA shell
    ///     must authenticate the principal before <see cref="UserInfoFactory" /> runs (e.g. back-office).
    ///     Null leaves the fallback anonymous, which is the correct setting for hosts whose API endpoints
    ///     are gated separately and whose SPA shell should render unauthenticated.
    /// </summary>
    public string? AuthorizationPolicy { get; } = authorizationPolicy;

    /// <summary>Per-SPA runtime environment variables embedded into the HTML alongside <c>userInfo</c>.</summary>
    public Dictionary<string, string>? EnvironmentVariables { get; } = environmentVariables;

    /// <summary>
    ///     Paths under this host that must render the SPA shell WITHOUT applying <see cref="AuthorizationPolicy" />.
    ///     Used for routes the SPA itself owns that need to be reachable while unauthenticated (e.g. the
    ///     development-only mock-login picker on the back-office host). Each entry must be an absolute path
    ///     starting with <c>/</c>. Has no observable effect when <see cref="AuthorizationPolicy" /> is null,
    ///     since the fallback is already anonymous.
    /// </summary>
    public string[] UnauthenticatedPaths { get; } = unauthenticatedPaths ?? [];
}
