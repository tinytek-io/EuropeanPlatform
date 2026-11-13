using System.Text.Json;
using DeveloperCli.Commands.CostEstimate;
using FluentAssertions;
using Scaleway.Sdk;
using Xunit;

namespace DeveloperCli.Tests.CostEstimate;

public sealed class CostEstimatorTests
{
    private static readonly Dictionary<string, CatalogProduct[]> Catalog = new()
    {
        ["zone-fr-par-1"] =
        [
            new CatalogProduct("/instance/server/play2_nano/fr-par-1", "Instance", "PLAY2-NANO", "PLAY2-NANO - fr-par-1", "fr-par-1", 0.02754m, "hour", 1, "general_availability"),
            new CatalogProduct("/storage/block/volume-low-latency-5k/fr-par-1", "Block Storage", "Volume Low Latency 5K", "Block Storage Volume Low Latency 5K - fr-par-1", "fr-par-1", 0.00013m, "gigabyte", 1, "general_availability"),
            new CatalogProduct("/network/loadbalancer/loadbalancer-s/par1", "Loadbalancer", "LB-S", "Loadbalancer LB-S - fr-par-1", "fr-par-1", 0.023m, "node", 1, "general_availability")
        ],
        ["region-fr-par"] =
        [
            new CatalogProduct("/k8s/control-plane/fr-par", "Kubernetes", "Kapsule Mutualized", "Kubernetes Kapsule Mutualized - fr-par", "fr-par", 0m, "hour", 1, "general_availability"),
            new CatalogProduct("/paas/tem/scale_plan/fr-par", "Transactional Email", "Transactional Email Plan", "Transactional Email Plan - fr-par", "fr-par", 80m, "plan", 1, "general_availability"),
            new CatalogProduct("/paas/tem/scale_emails/fr-par", "Transactional Email", "Transactional Emails", "Transactional Emails - fr-par", "fr-par", 0.0002m, "email", 1, "general_availability"),
            new CatalogProduct("/paas/tem/essential_emails/fr-par", "Transactional Email", "Transactional Emails", "Transactional Emails - fr-par", "fr-par", 0.00025m, "email", 1, "general_availability")
        ],
        ["global"] =
        [
            new CatalogProduct("/network/edge-services/plan/starter", "Edge Services", "Edge Services - Plan", "Edge Services - Plan - starter", "global", 0.99m, "plan", 1, "general_availability"),
            new CatalogProduct("/network/edge-services/plan/professional", "Edge Services", "Edge Services - Plan", "Edge Services - Plan - professional", "global", 12.99m, "plan", 1, "general_availability"),
            new CatalogProduct("/network/edge-services/pipeline/starter", "Edge Services", "Edge Services - Pipelines", "Edge Services - Pipelines - starter", "global", 0.005376m, "node", 1, "general_availability"),
            new CatalogProduct("/network/edge-services/waf/starter", "Edge Services", "Edge Services - WAF", "Edge Services - WAF - starter", "global", 4m, "plan", 1, "general_availability")
        ]
    };

    private readonly CostEstimator _estimator = new(locality => Task.FromResult<IReadOnlyList<CatalogProduct>>(Catalog.GetValueOrDefault(locality.Key) ?? []));

    [Fact]
    public async Task EstimateAsync_WhenProductsAreBilledHourly_ShouldPriceAMonthOf730HoursPerUnit()
    {
        // Arrange
        var pool = Resource("scaleway_k8s_pool.default", "scaleway_k8s_pool", """{ "node_type": "play2_nano", "size": 2, "zone": "fr-par-1", "root_volume_type": "sbs_5k", "root_volume_size_in_gb": 20 }""");

        // Act
        var estimate = await _estimator.EstimateAsync([pool]);

        // Assert
        estimate.Lines.Should().BeEquivalentTo([
                new CostLine("scaleway_k8s_pool.default", "2 × PLAY2-NANO nodes", 2 * 0.02754m * 730m),
                new CostLine("scaleway_k8s_pool.default", "2 × 20 GB node root volumes", 40 * 0.00013m * 730m)
            ]
        );
        estimate.Unpriced.Should().BeEmpty();
    }

