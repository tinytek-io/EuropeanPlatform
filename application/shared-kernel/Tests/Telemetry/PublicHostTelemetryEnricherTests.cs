using FluentAssertions;
using Microsoft.AspNetCore.Http;
using SharedKernel.Telemetry;
using Xunit;

namespace SharedKernel.Tests.Telemetry;

public sealed class PublicHostTelemetryEnricherTests
{
    [Theory]
    [InlineData("203.0.113.77", "203.0.113.0")]
    [InlineData("203.0.113.77, 10.0.0.5", "203.0.113.0")]
    [InlineData("2001:db8:abcd:12:34::1", "2001:db8:abcd::")]
    [InlineData("::ffff:203.0.113.77", "203.0.113.0")]
    public void Enrich_ShouldRecordOnlyTheClientNetwork(string forwardedFor, string expectedClientAddress)
    {
        // Arrange
        using var activity = new Activity("request");
        var context = new DefaultHttpContext { Request = { Host = new HostString("app.dev.localhost"), Headers = { ["X-Forwarded-For"] = forwardedFor } } };

        // Act
        PublicHostTelemetryEnricher.Enrich(activity, context.Request);

        // Assert
        activity.GetTagItem("client.address").Should().Be(expectedClientAddress);
    }

    [Fact]
    public void Enrich_WhenForwardedForIsNotAnAddress_ShouldOmitClientAddress()
    {
        // Arrange
        using var activity = new Activity("request");
        var context = new DefaultHttpContext { Request = { Host = new HostString("app.dev.localhost"), Headers = { ["X-Forwarded-For"] = "not-an-ip" } } };

        // Act
        PublicHostTelemetryEnricher.Enrich(activity, context.Request);

        // Assert
        activity.GetTagItem("client.address").Should().BeNull();
    }
}
