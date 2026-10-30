using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharedKernel.Authentication;
using SharedKernel.OpenApi;
using SharedKernel.SinglePageApp;
using Xunit;

namespace Account.Tests.ArchitectureTests;

// Walks the actual registered endpoints in Account.Api and enforces:
// - The staff-facing back-office routes live in the back-office system; the routes here are the
//   INTERNAL API the back-office BFF consumes, and their security boundary is the delegation
//   policy (a platform-signed short-lived JWT minted by the BFF) — not a RequireHost match.
// - Public /api/account/* endpoints intentionally do NOT declare RequireHost. Internal-only ACA apps
//   sit behind AppGateway and ACA's internal ingress; the host header from upstream proxies cannot
//   be trusted as a security boundary, and AppGateway is the public trust boundary.
// - Every endpoint under /api/account/* and /internal-api/account/* must declare WithGroupName("account").
// - Every endpoint under /internal-api/back-office/* must declare WithGroupName("back-office") so it
//   stays out of the account SPA's OpenAPI document.
public sealed class EndpointMetadataTests : IDisposable
{
    private const string AppHost = "app.test.localhost";
    private const string BackOfficeHost = "back-office.test.localhost";
    private const string TestPublicUrl = "https://localhost";

    // Anonymous-by-design /internal-api/* endpoints. Adding a new entry requires a documented
    // rationale alongside the route key: the endpoint either has no credential available in-cluster
    // (ACA health probes), is the credential issuance route itself (refresh-authentication-tokens),
    // is a backend-to-backend call from downstream main SCS projects that cannot pass an auth token
    // today, or is the framework-level catch-all that returns 404 for unmatched /internal-api/* paths.
    private static readonly string[] AnonymousInternalApiAllowlist =
    [
        // Refresh-authentication-tokens: anonymous by design — the refresh token in the request body
        // is the bearer credential. AppGateway's AuthenticationCookieMiddleware re-issues cookies via
        // this route when an upstream sets `x-refresh-authentication-tokens-required`.
        "POST:/internal-api/account/authentication/refresh-authentication-tokens",
        // ACA container app liveness + readiness probes. The probes do not carry credentials.
        "GET:/internal-api/live",
        "GET:/internal-api/ready",
        // /internal-api/account/tenants/{id} stays anonymous: server-to-server call from main SCS in
        // downstream projects. Until downstream callers can pass an auth token, the
        // BlockInternalApiTransform + ACA private ingress are the perimeter.
        "DELETE:/internal-api/account/tenants/{id}",
        // SinglePageAppFallbackExtensions registers a framework-level catch-all that emits 404 for
        // any unmatched /internal-api/* path. The 404 emitter is not a callable endpoint.
        "GET:/internal-api/{**_}",
        "POST:/internal-api/{**_}",
        "PUT:/internal-api/{**_}",
        "DELETE:/internal-api/{**_}",
        "PATCH:/internal-api/{**_}",
        "HEAD:/internal-api/{**_}",
        "OPTIONS:/internal-api/{**_}"
    ];

    // Internal back-office write endpoints that must require DelegationAdminPolicyName. Adding a new
    // mutation here forces the implementer to explicitly opt into either the admin set or the regular
    // set — the assertion below catches the case where a refactor swaps the delegation policy for the
    // delegation-admin policy or vice versa on a hand-listed route, which a relaxed check would let
    // through silently.
    private static readonly string[] AdminPolicyBackOfficeRoutes =
    [
        "PUT:/internal-api/back-office/feature-flags/{flagKey}/activate",
        "PUT:/internal-api/back-office/feature-flags/{flagKey}/deactivate",
        "DELETE:/internal-api/back-office/feature-flags/{flagKey}",
        "PUT:/internal-api/back-office/feature-flags/{flagKey}/tenant-override",
        "PUT:/internal-api/back-office/feature-flags/{flagKey}/rollout-percentage",
        "DELETE:/internal-api/back-office/feature-flags/{flagKey}/tenant-override",
        "PUT:/internal-api/back-office/feature-flags/{flagKey}/user-override",
        "DELETE:/internal-api/back-office/feature-flags/{flagKey}/user-override",
        "POST:/internal-api/back-office/tenants/{id}/reconcile-with-stripe",
        "POST:/internal-api/back-office/tenants/{id}/replay-archived-stripe-events",
        "POST:/internal-api/back-office/tenants/{id}/drift/acknowledge",
        "PUT:/internal-api/back-office/tenants/{id}/ab-inclusion-pin",
        "PUT:/internal-api/back-office/users/{id}/ab-inclusion-pin"
    ];

    private readonly WebApplicationFactory<Program> _webApplicationFactory;

