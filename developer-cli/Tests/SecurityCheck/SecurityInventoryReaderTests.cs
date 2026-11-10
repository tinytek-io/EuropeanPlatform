using System.Net;
using System.Text;
using DeveloperCli.Commands.SecurityCheck;
using FluentAssertions;
using Scaleway.Sdk;
using Xunit;
using ScwEdge = Scaleway.Sdk.EdgeServices.V1Beta1;
using ScwLb = Scaleway.Sdk.Lb.V1;

namespace DeveloperCli.Tests.SecurityCheck;

/// <summary>
///     Feeds the inventory reader Scaleway-shaped responses for every path it reads, so a mapping mistake between the
///     generated Load Balancer and Edge Services models and the snapshots the checks evaluate fails here rather than
///     against a live environment.
/// </summary>
public sealed class SecurityInventoryReaderTests
{
    private const string ProjectId = "22222222-2222-2222-2222-222222222222";
    private const string GatewayId = "fr-par-1/11111111-1111-1111-1111-111111111111";

    private static readonly Dictionary<string, string> Responses = new()
    {
        ["/lb/v1/zones/fr-par-1/lbs"] = """{ "lbs": [{ "id": "11111111-1111-1111-1111-111111111111", "name": "gateway", "ip": [{ "ip_address": "51.15.0.1" }] }], "total_count": 1 }""",
        ["/lb/v1/zones/fr-par-2/lbs"] = """{ "lbs": [], "total_count": 0 }""",
        ["/lb/v1/zones/fr-par-1/lbs/11111111-1111-1111-1111-111111111111/frontends"] = """{ "frontends": [{ "id": "frontend-443", "name": "app-https", "inbound_port": 443 }], "total_count": 1 }""",
        ["/lb/v1/zones/fr-par-1/frontends/frontend-443/acls"] =
            """{ "acls": [{ "name": "edge-only", "index": 0, "action": { "type": "deny" }, "match": { "ips_edge_services": true, "invert": true, "ip_subnet": [] } }], "total_count": 1 }""",
        ["/edge-services/v1beta1/pipelines"] = """{ "pipelines": [{ "id": "pipeline-1", "name": "staging-app", "status": "ready", "errors": [] }], "total_count": 1 }""",
        ["/edge-services/v1beta1/pipelines/pipeline-1/waf-stages"] =
            """{ "stages": [{ "id": "waf-1", "mode": "log_only", "exclusion_rules": [{ "rule_id": 942100 }, { "rule_id": 920350 }] }], "total_count": 1 }""",
        ["/edge-services/v1beta1/pipelines/pipeline-1/tls-stages"] = """{ "stages": [{ "id": "tls-1", "backend_stage_id": "backend-1" }], "total_count": 1 }""",
        ["/edge-services/v1beta1/pipelines/pipeline-1/cache-stages"] = """{ "stages": [{ "id": "cache-1", "waf_stage_id": "waf-1" }], "total_count": 1 }"""
    };

    [Fact]
    public async Task ReadAsync_WhenScalewayReturnsTheGateway_ShouldMapLoadBalancersFrontendsAclsAndEdgePipelines()
    {
        // Arrange
        var handler = new RecordedResponsesHandler(Responses);
        using var httpClient = new HttpClient(handler);
        httpClient.BaseAddress = new Uri("https://api.scaleway.com/");
        using var transport = new ScalewayApiClient(httpClient);
        var reader = new SecurityInventoryReader(new ScwLb.LbApi(transport), new ScwEdge.EdgeServicesApi(transport), ProjectId, "fr-par");

        // Act
        var inventory = await reader.ReadAsync(GatewayId, CancellationToken.None);

        // Assert
        inventory.LoadBalancers.Should().BeEquivalentTo([new LoadBalancerSnapshot(GatewayId, "gateway", ["51.15.0.1"])]);
        var frontend = inventory.GatewayFrontends.Should().ContainSingle().Subject;
        frontend.Should().BeEquivalentTo(new FrontendSnapshot("frontend-443", "app-https", 443, [new AclSnapshot("edge-only", 0, "deny", true, true, [])]));
        inventory.EdgePipelines.Should().BeEquivalentTo([new EdgePipelineSnapshot("pipeline-1", "staging-app", [new WafStageSnapshot("waf-1", "log_only", 2)], ["tls-1"])]);
        handler.RequestedUris.Where(uri => uri.AbsolutePath.EndsWith("/lbs") || uri.AbsolutePath.EndsWith("/pipelines"))
            .Should().OnlyContain(uri => uri.Query.Contains($"project_id={ProjectId}"));
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
