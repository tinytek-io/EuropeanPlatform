using System.Net;
using System.Text;
using BackOffice.Features.SecurityChecks;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BackOffice.Tests.SecurityChecks;

/// <summary>
///     Feeds the inventory client Scaleway-shaped responses for every path it reads, so a mapping mistake between the
///     generated Load Balancer models and the snapshots the checks evaluate fails here rather than in a deployed worker.
/// </summary>
public sealed class ScalewaySecurityInventoryClientTests
{
    private const string ProjectId = "22222222-2222-2222-2222-222222222222";
    private const string GatewayId = "fr-par-1/11111111-1111-1111-1111-111111111111";

    private static readonly Dictionary<string, string> Responses = new()
    {
        ["/lb/v1/zones/fr-par-1/lbs"] = """{ "lbs": [{ "id": "11111111-1111-1111-1111-111111111111", "name": "gateway", "ip": [{ "ip_address": "51.15.0.1" }] }], "total_count": 1 }""",
        ["/lb/v1/zones/fr-par-2/lbs"] = """{ "lbs": [], "total_count": 0 }""",
        ["/lb/v1/zones/fr-par-3/lbs"] = """{ "lbs": [], "total_count": 0 }""",
        ["/lb/v1/zones/fr-par-1/lbs/11111111-1111-1111-1111-111111111111/frontends"] = """{ "frontends": [{ "id": "frontend-443", "name": "app-https", "inbound_port": 443 }], "total_count": 1 }""",
        ["/lb/v1/zones/fr-par-1/frontends/frontend-443/acls"] =
            """{ "acls": [{ "name": "edge-only", "index": 0, "action": { "type": "deny" }, "match": { "ips_edge_services": true, "invert": true, "ip_subnet": [] } }], "total_count": 1 }""",
        ["/edge-services/v1beta1/pipelines"] = """{ "pipelines": [], "total_count": 0 }"""
    };

    [Fact]
    public async Task ReadAsync_WhenScalewayReturnsTheGateway_ShouldMapLoadBalancersFrontendsAndAcls()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SCW_DEFAULT_PROJECT_ID", ProjectId);
        Environment.SetEnvironmentVariable("SCW_DEFAULT_REGION", "fr-par");
        var handler = new RecordedResponsesHandler(Responses);
        var client = new ScalewaySecurityInventoryClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.scaleway.com/") },
            Options.Create(new SecurityChecksOptions { GatewayLoadBalancerId = GatewayId })
        );

        // Act
        var inventory = await client.ReadAsync(CancellationToken.None);

        // Assert
        inventory.LoadBalancers.Should().BeEquivalentTo([new LoadBalancerSnapshot(GatewayId, "gateway", ["51.15.0.1"])]);
        var frontend = inventory.GatewayFrontends.Should().ContainSingle().Subject;
        frontend.Should().BeEquivalentTo(new FrontendSnapshot("frontend-443", "app-https", 443, [new AclSnapshot("edge-only", 0, "deny", true, true, [])]));
        inventory.EdgePipelines.Should().BeEmpty();
        handler.RequestedUris.Where(uri => uri.AbsolutePath.EndsWith("/lbs")).Should().OnlyContain(uri => uri.Query.Contains($"project_id={ProjectId}"));
    }

    private sealed class RecordedResponsesHandler(Dictionary<string, string> responses) : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            var found = responses.TryGetValue(request.RequestUri!.AbsolutePath, out var body);
            return Task.FromResult(new HttpResponseMessage(found ? HttpStatusCode.OK : HttpStatusCode.NotFound)
                {
                    Content = new StringContent(found ? body! : """{ "message": "not recorded" }""", Encoding.UTF8, "application/json")
                }
            );
        }
    }
}
