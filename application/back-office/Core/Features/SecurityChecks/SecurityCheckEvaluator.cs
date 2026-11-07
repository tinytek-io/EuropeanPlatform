namespace BackOffice.Features.SecurityChecks;

/// <param name="Name">Stable check name: the log and alert key.</param>
/// <param name="Findings">Why the check failed, one entry per offending resource; empty when it passed.</param>
public sealed record SecurityCheckResult(string Name, string[] Findings)
{
    public bool Passed => Findings.Length == 0;
}

/// <summary>
///     Deterministic pass/fail rules over a <see cref="SecurityInventory" />: the guarantees that keep the WAF from
///     being bypassed. Every rule states what must hold, so a failure names the resource that breaks it.
/// </summary>
public static class SecurityCheckEvaluator
{
    public const string GatewayFrontendsEdgeOnly = "gateway-frontends-edge-only";
    public const string OnlyGatewayLoadBalancerIsPublic = "only-gateway-load-balancer-is-public";
    public const string EdgeWafEnforced = "edge-waf-enforced";

    private const string DenyAction = "deny";
    private const string BlockingWafMode = "enable";

    public static SecurityCheckResult[] Evaluate(SecurityInventory inventory)
    {
        return
        [
            CheckGatewayFrontendsAreEdgeOnly(inventory),
            CheckOnlyGatewayLoadBalancerIsPublic(inventory),
            CheckEdgeWafIsEnforced(inventory)
        ];
    }

    // Envoy routes by SNI, not by frontend, so a single frontend that accepts any source serves every hostname
    // without the WAF. Every frontend on the gateway LB must therefore refuse everything but Edge Services before
    // any other rule applies: its first ACL denies unless the source is an Edge Services IP.
    private static SecurityCheckResult CheckGatewayFrontendsAreEdgeOnly(SecurityInventory inventory)
    {
        if (inventory.LoadBalancers.All(loadBalancer => loadBalancer.Id != inventory.GatewayLoadBalancerId))
        {
            return new SecurityCheckResult(GatewayFrontendsEdgeOnly, [$"Gateway Load Balancer '{inventory.GatewayLoadBalancerId}' was not found in the project."]);
        }

        var findings = inventory.GatewayFrontends
            .Where(frontend => !IsEdgeOnly(frontend))
            .Select(frontend => $"Frontend '{frontend.Name}' (port {frontend.InboundPort}) accepts connections from sources other than Edge Services.")
            .ToArray();

        return new SecurityCheckResult(GatewayFrontendsEdgeOnly, findings);
    }

    private static bool IsEdgeOnly(FrontendSnapshot frontend)
    {
        var firstAcl = frontend.Acls.OrderBy(acl => acl.Index).FirstOrDefault();

        // An IP list next to the edge match widens what the rule matches, so the edge match must stand alone.
        return firstAcl is { ActionType: DenyAction, MatchesEdgeServices: true, Invert: true, IpSubnets.Length: 0 };
    }

    // Any other public LB in the project is an entry point the edge does not front.
    private static SecurityCheckResult CheckOnlyGatewayLoadBalancerIsPublic(SecurityInventory inventory)
    {
        var findings = inventory.LoadBalancers
            .Where(loadBalancer => loadBalancer.Id != inventory.GatewayLoadBalancerId && loadBalancer.PublicIpAddresses.Length > 0)
            .Select(loadBalancer => $"Load Balancer '{loadBalancer.Name}' ({loadBalancer.Id}) is public on {string.Join(", ", loadBalancer.PublicIpAddresses)}.")
            .ToArray();

        return new SecurityCheckResult(OnlyGatewayLoadBalancerIsPublic, findings);
    }

    // Every pipeline must block (not just log) with the managed ruleset, carry no exclusions the platform has not
    // reviewed (none today; a reviewed exclusion changes this rule with it), and route all traffic through the WAF.
    private static SecurityCheckResult CheckEdgeWafIsEnforced(SecurityInventory inventory)
    {
        if (inventory.EdgePipelines.Length == 0)
        {
            return new SecurityCheckResult(EdgeWafEnforced, ["The project has no Edge Services pipelines."]);
        }

        var findings = new List<string>();
        foreach (var pipeline in inventory.EdgePipelines)
        {
            if (pipeline.WafStages.Length == 0)
            {
                findings.Add($"Pipeline '{pipeline.Name}' has no WAF stage.");
            }

            foreach (var wafStage in pipeline.WafStages)
            {
                if (wafStage.Mode != BlockingWafMode)
                {
                    findings.Add($"Pipeline '{pipeline.Name}' WAF stage '{wafStage.Id}' runs in mode '{wafStage.Mode}', not '{BlockingWafMode}'.");
                }

                if (wafStage.ExclusionRuleCount > 0)
                {
                    findings.Add($"Pipeline '{pipeline.Name}' WAF stage '{wafStage.Id}' has {wafStage.ExclusionRuleCount} exclusion rules.");
                }
            }

            findings.AddRange(pipeline.StagesTargetingBackendDirectly.Select(stageId => $"Pipeline '{pipeline.Name}' stage '{stageId}' targets the backend directly, bypassing the WAF."));
        }

        return new SecurityCheckResult(EdgeWafEnforced, findings.ToArray());
    }
}
