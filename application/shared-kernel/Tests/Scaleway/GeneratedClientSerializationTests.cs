using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scaleway.Sdk;
using Scaleway.Sdk.Secret.V1Beta1;
using Xunit;

namespace SharedKernel.Tests.Scaleway;

/// <summary>
///     The wire format of a generated client, through the real transport: the source-generated serializer contexts
///     have to produce exactly what the reflection-based serializer did, or every Scaleway call drifts at once.
/// </summary>
public sealed class GeneratedClientSerializationTests
{
    private const string SecretJson = """
                                      {
                                        "id": "11111111-1111-1111-1111-111111111111",
                                        "project_id": "22222222-2222-2222-2222-222222222222",
                                        "name": "database-password",
                                        "status": "ready",
                                        "type": "a_type_newer_than_the_sdk",
                                        "tags": ["staging"],
                                        "version_count": 3,
                                        "created_at": "2026-11-12T08:00:00Z"
                                      }
                                      """;

    [Fact]
    public async Task CreateSecretAsync_ShouldSendSnakeCaseFieldsAndOmitUnsetOnes()
    {
        // Arrange
        var handler = new RecordingHandler(SecretJson);
        using var transport = new ScalewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.scaleway.com") });
        var secrets = new SecretApi(transport);

        // Act
        await secrets.CreateSecretAsync(new CreateSecretRequest { ProjectId = "22222222-2222-2222-2222-222222222222", Name = "database-password", Type = SecretType.Opaque, Protected = true }, "fr-par");

        // Assert
        using var body = JsonDocument.Parse(handler.RequestBody!);
        body.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("project_id", "name", "type", "protected");
        body.RootElement.GetProperty("type").GetString().Should().Be("opaque", "an extensible enum travels as its wire value");
        body.RootElement.GetProperty("protected").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetSecretAsync_ShouldReadSnakeCaseFieldsAndKeepAnUnknownEnumValue()
    {
        // Arrange
        var handler = new RecordingHandler(SecretJson);
        using var transport = new ScalewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.scaleway.com") });
        var secrets = new SecretApi(transport);

        // Act
        var secret = await secrets.GetSecretAsync("11111111-1111-1111-1111-111111111111", "fr-par");

        // Assert
        secret.Should().NotBeNull();
        secret.ProjectId.Should().Be("22222222-2222-2222-2222-222222222222");
        secret.Status.Should().Be(SecretStatus.Ready);
        secret.Type.Should().Be(SecretType.FromValue("a_type_newer_than_the_sdk"));
        secret.Tags.Should().Equal("staging");
        secret.VersionCount.Should().Be(3);
        secret.CreatedAt.Should().Be(new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero));
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") };
        }
    }
}