    public EndpointMetadataTests()
    {
        // SinglePageAppConfiguration reads these env vars on host start to build the CSP. Without
        // them, host construction throws "Invalid URI: The URI is empty" before the endpoint data
        // source becomes available. The values are throwaway — only their well-formedness matters.
        Environment.SetEnvironmentVariable(SinglePageAppConfiguration.PublicUrlKey, TestPublicUrl);
        Environment.SetEnvironmentVariable(SinglePageAppConfiguration.CdnUrlKey, $"{TestPublicUrl}/account");

        _webApplicationFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(logging => logging.AddFilter(_ => false));
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Hostnames:App"] = AppHost,
                            ["BackOffice:Host"] = BackOfficeHost
                        }
                    )
                );
            }
        );
        // Force the host to start so the endpoint data source is populated.
        _ = _webApplicationFactory.Server;
    }

    public void Dispose()
    {
        _webApplicationFactory.Dispose();
    }

    [Fact]
    public void PublicAccountEndpoints_ShouldNotDeclareRequireHost()
    {
        // Arrange
        var routeEndpoints = GetRouteEndpoints();
        var publicAccountEndpoints = routeEndpoints
            .Where(endpoint => endpoint.RoutePattern.RawText is { } pattern && pattern.StartsWith("/api/account", StringComparison.OrdinalIgnoreCase))
            .ToList();
        publicAccountEndpoints.Should().NotBeEmpty();

        // Assert
        var endpointsWithHost = publicAccountEndpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHostMetadata>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();
        endpointsWithHost.Should().BeEmpty("public account endpoints must NOT declare RequireHost. AppGateway and ACA internal ingress are the trust boundaries; X-Forwarded-Host from upstream proxies is not a reliable security signal.");
    }

    [Fact]
    public void AccountEndpoints_ShouldAllDeclareAccountGroupName()
    {
        // Arrange
        var routeEndpoints = GetRouteEndpoints();
        var accountEndpoints = routeEndpoints
            .Where(endpoint => endpoint.RoutePattern.RawText is { } pattern && (pattern.StartsWith("/api/account", StringComparison.OrdinalIgnoreCase) || pattern.StartsWith("/internal-api/account", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        accountEndpoints.Should().NotBeEmpty();

        // Assert
        var endpointsMissingGroupName = accountEndpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IEndpointGroupNameMetadata>()?.EndpointGroupName != OpenApiDocumentNames.Account)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();
        endpointsMissingGroupName.Should().BeEmpty("account endpoints must declare WithGroupName(\"account\") so they appear in the account OpenAPI document");
    }

    [Fact]
    public void BackOfficeEndpoints_ShouldAllDeclareBackOfficeGroupName()
    {
        // Arrange
        var routeEndpoints = GetRouteEndpoints();
        var backOfficeEndpoints = routeEndpoints
            .Where(endpoint => endpoint.RoutePattern.RawText is { } pattern && pattern.StartsWith("/internal-api/back-office", StringComparison.OrdinalIgnoreCase))
            .ToList();
        backOfficeEndpoints.Should().NotBeEmpty("the internal back-office API must register at least one endpoint");

        // Assert
        var endpointsMissingGroupName = backOfficeEndpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IEndpointGroupNameMetadata>()?.EndpointGroupName != OpenApiDocumentNames.BackOffice)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();
        endpointsMissingGroupName.Should().BeEmpty("back-office endpoints must declare WithGroupName(\"back-office\") so they appear in the back-office OpenAPI document");
    }

    [Fact]
    public void InternalApiEndpoints_ShouldEitherRequireAuthorizationOrBeOnAllowlist()
    {
        // Arrange
        var routeEndpoints = GetRouteEndpoints();
        var internalApiEndpoints = routeEndpoints
            .Where(endpoint => endpoint.RoutePattern.RawText is { } pattern && pattern.StartsWith("/internal-api/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        internalApiEndpoints.Should().NotBeEmpty("the account host must register at least one /internal-api/* endpoint (refresh-tokens, health probes)");

        // Assert
        var violations = internalApiEndpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Where(endpoint => !AnonymousInternalApiAllowlist.Contains(BuildEndpointKey(endpoint)))
            .Select(BuildEndpointKey)
            .ToList();
        violations.Should().BeEmpty(
            "every /internal-api/* endpoint must either declare RequireAuthorization, or be added to AnonymousInternalApiAllowlist with a documented rationale. Anonymous /internal-api/* endpoints bypass BlockInternalApiTransform on pod-to-pod traffic and have been the source of cross-tenant data-exposure regressions."
        );
    }

    [Fact]
    public void BackOfficeWriteEndpoints_ShouldDeclareExpectedAuthorizationPolicy()
    {
        // Arrange
        var routeEndpoints = GetRouteEndpoints();
        var backOfficeWriteEndpoints = routeEndpoints
            .Where(endpoint => endpoint.RoutePattern.RawText is { } pattern && pattern.StartsWith("/internal-api/back-office/", StringComparison.OrdinalIgnoreCase))
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Any(method => method is "POST" or "PUT" or "PATCH" or "DELETE") == true)
            .ToList();
        backOfficeWriteEndpoints.Should().NotBeEmpty("the back-office route group must register at least one write endpoint");

        // Assert
        var violations = new List<string>();
        foreach (var endpoint in backOfficeWriteEndpoints)
        {
            var endpointKey = BuildEndpointKey(endpoint);
            var declaredPolicies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy).ToArray();
            var expectedPolicy = AdminPolicyBackOfficeRoutes.Contains(endpointKey)
                ? BackOfficeDelegationDefaults.DelegationAdminPolicyName
                : BackOfficeDelegationDefaults.DelegationPolicyName;

            if (!declaredPolicies.Contains(expectedPolicy))
            {
                violations.Add($"{endpointKey} (expected '{expectedPolicy}', declared [{string.Join(", ", declaredPolicies)}])");
            }
        }

        violations.Should().BeEmpty(
            $"every internal back-office write endpoint must declare its expected delegation policy: routes listed in {nameof(AdminPolicyBackOfficeRoutes)} require '{BackOfficeDelegationDefaults.DelegationAdminPolicyName}' (delegated staff member carries the admins group), every other write endpoint requires '{BackOfficeDelegationDefaults.DelegationPolicyName}' (any delegated staff member). Add new admin-gated mutations to the allowlist and add a `_WhenNonAdminDelegatedIdentity_ShouldReturnForbidden` test alongside the mutation."
        );
    }

    private static string BuildEndpointKey(RouteEndpoint endpoint)
    {
        var method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
        return $"{method}:{endpoint.RoutePattern.RawText}";
    }

    private List<RouteEndpoint> GetRouteEndpoints()
    {
        return _webApplicationFactory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToList();
    }
}
