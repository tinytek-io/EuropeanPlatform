using System.Net;
using System.Net.Http.Json;
using Account.Database;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Account.Tests.SharedKernel;

public sealed class TrackEndpointsTests(AccountWebApplicationFactory factory) : EndpointBaseTest<AccountDbContext>(factory), IClassFixture<AccountWebApplicationFactory>
{
    [Fact]
    public async Task Track_WhenValidBatch_ShouldSucceed()
    {
        // Arrange
        var events = new[] { new { type = "pageview", name = "dashboard", url = "https://app.dev.localhost/dashboard", properties = new Dictionary<string, string> { ["type"] = "page" } } };

        // Act
        var response = await AnonymousHttpClient.PostAsJsonAsync("/api/track", events);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void Track_ShouldDeclareRequestSizeLimit()
    {
        // Kestrel enforces the limit from this endpoint metadata (TestServer does not enforce body
        // size limits, so the 413 itself is only observable against a real Kestrel host).
        var endpoint = WebApplicationServices.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(routeEndpoint => routeEndpoint.RoutePattern.RawText == "/api/track");

        endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()!.MaxRequestBodySize.Should().Be(256 * 1024);
    }
}
