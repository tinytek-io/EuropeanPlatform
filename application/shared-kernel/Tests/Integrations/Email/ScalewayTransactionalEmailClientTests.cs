using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scaleway.Sdk;
using SharedKernel.Integrations.Email;
using Xunit;

namespace SharedKernel.Tests.Integrations.Email;

/// <summary>
///     The bodies are Scaleway's own error responses, so a change in Scaleway's wording fails these tests instead of
///     silently turning provider incidents back into generic errors.
/// </summary>
public sealed class ScalewayTransactionalEmailClientTests
{
    // Recorded from the staging send during "[Transactional Email] - world - Delayed email delivery to Microsoft domains".
    private const string RecordedIncidentRefusal = """
        {"details":[{"argument_name":"to.email","reason":"constraint","help_message":"Recipient domain \"example.com\" is temporarily not supported. For more information: https://status.scaleway.com/incidents/vyc7z0zdp42y"}],"message":"invalid argument(s)","type":"invalid_arguments"}
        """;

    // Scaleway's incident notice quotes the message with the domain missing.
    private const string RefusalWithoutDomain = """
        {"details":[{"argument_name":"to.email","reason":"constraint","help_message":"The recipient domain  is temporarily not supported. For more information: https://status.scaleway.com/incidents/vyc7z0zdp42y"}],"message":"invalid argument(s)","type":"invalid_arguments"}
        """;

    private const string RefusalWithoutIncidentLink = """
        {"details":[{"argument_name":"to.email","reason":"constraint","help_message":"Recipient domain \"example.com\" is temporarily not supported."}],"message":"invalid argument(s)","type":"invalid_arguments"}
        """;

    private const string RefusalLinkingElsewhere = """
        {"details":[{"argument_name":"to.email","reason":"constraint","help_message":"Recipient domain \"example.com\" is temporarily not supported. For more information: https://status.example.net/incidents/abc123"}],"message":"invalid argument(s)","type":"invalid_arguments"}
        """;

    private const string MalformedAddress = """
        {"details":[{"argument_name":"to.email","reason":"constraint","help_message":"must be a valid email address"}],"message":"invalid argument(s)","type":"invalid_arguments"}
        """;

    public ScalewayTransactionalEmailClientTests()
    {
        // Read by the client's constructor; every test sets the same values, so parallel test classes cannot disagree.
        Environment.SetEnvironmentVariable("SCW_DEFAULT_PROJECT_ID", "22222222-2222-2222-2222-222222222222");
        Environment.SetEnvironmentVariable("SENDER_EMAIL_ADDRESS", "no-reply@example.org");
    }

    [Fact]
    public async Task SendAsync_WhenRecipientDomainIsRefusedDuringIncident_ShouldReturnServiceUnavailableWithDomainAndIncidentLink()
    {
        // Arrange
        var client = CreateClient(RecordedIncidentRefusal);

        // Act
        var result = await client.SendAsync(Message("someone@example.com"), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        result.ResponseHeaders.Should().ContainKey("Retry-After").WhoseValue.Should().Be("900");
        result.ProblemExtensions.Should().BeEquivalentTo(new Dictionary<string, object?>
            {
                ["reason"] = "email-recipient-domain-unavailable",
                ["recipientDomain"] = "example.com",
                ["statusUrl"] = "https://status.scaleway.com/incidents/vyc7z0zdp42y"
            }
        );
    }

    [Fact]
    public async Task SendAsync_WhenRefusalMessageOmitsTheDomain_ShouldTakeTheDomainFromTheRecipient()
    {
        // Arrange
        var client = CreateClient(RefusalWithoutDomain);

        // Act
        var result = await client.SendAsync(Message("someone@outlook.example"), CancellationToken.None);

        // Assert
        result.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        result.ProblemExtensions!["recipientDomain"].Should().Be("outlook.example");
        result.ProblemExtensions!["statusUrl"].Should().Be("https://status.scaleway.com/incidents/vyc7z0zdp42y");
    }

    [Fact]
    public async Task SendAsync_WhenRefusalHasNoIncidentLink_ShouldLinkToTheStatusPage()
    {
        // Arrange
        var client = CreateClient(RefusalWithoutIncidentLink);

        // Act
        var result = await client.SendAsync(Message("someone@example.com"), CancellationToken.None);

        // Assert
        result.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        result.ProblemExtensions!["statusUrl"].Should().Be("https://status.scaleway.com");
    }

    [Fact]
    public async Task SendAsync_WhenRefusalLinksOutsideScalewaysStatusPage_ShouldLinkToTheStatusPage()
    {
        // Arrange
        var client = CreateClient(RefusalLinkingElsewhere);

        // Act
        var result = await client.SendAsync(Message("someone@example.com"), CancellationToken.None);

        // Assert
        result.ProblemExtensions!["statusUrl"].Should().Be("https://status.scaleway.com");
    }

    [Fact]
    public async Task SendAsync_WhenRecipientAddressIsMalformed_ShouldThrowScalewaysError()
    {
        // Arrange
        var client = CreateClient(MalformedAddress);

        // Act
        var act = () => client.SendAsync(Message("someone@example.com"), CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<ScalewayApiException>()).Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task SendAsync_WhenScalewayAcceptsTheEmail_ShouldReturnSuccess()
    {
        // Arrange
        var client = new ScalewayTransactionalEmailClient(HttpClientAnswering(HttpStatusCode.OK, """{"emails":[]}"""), NullLogger<ScalewayTransactionalEmailClient>.Instance);

        // Act
        var result = await client.SendAsync(Message("someone@example.com"), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    private static ScalewayTransactionalEmailClient CreateClient(string errorBody)
    {
        return new ScalewayTransactionalEmailClient(HttpClientAnswering(HttpStatusCode.BadRequest, errorBody), NullLogger<ScalewayTransactionalEmailClient>.Instance);
    }

    private static HttpClient HttpClientAnswering(HttpStatusCode statusCode, string body)
    {
        return new HttpClient(new FixedResponseHandler(statusCode, body)) { BaseAddress = new Uri(ScalewayApiClient.DefaultApiUrl) };
    }

    private static EmailMessage Message(string recipient)
    {
        return new EmailMessage(recipient, "Your code", "<p>123456</p>", "123456");
    }

    private sealed class FixedResponseHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
