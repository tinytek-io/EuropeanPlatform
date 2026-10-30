using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace AppGateway.Tests;

public sealed class RouteConfigurationTests(AppGatewayApplicationFactory factory) : IClassFixture<AppGatewayApplicationFactory>
{
    [Theory]
    [InlineData("account-api")]
    [InlineData("account-federation")]
    [InlineData("account-static")]
    [InlineData("account-hmr")]
    [InlineData("main-static")]
    [InlineData("main-hmr")]
    [InlineData("main")]
    public void AppRoutes_ShouldDeclareAppHostnameKey(string routeId)
    {
        // Act
        var route = GetRoute(routeId);

        // Assert
        route.Metadata.Should().ContainKey("HostnameKey").WhoseValue.Should().Be("App");
    }

    [Theory]
    [InlineData("favicon")]
    [InlineData("apple-touch-icon")]
    [InlineData("manifest")]
    [InlineData("robots")]
    [InlineData("tracking")]
    [InlineData("avatars")]
    [InlineData("logos")]
    [InlineData("account-legal-documents")]
    public void AssetRoutes_ShouldDeclareWildcardHostnameKey(string routeId)
    {
        // Act
        var route = GetRoute(routeId);

        // Assert
        route.Metadata.Should().ContainKey("HostnameKey").WhoseValue.Should().Be("*");
    }

    [Fact]
    public void EveryConfiguredRoute_ShouldDeclareHostnameKeyMetadata()
    {
        // Arrange
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IProxyConfigProvider>();

        // Act
        var routes = provider.GetConfig().Routes;

        // Assert
        routes.Should().NotBeEmpty();
        foreach (var route in routes)
        {
            route.Metadata.Should().NotBeNull($"route '{route.RouteId}' must declare metadata");
            route.Metadata.Should().ContainKey("HostnameKey", $"route '{route.RouteId}' must declare a HostnameKey");
        }
    }

    [Fact]
    public void BackOfficeRoute_ShouldTargetBackOfficeApiWithBackOfficeHostnameKey()
    {
        // Arrange -- both public hostnames flow through the gateway after the back-office cutover:
        // the back-office catch-all routes to the back-office system, while the wildcard avatar/logo
        // routes keep serving storage for both hosts.
        // Act
        var route = GetRoute("back-office");

        // Assert
        route.ClusterId.Should().Be("back-office-api");
        route.Metadata?.GetValueOrDefault("HostnameKey").Should().Be("BackOffice");
        route.Transforms.Should().Contain(
            t => t.ContainsKey("RequestHeaderOriginalHost") && string.Equals(t["RequestHeaderOriginalHost"], "true", StringComparison.OrdinalIgnoreCase),
            "the back-office API's RequireHost matches the original public host header"
        );
    }

    private RouteConfig GetRoute(string routeId)
    {
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IProxyConfigProvider>();
        var route = provider.GetConfig().Routes.SingleOrDefault(r => r.RouteId == routeId);
        route.Should().NotBeNull($"route '{routeId}' should exist in AppGateway configuration");
        return route;
    }
}
