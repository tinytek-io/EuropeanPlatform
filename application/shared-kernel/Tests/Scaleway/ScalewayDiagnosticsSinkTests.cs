using System.Net;
using System.Text;
using FluentAssertions;
using Scaleway.Sdk;
using Xunit;

namespace SharedKernel.Tests.Scaleway;

/// <summary>
///     Diagnostics capture is optional and runs while an API error is being raised, so a diagnostics path that cannot
///     be written must never replace that error with an I/O one.
/// </summary>
public sealed class ScalewayDiagnosticsSinkTests
{
    [Fact]
    public async Task CreateResourceAsync_WhenTheDiagnosticsPathCannotBeWritten_ShouldStillThrowTheApiError()
    {
        // Arrange
        var unwritablePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "diagnostics.jsonl");
        var handler = new BadRequestHandler();
        using var transport = new ScalewayApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.scaleway.com") },
            new FileScalewayDiagnosticsSink(unwritablePath)
        );

        // Act
        ScalewayApiException? exception = null;
        try
        {
            await transport.CreateResourceAsync("secret-manager/v1beta1/regions/fr-par/secrets", "fr-par", ScalewayJsonBody.EmptyObject, CancellationToken.None);
        }
        catch (ScalewayApiException caught)
        {
            exception = caught;
        }

        // Assert
        exception.Should().NotBeNull("the API error must surface even though the diagnostics write fails");
        exception.StatusCode.Should().Be(400);
        exception.ErrorType.Should().Be("invalid_arguments");
        File.Exists(unwritablePath).Should().BeFalse();
    }

    private sealed class BadRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string body = """{ "message": "invalid argument(s)", "type": "invalid_arguments", "details": [{ "argument_name": "name", "reason": "required" }] }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
