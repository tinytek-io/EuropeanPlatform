using DeveloperCli.Commands.SecurityCheck;
using FluentAssertions;
using Xunit;

namespace DeveloperCli.Tests.SecurityCheck;

public sealed class SecurityCheckEvaluatorTests
{
    private const string GatewayId = "fr-par-1/11111111-1111-1111-1111-111111111111";

    private static readonly AclSnapshot EdgeOnlyAcl = new("edge-only", 0, "deny", true, true, []);

    private static readonly LoadBalancerSnapshot Gateway = new(GatewayId, "gateway", ["51.15.0.1"]);

    private static readonly EdgePipelineSnapshot SecurePipeline = new("pipeline-app", "app", [new WafStageSnapshot("waf-app", "enable", 0)], []);

    private static SecurityInventory BuildInventory(
        LoadBalancerSnapshot[]? loadBalancers = null,
        FrontendSnapshot[]? frontends = null,
        EdgePipelineSnapshot[]? pipelines = null)
    {
        return new SecurityInventory(
            GatewayId,
            loadBalancers ?? [Gateway],
            frontends ?? [new FrontendSnapshot("frontend-443", "app-https", 443, [EdgeOnlyAcl])],
            pipelines ?? [SecurePipeline]
        );
    }

    private static SecurityCheckResult ResultOf(SecurityInventory inventory, string checkName)
    {
        return SecurityCheckEvaluator.Evaluate(inventory).Single(result => result.Name == checkName);
    }

    [Fact]
    public void Evaluate_WhenEverythingIsLockedDown_ShouldPassEveryCheck()
    {
        var results = SecurityCheckEvaluator.Evaluate(BuildInventory());

        results.Should().OnlyContain(result => result.Passed);
        results.Select(result => result.Name).Should().BeEquivalentTo(
            SecurityCheckEvaluator.GatewayFrontendsEdgeOnly, SecurityCheckEvaluator.OnlyGatewayLoadBalancerIsPublic, SecurityCheckEvaluator.EdgeWafEnforced
        );
    }

    [Fact]
    public void Evaluate_WhenAFrontendHasNoAcl_ShouldFailNamingThePort()
    {
        var inventory = BuildInventory(frontends:
            [
                new FrontendSnapshot("frontend-443", "app-https", 443, [EdgeOnlyAcl]),
                new FrontendSnapshot("frontend-80", "http", 80, [])
            ]
        );

        var result = ResultOf(inventory, SecurityCheckEvaluator.GatewayFrontendsEdgeOnly);

        result.Passed.Should().BeFalse();
        result.Findings.Should().ContainSingle().Which.Should().Contain("port 80");
    }