    [Fact]
    public async Task EstimateAsync_WhenTheEdgePlanIsStarter_ShouldPriceThePlanAndWafMonthlyAndTheWafOnce()
    {
        // Arrange
        TerraformResource[] resources =
        [
            Resource("scaleway_edge_services_plan.main", "scaleway_edge_services_plan", """{ "name": "starter" }"""),
            Resource("scaleway_edge_services_pipeline.gateway[\"app\"]", "scaleway_edge_services_pipeline", "{}"),
            Resource("scaleway_edge_services_waf_stage.gateway[\"app\"]", "scaleway_edge_services_waf_stage", "{}"),
            Resource("scaleway_edge_services_waf_stage.gateway[\"back-office\"]", "scaleway_edge_services_waf_stage", "{}")
        ];

        // Act
        var estimate = await _estimator.EstimateAsync(resources);

        // Assert
        estimate.Lines.Select(line => line.MonthlyCost).Should().BeEquivalentTo([0.99m, 4m, 0m], "starter includes one pipeline, so the only pipeline is free");
    }

    [Fact]
    public async Task EstimateAsync_WhenPipelinesExceedThePlanAllowance_ShouldPriceOnlyTheAdditionalOnes()
    {
        // Arrange
        TerraformResource[] resources =
        [
            Resource("scaleway_edge_services_plan.main", "scaleway_edge_services_plan", """{ "name": "starter" }"""),
            Resource("scaleway_edge_services_pipeline.gateway[\"app\"]", "scaleway_edge_services_pipeline", "{}"),
            Resource("scaleway_edge_services_pipeline.gateway[\"back-office\"]", "scaleway_edge_services_pipeline", "{}"),
            Resource("scaleway_edge_services_pipeline.gateway[\"docs\"]", "scaleway_edge_services_pipeline", "{}")
        ];

        // Act
        var estimate = await _estimator.EstimateAsync(resources);

        // Assert
        var pipelines = estimate.Lines.Should().ContainSingle(line => line.Description.StartsWith("3 Edge Services pipelines")).Subject;
        pipelines.MonthlyCost.Should().Be(2 * 0.005376m * 730m);
        pipelines.Description.Should().Be("3 Edge Services pipelines (1 included in starter, 2 additional)");
    }

    [Fact]
    public async Task EstimateAsync_WhenThePlanHasNoWafAddOn_ShouldListWafRequestsAsUsageBilled()
    {
        // Arrange
        TerraformResource[] resources =
        [
            Resource("scaleway_edge_services_plan.main", "scaleway_edge_services_plan", """{ "name": "professional" }"""),
            Resource("scaleway_edge_services_waf_stage.gateway[\"app\"]", "scaleway_edge_services_waf_stage", "{}")
        ];

        // Act
        var estimate = await _estimator.EstimateAsync(resources);

        // Assert
        estimate.Lines.Should().ContainSingle().Which.MonthlyCost.Should().Be(12.99m);
        estimate.UsageBilled.Should().Contain("Edge Services WAF: per request inspected");
    }

    [Theory]
    [InlineData("scale", 80)]
    [InlineData("essential", 0)]
    public async Task EstimateAsync_WhenScalewayReportsAnEmailOffer_ShouldPriceItsMonthlyFee(string offer, int expectedMonthlyCost)
    {
        // Act
        var estimate = await _estimator.EstimateAsync([], new LiveSubscriptions(null, offer, "fr-par"));

        // Assert
        estimate.Lines.Should().ContainSingle().Which.MonthlyCost.Should().Be(expectedMonthlyCost);
        estimate.Unpriced.Should().BeEmpty();
    }

    [Fact]
    public async Task EstimateAsync_WhenTheEmailOfferCouldNotBeRead_ShouldReportTheEstimateIncomplete()
    {
        // Act
        var estimate = await _estimator.EstimateAsync([], new LiveSubscriptions(null, null, "fr-par", TransactionalEmailOfferUnreadable: true));

        // Assert
        estimate.Unpriced.Should().ContainSingle().Which.Reason.Should().Contain("offer subscription could not be read");
    }

