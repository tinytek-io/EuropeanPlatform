using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AppGateway.Tests;

public sealed class UnknownHostFallbackTests(AppGatewayApplicationFactory factory) : IClassFixture<AppGatewayApplicationFactory>
{
    [Theory]
    [InlineData("other.dev.localhost")]
    [InlineData("typo.dev.localhost")]
    public async Task Request_WhenHostIsUnknown_ShouldReturn404WithProblemDetails(string unknownHost)
    {
        // Arrange
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://{unknownHost}/");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var problemDetails = await response.Content.ReadFromJsonAsync<JsonElement>();
        problemDetails.GetProperty("status").GetInt32().Should().Be(404);
        problemDetails.GetProperty("title").GetString().Should().Be("Unknown host");
        problemDetails.GetProperty("detail").GetString().Should().Contain(unknownHost);

        var canonicalUrls = problemDetails.GetProperty("canonicalUrls").EnumerateArray().Select(e => e.GetString()).ToArray();
        canonicalUrls.Should().Contain(url => url!.Contains("app.dev.localhost"));
        canonicalUrls.Should().NotContain(url => url!.Contains("back-office.dev.localhost"));
    }

    [Theory]
    [InlineData("other.dev.localhost")]
    [InlineData("typo.dev.localhost")]
    public async Task Request_WhenHostIsUnknown_ShouldReturn404RegardlessOfPath(string unknownHost)
    {
        // Arrange
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://{unknownHost}/api/account/users");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Request_WhenHostIsKnown_ShouldNotHitUnknownHostFallback()
    {
        // Arrange
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "http://app.dev.localhost/openapi");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("application/problem+json");
    }

    [Fact]
    public async Task Request_WhenHostIsBackOffice_ShouldNotHitUnknownHostFallback()
    {
        // Arrange -- the back-office hostname is a known gateway host after the cutover: requests
        // match the back-office route (and fail on the unreachable test destination) rather than
        // the unknown-host fallback's 404.
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "http://back-office.dev.localhost/");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }
}
