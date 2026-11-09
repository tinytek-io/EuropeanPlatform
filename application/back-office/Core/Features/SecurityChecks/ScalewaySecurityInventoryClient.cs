using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Scaleway.Sdk;
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
///     read-only Load Balancer and Edge Services permission sets only. Load Balancers, frontends and ACLs come from the
///     SDK's generated Load Balancer client. Edge Services is not generated yet, so its reads go through the SDK's
///     transport, which still owns paging and error classification.
/// </summary>
public sealed class ScalewaySecurityInventoryClient(HttpClient httpClient, IOptions<SecurityChecksOptions> options)
{
    // Load Balancers are zonal; a region has these three zones (fr-par, nl-ams and pl-waw alike).
    private static readonly int[] ZoneNumbers = [1, 2, 3];

    // No request scope: a scope needs the organization, which the worker is not given. Listing names the project.
    private readonly ScwLb.LbApi _loadBalancers = new(new ScalewayApiClient(httpClient));

    private readonly string _projectId = Environment.GetEnvironmentVariable("SCW_DEFAULT_PROJECT_ID")
                                         ?? throw new InvalidOperationException("SCW_DEFAULT_PROJECT_ID environment variable is not configured.");

    private readonly string _region = Environment.GetEnvironmentVariable("SCW_DEFAULT_REGION")
                                      ?? throw new InvalidOperationException("SCW_DEFAULT_REGION environment variable is not configured.");

    private readonly ScalewayApiClient _scaleway = new(httpClient);

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
        // Edge Services is a global API: the paths carry no region.
        var pipelines = await _scaleway.ListResourcesAsync("edge-services/v1beta1/pipelines", _region, [new KeyValuePair<string, string>("project_id", _projectId)], cancellationToken);

        var snapshots = new List<EdgePipelineSnapshot>();
        foreach (var pipeline in pipelines)
        {
            var pipelineId = pipeline.GetProperty("id").GetString()!;
            var wafStages = await ListPipelineStagesAsync(pipelineId, "waf-stages", cancellationToken);
            var tlsStages = await ListPipelineStagesAsync(pipelineId, "tls-stages", cancellationToken);
            var cacheStages = await ListPipelineStagesAsync(pipelineId, "cache-stages", cancellationToken);

            var stagesTargetingBackendDirectly = tlsStages.Concat(cacheStages)
                .Where(stage => stage.TryGetProperty("backend_stage_id", out var backendStageId) && backendStageId.ValueKind == JsonValueKind.String)
                .Select(stage => stage.GetProperty("id").GetString()!)
                .ToArray();

            snapshots.Add(new EdgePipelineSnapshot(
                    pipelineId,
                    pipeline.GetProperty("name").GetString()!,
                    wafStages.Select(stage => new WafStageSnapshot(stage.GetProperty("id").GetString()!, stage.GetProperty("mode").GetString()!, ReadArray(stage, "exclusion_rules").Length)).ToArray(),
                    stagesTargetingBackendDirectly
                )
            );
        }

        return snapshots.ToArray();
    }

    private Task<JsonElement[]> ListPipelineStagesAsync(string pipelineId, string stageCollection, CancellationToken cancellationToken)
    {
        return _scaleway.ListResourcesAsync($"edge-services/v1beta1/pipelines/{pipelineId}/{stageCollection}", _region, null, cancellationToken);
    }

    // Scaleway omits or nulls an empty optional array depending on the API, so both read as empty.
    private static JsonElement[] ReadArray(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToArray() : [];
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
