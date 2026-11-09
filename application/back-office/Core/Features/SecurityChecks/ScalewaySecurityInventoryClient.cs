using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Scaleway.Sdk;
using ScwEdge = Scaleway.Sdk.EdgeServices.V1Beta1;
using ScwLb = Scaleway.Sdk.Lb.V1;

namespace BackOffice.Features.SecurityChecks;

public sealed class SecurityChecksOptions
{
    public const string SectionName = "SecurityChecks";

    /// <summary>The gateway LB, zone-qualified as Terraform reports it; delivered through the worker's Secret Manager path.</summary>
    [Required]
    public string GatewayLoadBalancerId { get; init; } = null!;

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
///     Reads the <see cref="SecurityInventory" /> from the Scaleway API with the worker's own key, which holds the
///     read-only Load Balancer and Edge Services permission sets only, through the SDK's generated Load Balancer and
///     Edge Services clients.
/// </summary>
public sealed class ScalewaySecurityInventoryClient(HttpClient httpClient, IOptions<SecurityChecksOptions> options)
{
    // Load Balancers are zonal; a region has these three zones (fr-par, nl-ams and pl-waw alike).
    private static readonly int[] ZoneNumbers = [1, 2, 3];

    // No request scope: a scope needs the organization, which the worker is not given. Listing names the project.
    private readonly ScwEdge.EdgeServicesApi _edgeServices = new(new ScalewayApiClient(httpClient));

    private readonly ScwLb.LbApi _loadBalancers = new(new ScalewayApiClient(httpClient));

    private readonly string _projectId = Environment.GetEnvironmentVariable("SCW_DEFAULT_PROJECT_ID")
                                         ?? throw new InvalidOperationException("SCW_DEFAULT_PROJECT_ID environment variable is not configured.");

    private readonly string _region = Environment.GetEnvironmentVariable("SCW_DEFAULT_REGION")
                                      ?? throw new InvalidOperationException("SCW_DEFAULT_REGION environment variable is not configured.");

    public async Task<SecurityInventory> ReadAsync(CancellationToken cancellationToken)
    {
        var gatewayLoadBalancerId = options.Value.GatewayLoadBalancerId;

        var loadBalancers = new List<LoadBalancerSnapshot>();
        foreach (var zone in ZoneNumbers.Select(number => $"{_region}-{number}"))
        {
            loadBalancers.AddRange(await ReadLoadBalancersAsync(zone, cancellationToken));
        }

        var gatewayFrontends = loadBalancers.Any(loadBalancer => loadBalancer.Id == gatewayLoadBalancerId)
            ? await ReadFrontendsAsync(gatewayLoadBalancerId, cancellationToken)
            : [];

        return new SecurityInventory(gatewayLoadBalancerId, loadBalancers.ToArray(), gatewayFrontends, await ReadEdgePipelinesAsync(cancellationToken));
    }

    private async Task<LoadBalancerSnapshot[]> ReadLoadBalancersAsync(string zone, CancellationToken cancellationToken)
    {
        var loadBalancers = await _loadBalancers.ListLbsAsync(new ScwLb.ListLbsRequest { ProjectId = _projectId }, zone, cancellationToken);

        return loadBalancers.Select(loadBalancer => new LoadBalancerSnapshot(
                $"{zone}/{loadBalancer.Id}",
                loadBalancer.Name!,
                (loadBalancer.Ip ?? []).Select(ip => ip.IpAddress!).ToArray()
            )
        ).ToArray();
    }

    private async Task<FrontendSnapshot[]> ReadFrontendsAsync(string zoneQualifiedLoadBalancerId, CancellationToken cancellationToken)
    {
        var (zone, loadBalancerId) = SplitZoneQualifiedId(zoneQualifiedLoadBalancerId);
        var frontends = await _loadBalancers.ListFrontendsAsync(loadBalancerId, zone: zone, cancellationToken: cancellationToken);

        var snapshots = new List<FrontendSnapshot>();
        foreach (var frontend in frontends)
        {
            var acls = await _loadBalancers.ListAclsAsync(frontend.Id!, zone: zone, cancellationToken: cancellationToken);
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
        var pipelines = await _edgeServices.ListPipelinesAsync(new ScwEdge.ListPipelinesRequest { ProjectId = _projectId }, cancellationToken);

        var snapshots = new List<EdgePipelineSnapshot>();
        foreach (var pipeline in pipelines)
        {
            var pipelineId = pipeline.Id!;
            var wafStages = await _edgeServices.ListWafStagesAsync(pipelineId, cancellationToken: cancellationToken);
            var tlsStages = await _edgeServices.ListTlsStagesAsync(pipelineId, cancellationToken: cancellationToken);
            var cacheStages = await _edgeServices.ListCacheStagesAsync(pipelineId, cancellationToken: cancellationToken);

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
