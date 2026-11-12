using System.Net;
using System.Text;
using FluentAssertions;
using Scaleway.Sdk;
using Xunit;

namespace SharedKernel.Tests.Scaleway;

/// <summary>
///     Callers that hold only a secret key (a workload's IAM key, or a tool still looking up its organization) set up
///     their HTTP client through the SDK, so the API host and authentication header are written down in one place.
/// </summary>
public sealed class ScalewayHttpClientConfigurationTests
{
    [Fact]
    public async Task ConfigureHttpClient_WhenATransportCallsThroughIt_ShouldSendTheKeyToTheScalewayApi()
    {
        // Arrange
        var handler = new RecordingHandler();
        using var httpClient = ScalewayApiClient.ConfigureHttpClient(new HttpClient(handler), "secret-key");
        using var transport = new ScalewayApiClient(httpClient);

        // Act
        await transport.GetAsync("secret-manager/v1beta1/regions/fr-par/secrets", "fr-par", CancellationToken.None);

        // Assert
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.RequestUri!.GetLeftPart(UriPartial.Authority).Should().Be(ScalewayApiClient.DefaultApiUrl);
        request.Headers.GetValues("X-Auth-Token").Should().Equal("secret-key");
        request.Headers.Accept.Select(mediaType => mediaType.MediaType).Should().Equal("application/json");
        request.Headers.UserAgent.Should().Contain(product => product.Product!.Name == "Scaleway.Sdk-dotnet");
    }

    [Fact]
    public void ConfigureHttpClient_WhenCalledAgain_ShouldReplaceTheKeyWithoutRepeatingHeaders()
    {
        // Arrange
        using var httpClient = new HttpClient();
        ScalewayApiClient.ConfigureHttpClient(httpClient, "first-key");

        // Act
        ScalewayApiClient.ConfigureHttpClient(httpClient, "second-key");

        // Assert
        httpClient.DefaultRequestHeaders.GetValues("X-Auth-Token").Should().Equal("second-key");
        httpClient.DefaultRequestHeaders.Accept.Should().ContainSingle();
        httpClient.DefaultRequestHeaders.UserAgent.Should().ContainSingle();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }
}
