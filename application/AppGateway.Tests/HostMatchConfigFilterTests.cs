using AppGateway.Filters;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace AppGateway.Tests;

public sealed class HostMatchConfigFilterTests
{
    private const string BackOfficeHost = "back-office.dev.localhost";
    private const string AppHost = "app.dev.localhost";

    private readonly HostMatchConfigFilter _filter = new(Options.Create(new HostnamesOptions { App = AppHost, BackOffice = BackOfficeHost }));

    [Fact]
    public async Task ConfigureRouteAsync_WhenMetadataReferencesApp_ShouldInjectAppHostnameOnly()
    {
        // Arrange
        var route = CreateRoute("account-api", "App");

        // Act
        var result = await _filter.ConfigureRouteAsync(route, null, CancellationToken.None);

        // Assert -- the App key injects the app hostname only; wildcard routes get both hosts.
        result.Match.Hosts.Should().BeEquivalentTo(AppHost);
    }

    [Fact]
    public async Task ConfigureRouteAsync_WhenMetadataReferencesWildcard_ShouldInjectAllConfiguredHostnames()
    {
        // Arrange
        var route = CreateRoute("favicon", "*");

        // Act
        var result = await _filter.ConfigureRouteAsync(route, null, CancellationToken.None);

        // Assert
        result.Match.Hosts.Should().BeEquivalentTo(AppHost, BackOfficeHost);
    }

    [Fact]
    public async Task ConfigureRouteAsync_WhenMetadataReferencesUnknownKey_ShouldThrow()
    {
        // Arrange
        var route = CreateRoute("rogue", "Unknown");

        // Act
        var act = async () => await _filter.ConfigureRouteAsync(route, null, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*HostnameKey 'Unknown'*");
    }

    [Fact]
    public async Task ConfigureRouteAsync_WhenNoHostnameKeyMetadata_ShouldLeaveRouteUnchanged()
    {
        // Arrange
        var route = new RouteConfig { RouteId = "no-metadata", ClusterId = "main-api", Match = new RouteMatch { Path = "/" } };

        // Act
        var result = await _filter.ConfigureRouteAsync(route, null, CancellationToken.None);

        // Assert
        result.Match.Hosts.Should().BeNull();
    }

    private static RouteConfig CreateRoute(string routeId, string hostnameKey)
    {
        return new RouteConfig
        {
            RouteId = routeId,
            ClusterId = "any-cluster",
            Match = new RouteMatch { Path = "/" },
            Metadata = new Dictionary<string, string> { ["HostnameKey"] = hostnameKey }
        };
    }
}
