using DeveloperCli.Utilities;
using FluentAssertions;
using Xunit;

namespace DeveloperCli.Tests.Utilities;

/// <summary>
///     A DNS-over-HTTPS resolver reports its own failures as HTTP 200 with a failing DNS status, so dns-check and
///     edge-health must tell "the resolver could not answer" (unchecked) from "there are no such records" (missing).
/// </summary>
public sealed class PublicDnsTests
{
    [Fact]
    public void ParseResponse_WhenTheLookupSucceeds_ShouldReturnTheRecordsOfTheRequestedType()
    {
        // Arrange
        const string json = """
                            { "Status": 0, "Answer": [
                              { "name": "app.example.com", "type": 5, "data": "edge.example.net." },
                              { "name": "edge.example.net", "type": 1, "data": "51.15.0.1" }
                            ] }
                            """;

        // Act
        var records = PublicDns.ParseResponse(json, "A");

        // Assert
        records.Should().Equal("51.15.0.1");
    }

    [Theory]
    [InlineData("""{ "Status": 0 }""")]
    [InlineData("""{ "Status": 3 }""")]
    public void ParseResponse_WhenTheNameHasNoSuchRecords_ShouldReturnEmpty(string json)
    {
        // Act
        var records = PublicDns.ParseResponse(json, "TXT");

        // Assert
        records.Should().NotBeNull().And.BeEmpty();
    }

    [Theory]
    [InlineData("""{ "Status": 2 }""")]
    [InlineData("""{ "Status": 5 }""")]
    public void ParseResponse_WhenTheResolverFails_ShouldReturnNull(string json)
    {
        // Act
        var records = PublicDns.ParseResponse(json, "CNAME");

        // Assert
        records.Should().BeNull("a SERVFAIL or REFUSED answer means the record was not checked, not that it is missing");
    }
}
