using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scaleway.Sdk;
using Xunit;

namespace SharedKernel.Tests.Scaleway;

/// <summary>
///     Get-or-create must never replace a value a deployed resource may already use — including when the secret only
///     becomes visible through a create that reports it already exists.
/// </summary>
public sealed class ScalewaySecretClientTests
{
    private const string ProjectId = "22222222-2222-2222-2222-222222222222";
    private const string SecretId = "11111111-1111-1111-1111-111111111111";
    private const string SecretName = "database-password";

    [Fact]
    public async Task GetOrCreateAsync_WhenCreateReportsTheSecretAlreadyExists_ShouldReturnItsValueWithoutWriting()
    {
        // Arrange
        var secretManager = new FakeSecretManager { VisibleAfterFirstLookup = true, CreateConflicts = true, StoredValue = "value-in-use" };
        var secrets = CreateClient(secretManager);
        var factoryCalls = 0;

        // Act
        var value = await secrets.GetOrCreateAsync(ProjectId, "fr-par", SecretName, () => $"generated-{++factoryCalls}", []);

        // Assert
        value.Should().Be("value-in-use");
        factoryCalls.Should().Be(0);
        secretManager.VersionsWritten.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenAdoptedSecretHasNoValue_ShouldWriteTheGeneratedValue()
    {
        // Arrange
        var secretManager = new FakeSecretManager { VisibleAfterFirstLookup = true, CreateConflicts = true, StoredValue = null };
        var secrets = CreateClient(secretManager);

        // Act
        var value = await secrets.GetOrCreateAsync(ProjectId, "fr-par", SecretName, () => "generated", []);

        // Assert
        value.Should().Be("generated");
        secretManager.VersionsWritten.Should().Equal("generated");
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenSecretIsAbsent_ShouldCreateItWithTheGeneratedValue()
    {
        // Arrange
        var secretManager = new FakeSecretManager();
        var secrets = CreateClient(secretManager);

        // Act
        var value = await secrets.GetOrCreateAsync(ProjectId, "fr-par", SecretName, () => "generated", []);

        // Assert
        value.Should().Be("generated");
        secretManager.VersionsWritten.Should().Equal("generated");
    }

    [Fact]
    public async Task SetAsync_WhenCreateReportsTheSecretAlreadyExists_ShouldOverwriteItsValue()
    {
        // Arrange
        var secretManager = new FakeSecretManager { VisibleAfterFirstLookup = true, CreateConflicts = true, StoredValue = "old" };
        var secrets = CreateClient(secretManager);

        // Act
        await secrets.SetAsync(ProjectId, "fr-par", SecretName, "new", []);

        // Assert
        secretManager.VersionsWritten.Should().Equal("new");
    }

    private static ScalewaySecretClient CreateClient(FakeSecretManager secretManager)
    {
        var transport = new ScalewayApiClient(new HttpClient(secretManager) { BaseAddress = new Uri("https://api.scaleway.com") });
        return new ScalewaySecretClient(transport);
    }

    /// <summary>
    ///     Secret Manager for one secret. With <see cref="VisibleAfterFirstLookup" /> the first name lookup misses it
    ///     (a lost create response, or a list that has not caught up) and every later one finds it.
    /// </summary>
    private sealed class FakeSecretManager : HttpMessageHandler
    {
        private int _lookups;

        public bool VisibleAfterFirstLookup { get; init; }

        public bool CreateConflicts { get; init; }

        public string? StoredValue { get; init; }

        public List<string> VersionsWritten { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && path.EndsWith("/secrets"))
            {
                var visible = VisibleAfterFirstLookup && _lookups++ > 0;
                var secrets = visible ? $$"""[{"id":"{{SecretId}}","project_id":"{{ProjectId}}","name":"{{SecretName}}","path":"/"}]""" : "[]";
                return Json(HttpStatusCode.OK, $$"""{"secrets":{{secrets}},"total_count":{{(visible ? 1 : 0)}}}""");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/secrets"))
            {
                return CreateConflicts
                    ? Json(HttpStatusCode.Conflict, """{"message":"resource already exists","type":"conflict"}""")
                    : Json(HttpStatusCode.OK, $$"""{"id":"{{SecretId}}","name":"{{SecretName}}","path":"/"}""");
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/access"))
            {
                return StoredValue is null
                    ? Json(HttpStatusCode.NotFound, """{"message":"resource is not found","type":"not_found"}""")
                    : Json(HttpStatusCode.OK, $$"""{"secret_id":"{{SecretId}}","revision":1,"data":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(StoredValue))}}"}""");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/versions"))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var data = JsonDocument.Parse(body).RootElement.GetProperty("data").GetString()!;
                VersionsWritten.Add(Encoding.UTF8.GetString(Convert.FromBase64String(data)));
                return Json(HttpStatusCode.OK, $$"""{"secret_id":"{{SecretId}}","revision":{{VersionsWritten.Count}}}""");
            }

            return Json(HttpStatusCode.NotImplemented, """{"message":"unexpected request"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json)
        {
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
