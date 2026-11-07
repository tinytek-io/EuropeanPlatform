namespace BackOffice.Features.SecurityChecks;

/// <summary>
///     What the security checks evaluate: a read-only snapshot of the project's public entry points, taken from the
///     Scaleway API in one pass so every check judges the same moment.
/// </summary>
/// <param name="GatewayLoadBalancerId">The gateway LB the edge pipelines target, zone-qualified ("fr-par-1/&lt;uuid&gt;").</param>
/// <param name="LoadBalancers">Every Load Balancer in the project, across the region's zones.</param>
/// <param name="GatewayFrontends">The gateway LB's frontends with their ACLs (empty when the gateway LB is missing).</param>
/// <param name="EdgePipelines">Every Edge Services pipeline in the project.</param>
public sealed record SecurityInventory(
    string GatewayLoadBalancerId,
    LoadBalancerSnapshot[] LoadBalancers,
    FrontendSnapshot[] GatewayFrontends,
    EdgePipelineSnapshot[] EdgePipelines
);

/// <param name="Id">Zone-qualified id, the same shape Terraform uses.</param>
/// <param name="PublicIpAddresses">The flexible IPs attached to the LB; empty for a private LB.</param>
public sealed record LoadBalancerSnapshot(string Id, string Name, string[] PublicIpAddresses);

public sealed record FrontendSnapshot(string Id, string Name, int InboundPort, AclSnapshot[] Acls);

/// <param name="ActionType">Scaleway's action type: "allow", "deny" or "redirect".</param>
/// <param name="MatchesEdgeServices">The match targets Scaleway Edge Services' source IPs.</param>
/// <param name="Invert">The match is an "unless" condition.</param>
public sealed record AclSnapshot(
    string Name,
    int Index,
    string ActionType,
    bool MatchesEdgeServices,
    bool Invert,
    string[] IpSubnets
);

/// <param name="StagesTargetingBackendDirectly">
///     Ids of the TLS and cache stages that point straight at a backend stage, which routes traffic past any WAF
///     stage in the pipeline.
/// </param>
public sealed record EdgePipelineSnapshot(string Id, string Name, WafStageSnapshot[] WafStages, string[] StagesTargetingBackendDirectly);

/// <param name="Mode">Scaleway's WAF mode: "enable" (blocking), "log_only" or "disable".</param>
public sealed record WafStageSnapshot(string Id, string Mode, int ExclusionRuleCount);