    [Fact]
    public void Evaluate_WhenAnAllowAclRunsBeforeTheEdgeOnlyAcl_ShouldFail()
    {
        var allowEveryone = new AclSnapshot("allow-everyone", 0, "allow", false, false, ["0.0.0.0/0"]);
        var edgeOnlyLater = EdgeOnlyAcl with { Index = 1 };
        var inventory = BuildInventory(frontends: [new FrontendSnapshot("frontend-443", "app-https", 443, [edgeOnlyLater, allowEveryone])]);

        ResultOf(inventory, SecurityCheckEvaluator.GatewayFrontendsEdgeOnly).Passed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_WhenTheEdgeMatchIsNotInverted_ShouldFail()
    {
        var denyEdgeOnly = EdgeOnlyAcl with { Invert = false };
        var inventory = BuildInventory(frontends: [new FrontendSnapshot("frontend-443", "app-https", 443, [denyEdgeOnly])]);

        ResultOf(inventory, SecurityCheckEvaluator.GatewayFrontendsEdgeOnly).Passed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_WhenTheEdgeMatchAlsoListsSubnets_ShouldFail()
    {
        var widenedAcl = EdgeOnlyAcl with { IpSubnets = ["203.0.113.0/24"] };
        var inventory = BuildInventory(frontends: [new FrontendSnapshot("frontend-443", "app-https", 443, [widenedAcl])]);

        ResultOf(inventory, SecurityCheckEvaluator.GatewayFrontendsEdgeOnly).Passed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_WhenTheGatewayLoadBalancerIsMissing_ShouldFail()
    {
        var inventory = BuildInventory([], []);

        var result = ResultOf(inventory, SecurityCheckEvaluator.GatewayFrontendsEdgeOnly);

        result.Passed.Should().BeFalse();
        result.Findings.Should().ContainSingle().Which.Should().Contain("was not found");
    }

    [Fact]
    public void Evaluate_WhenAnotherLoadBalancerIsPublic_ShouldFail()
    {
        var strayLoadBalancer = new LoadBalancerSnapshot("fr-par-2/22222222-2222-2222-2222-222222222222", "stray", ["51.15.0.2"]);
        var inventory = BuildInventory([Gateway, strayLoadBalancer]);

        var result = ResultOf(inventory, SecurityCheckEvaluator.OnlyGatewayLoadBalancerIsPublic);

        result.Passed.Should().BeFalse();
        result.Findings.Should().ContainSingle().Which.Should().Contain("stray");
    }

    [Fact]
    public void Evaluate_WhenAnotherLoadBalancerIsPrivate_ShouldPass()
    {
        var privateLoadBalancer = new LoadBalancerSnapshot("fr-par-1/33333333-3333-3333-3333-333333333333", "private", []);
        var inventory = BuildInventory([Gateway, privateLoadBalancer]);

        ResultOf(inventory, SecurityCheckEvaluator.OnlyGatewayLoadBalancerIsPublic).Passed.Should().BeTrue();
    }

    [Theory]
    [InlineData("log_only")]
    [InlineData("disable")]
    public void Evaluate_WhenTheWafDoesNotBlock_ShouldFail(string mode)
    {
        var pipeline = SecurePipeline with { WafStages = [new WafStageSnapshot("waf-app", mode, 0)] };

        var result = ResultOf(BuildInventory(pipelines: [pipeline]), SecurityCheckEvaluator.EdgeWafEnforced);

        result.Passed.Should().BeFalse();
        result.Findings.Should().ContainSingle().Which.Should().Contain(mode);
    }

    [Fact]
    public void Evaluate_WhenThePipelineHasNoWafStage_ShouldFail()
    {
        var pipeline = SecurePipeline with { WafStages = [] };

        ResultOf(BuildInventory(pipelines: [pipeline]), SecurityCheckEvaluator.EdgeWafEnforced).Passed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_WhenTheWafHasExclusionRules_ShouldFail()
    {
        var pipeline = SecurePipeline with { WafStages = [new WafStageSnapshot("waf-app", "enable", 2)] };

        ResultOf(BuildInventory(pipelines: [pipeline]), SecurityCheckEvaluator.EdgeWafEnforced).Passed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_WhenAStageTargetsTheBackendDirectly_ShouldFail()
    {
        var pipeline = SecurePipeline with { StagesTargetingBackendDirectly = ["cache-app"] };

        var result = ResultOf(BuildInventory(pipelines: [pipeline]), SecurityCheckEvaluator.EdgeWafEnforced);

        result.Passed.Should().BeFalse();
        result.Findings.Should().ContainSingle().Which.Should().Contain("cache-app");
    }

    [Fact]
    public void Evaluate_WhenADnsStageTargetsTheBackendDirectlyBesideABlockingWaf_ShouldFail()
    {
        // The pipeline still carries a blocking WAF stage, but its entry routes straight to the origin, so the WAF is
        // never on the path.
        var pipeline = SecurePipeline with { StagesTargetingBackendDirectly = ["dns-app"] };

        var result = ResultOf(BuildInventory(pipelines: [pipeline]), SecurityCheckEvaluator.EdgeWafEnforced);

        result.Passed.Should().BeFalse();
        result.Findings.Should().ContainSingle().Which.Should().Contain("dns-app");
    }

    [Fact]
    public void Evaluate_WhenTheProjectHasNoPipelines_ShouldFail()
    {
        ResultOf(BuildInventory(pipelines: []), SecurityCheckEvaluator.EdgeWafEnforced).Passed.Should().BeFalse();
    }
}