    [Fact]
    public async Task EstimateAsync_WhenTheBilledEdgePlanCouldNotBeRead_ShouldReportTheEstimateIncomplete()
    {
        // Arrange
        var plan = Resource("scaleway_edge_services_plan.main", "scaleway_edge_services_plan", """{ "name": "starter" }""");

        // Act
        var estimate = await _estimator.EstimateAsync([plan], new LiveSubscriptions(null, null, "fr-par", true));

        // Assert
        estimate.Unpriced.Should().ContainSingle().Which.Reason.Should().Contain("plan Scaleway bills could not be read");
    }

    [Fact]
    public async Task EstimateAsync_WhenScalewayBillsAnotherEdgePlanThanTerraform_ShouldPriceTheBilledPlanAndWarn()
    {
        // Arrange
        var plan = Resource("scaleway_edge_services_plan.main", "scaleway_edge_services_plan", """{ "name": "starter" }""");

        // Act
        var estimate = await _estimator.EstimateAsync([plan], new LiveSubscriptions("professional", null, "fr-par"));

        // Assert
        estimate.Lines.Should().ContainSingle().Which.MonthlyCost.Should().Be(12.99m);
        estimate.Warnings.Should().ContainSingle().Which.Should().Contain("Scaleway bills the Edge Services professional plan, but Terraform configures starter");
    }

    [Fact]
    public async Task EstimateAsync_WhenAResourceTypeHasNoRule_ShouldReportItUnpricedInsteadOfCountingItFree()
    {
        // Arrange
        TerraformResource[] resources =
        [
            Resource("scaleway_instance_server.bastion", "scaleway_instance_server", """{ "type": "PLAY2-NANO" }"""),
            Resource("random_password.database", "random_password", "{}"),
            Resource("scaleway_iam_application.workload", "scaleway_iam_application", "{}")
        ];

        // Act
        var estimate = await _estimator.EstimateAsync(resources);

        // Assert
        estimate.Unpriced.Should().ContainSingle().Which.Address.Should().Be("scaleway_instance_server.bastion");
        estimate.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task EstimateAsync_WhenTheCatalogDoesNotListTheProduct_ShouldReportWhatWasMissing()
    {
        // Arrange
        var loadBalancer = Resource("scaleway_lb.gateway", "scaleway_lb", """{ "type": "lb-gp-m", "zone": "fr-par-1" }""");

        // Act
        var estimate = await _estimator.EstimateAsync([loadBalancer]);

        // Assert
        estimate.Unpriced.Should().ContainSingle().Which.Reason.Should().Be("the catalog lists no Loadbalancer 'LB-GP-M' in zone-fr-par-1");
    }

    [Fact]
    public void Read_ShouldTakeManagedResourcesFromAStateOrAPlanIncludingChildModules()
    {
        // Arrange
        const string state = """
                             { "values": { "root_module": {
                               "resources": [
                                 { "address": "scaleway_lb.gateway", "mode": "managed", "type": "scaleway_lb", "values": { "type": "lb-s" } },
                                 { "address": "data.scaleway_lb_frontends.gateway", "mode": "data", "type": "scaleway_lb_frontends", "values": {} }
                               ],
                               "child_modules": [ { "resources": [
                                 { "address": "module.edge.scaleway_edge_services_plan.main", "mode": "managed", "type": "scaleway_edge_services_plan", "values": { "name": "starter" } }
                               ] } ] } } }
                             """;
        const string plan = """{ "planned_values": { "root_module": { "resources": [ { "address": "scaleway_lb.gateway", "mode": "managed", "type": "scaleway_lb", "values": { "type": "lb-gp-m" } } ] } } }""";

        // Act
        var fromState = TerraformResources.Read(state);
        var fromPlan = TerraformResources.Read(plan);

        // Assert
        fromState.Select(resource => resource.Address).Should().Equal("scaleway_lb.gateway", "module.edge.scaleway_edge_services_plan.main");
        fromPlan.Should().ContainSingle().Which.Values.GetProperty("type").GetString().Should().Be("lb-gp-m");
    }

    private static TerraformResource Resource(string address, string type, string values)
    {
        return new TerraformResource(address, type, JsonDocument.Parse(values).RootElement.Clone());
    }
}
