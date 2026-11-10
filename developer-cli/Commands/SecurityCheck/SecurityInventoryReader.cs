using ScwEdge = Scaleway.Sdk.EdgeServices.V1Beta1;
using ScwLb = Scaleway.Sdk.Lb.V1;

// DeveloperCli.Tests compiles this file from outside its folder; the namespace matches its place in the CLI.
// ReSharper disable once CheckNamespace
namespace DeveloperCli.Commands.SecurityCheck;

/// <summary>
///     Reads the <see cref="SecurityInventory" /> through the generated Load Balancer and Edge Services clients:
///     every Load Balancer in the project, the gateway's frontends with their ACLs, and every edge pipeline's WAF,
///     TLS and cache stages. Read-only. Kept free of console output so the mapping is testable on its own.
/// </summary>
/// <param name="projectId">The project whose Load Balancers and pipelines are listed, named on every list request.</param>
/// <param name="region">The region whose zones are searched for Load Balancers.</param>
public sealed class SecurityInventoryReader(ScwLb.LbApi loadBalancers, ScwEdge.EdgeServicesApi edgeServices, string projectId, string region)
{
    // The zones the Load Balancer API serves, as its schema's zone parameter lists them. Not every zone of a region
    // has the product: fr-par-3 answers 501 "unknown service".
    private static readonly string[] LoadBalancerZones = ["fr-par-1", "fr-par-2", "nl-ams-1", "nl-ams-2", "nl-ams-3", "pl-waw-1", "pl-waw-2", "pl-waw-3"];

    /// <param name="gatewayLoadBalancerId">The gateway Load Balancer, zone-qualified as Terraform reports it.</param>
    /// <param name="cancellationToken">Passed to every request.</param>
    public async Task<SecurityInventory> ReadAsync(string gatewayLoadBalancerId, CancellationToken cancellationToken)
    {
        var projectLoadBalancers = new List<LoadBalancerSnapshot>();
        foreach (var zone in LoadBalancerZones.Where(zone => zone.StartsWith($"{region}-", StringComparison.Ordinal)))
        {
            projectLoadBalancers.AddRange(await ReadLoadBalancersAsync(zone, cancellationToken));
        }

        var gatewayFrontends = projectLoadBalancers.Any(loadBalancer => loadBalancer.Id == gatewayLoadBalancerId)
            ? await ReadFrontendsAsync(gatewayLoadBalancerId, cancellationToken)
            : [];

        return new SecurityInventory(gatewayLoadBalancerId, projectLoadBalancers.ToArray(), gatewayFrontends, await ReadEdgePipelinesAsync(cancellationToken));
    }

    private async Task<LoadBalancerSnapshot[]> ReadLoadBalancersAsync(string zone, CancellationToken cancellationToken)
    {
        var zoneLoadBalancers = await loadBalancers.ListLbsAsync(new ScwLb.ListLbsRequest { ProjectId = projectId }, zone, cancellationToken);

        return zoneLoadBalancers.Select(loadBalancer => new LoadBalancerSnapshot(
                $"{zone}/{loadBalancer.Id}",
                loadBalancer.Name!,
                (loadBalancer.Ip ?? []).Select(ip => ip.IpAddress!).ToArray()
            )
        ).ToArray();
    }

    private async Task<FrontendSnapshot[]> ReadFrontendsAsync(string zoneQualifiedLoadBalancerId, CancellationToken cancellationToken)
    {
        var (zone, loadBalancerId) = SplitZoneQualifiedId(zoneQualifiedLoadBalancerId);
        var frontends = await loadBalancers.ListFrontendsAsync(loadBalancerId, zone: zone, cancellationToken: cancellationToken);

        var snapshots = new List<FrontendSnapshot>();
        foreach (var frontend in frontends)
        {
            var acls = await loadBalancers.ListAclsAsync(frontend.Id!, zone: zone, cancellationToken: cancellationToken);
            snapshots.Add(new FrontendSnapshot(frontend.Id!, frontend.Name!, (int)frontend.InboundPort!.Value, acls.Select(ToAclSnapshot).ToArray()));
        }

        return snapshots.ToArray();
    }

    private static AclSnapshot ToAclSnapshot(ScwLb.Acl acl)
    {
        return new AclSnapshot(
            acl.Name!,
            (int)acl.Index!.Value,
            acl.Action!.Type!.Value.Value,
            acl.Match?.IpsEdgeServices == true,
            acl.Match?.Invert == true,
            acl.Match?.IpSubnet ?? []
        );
    }

    private async Task<EdgePipelineSnapshot[]> ReadEdgePipelinesAsync(CancellationToken cancellationToken)
    {
        var pipelines = await edgeServices.ListPipelinesAsync(new ScwEdge.ListPipelinesRequest { ProjectId = projectId }, cancellationToken);

        var snapshots = new List<EdgePipelineSnapshot>();
        foreach (var pipeline in pipelines)
        {
            var pipelineId = pipeline.Id!;
            var wafStages = await edgeServices.ListWafStagesAsync(pipelineId, cancellationToken: cancellationToken);
            var tlsStages = await edgeServices.ListTlsStagesAsync(pipelineId, cancellationToken: cancellationToken);
            var cacheStages = await edgeServices.ListCacheStagesAsync(pipelineId, cancellationToken: cancellationToken);

            var stagesTargetingBackendDirectly = tlsStages.Where(stage => stage.BackendStageId is not null).Select(stage => stage.Id!)
                .Concat(cacheStages.Where(stage => stage.BackendStageId is not null).Select(stage => stage.Id!))
                .ToArray();

            snapshots.Add(new EdgePipelineSnapshot(
                    pipelineId,
                    pipeline.Name!,
                    wafStages.Select(stage => new WafStageSnapshot(stage.Id!, stage.Mode!.Value.Value, stage.ExclusionRules?.Count ?? 0)).ToArray(),
                    stagesTargetingBackendDirectly
                )
            );
        }

        return snapshots.ToArray();
    }

    private static (string Zone, string Id) SplitZoneQualifiedId(string zoneQualifiedId)
    {
        var separator = zoneQualifiedId.IndexOf('/');
        if (separator <= 0)
        {
            throw new InvalidOperationException($"'{zoneQualifiedId}' is not a zone-qualified id such as 'fr-par-1/<uuid>'.");
        }

        return (zoneQualifiedId[..separator], zoneQualifiedId[(separator + 1)..]);
    }
}
