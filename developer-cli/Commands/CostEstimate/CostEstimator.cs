using System.Text.Json;
using Scaleway.Sdk;

// DeveloperCli.Tests compiles this file from outside its folder; the namespace matches its place in the CLI.
// ReSharper disable once CheckNamespace
namespace DeveloperCli.Commands.CostEstimate;

/// <summary>A managed resource from Terraform's JSON output (a state or a saved plan's planned values).</summary>
/// <param name="Address">The resource address (<c>scaleway_k8s_pool.default</c>), indexed for count and for_each.</param>
/// <param name="Type">The resource type (<c>scaleway_k8s_pool</c>).</param>
/// <param name="Values">The resource's attribute values.</param>
public sealed record TerraformResource(string Address, string Type, JsonElement Values);

/// <param name="Address">The Terraform resource the cost comes from.</param>
/// <param name="Description">What is billed ("2 × PLAY2-NANO nodes").</param>
/// <param name="MonthlyCost">Euros per month, excluding VAT.</param>
public sealed record CostLine(string Address, string Description, decimal MonthlyCost);

/// <param name="Address">The Terraform resource that could not be priced.</param>
/// <param name="Reason">Why, so the pricing rule can be added or corrected.</param>
public sealed record UnpricedResource(string Address, string Reason);

/// <summary>
///     What the project is subscribed to outside Terraform, as Scaleway reports it, or null where it was not read.
/// </summary>
/// <param name="EdgeServicesPlan">The Edge Services plan Scaleway bills (<c>professional</c>).</param>
/// <param name="TransactionalEmailOffer">
///     The Transactional Email offer the project subscribes to (<c>essential</c>,
///     <c>scale</c>).
/// </param>
/// <param name="TransactionalEmailRegion">The region the offer is subscribed in.</param>
public sealed record LiveSubscriptions(string? EdgeServicesPlan, string? TransactionalEmailOffer, string TransactionalEmailRegion);

/// <param name="Lines">The fixed monthly costs: what the environment costs before anyone uses it.</param>
/// <param name="UsageBilled">What is billed by use (storage, traffic, emails, requests), named but not estimated.</param>
/// <param name="Unpriced">Resources no rule covers; while any exist the total is a lower bound, not an estimate.</param>
/// <param name="Warnings">Where Scaleway bills something other than Terraform describes; the estimate follows Scaleway.</param>
public sealed record MonthlyCostEstimate(CostLine[] Lines, string[] UsageBilled, UnpricedResource[] Unpriced, string[] Warnings)
{
    public decimal MonthlyTotal => Lines.Sum(line => line.MonthlyCost);
}

/// <summary>
///     Prices an environment's Terraform resources from Scaleway's public catalog. Every resource type is listed
///     here as priced, usage-billed or free; a type that is none of them is reported unpriced rather than counted as
///     €0, so a new kind of resource cannot slip past a budget unnoticed.
///     <para>
///         The catalog states a price per unit but not the period it is billed over, so each rule says whether its
///         product is billed per hour (Instances, Load Balancers, gateways, IPs, pipelines) or per month (plans).
///         Hourly prices are turned into a month of 730 hours, Scaleway's own monthly cap.
///     </para>
/// </summary>
public sealed class CostEstimator(Func<CatalogLocality, Task<IReadOnlyList<CatalogProduct>>> catalogProducts)
{
    public const decimal HoursPerMonth = 730m;

    private static readonly Dictionary<string, string> UsageBilledTypes = new()
    {
        ["scaleway_object_bucket"] = "Object Storage: per GB stored and per GB sent out",
        ["scaleway_cockpit_source"] = "Cockpit: custom metrics, logs and traces, per volume ingested and retained",
        ["scaleway_secret_version"] = "Secret Manager: per secret version and per access",
        ["scaleway_tem_domain"] = "Transactional Email: per email sent",
        ["scaleway_edge_services_cache_stage"] = "Edge Services cache: per GB served",
        ["scaleway_rdb_instance"] = "Managed Database backups: per GB stored"
    };

    // Billed nothing on their own: configuration of a priced resource, identities, or networking that is included.
    private static readonly HashSet<string> FreeTypes =
    [
        "scaleway_cockpit_token", "scaleway_edge_services_backend_stage", "scaleway_edge_services_dns_stage", "scaleway_edge_services_head_stage",
        "scaleway_edge_services_tls_stage", "scaleway_iam_api_key", "scaleway_iam_application", "scaleway_iam_policy", "scaleway_lb_acl", "scaleway_lb_backend",
        "scaleway_lb_frontend", "scaleway_object", "scaleway_object_bucket_policy", "scaleway_object_bucket_website_configuration", "scaleway_rdb_acl",
        "scaleway_rdb_database", "scaleway_rdb_privilege", "scaleway_rdb_user", "scaleway_secret", "scaleway_vpc_gateway_network", "scaleway_vpc_private_network"
    ];

    // The pipelines each Edge Services plan includes before extra ones are billed. The catalog prices an additional
    // pipeline but does not carry the allowance; these are from scaleway.com/en/pricing/network (checked 2026-11-10).
    private static readonly Dictionary<string, int> EdgePipelinesIncluded = new(StringComparer.OrdinalIgnoreCase)
    {
        ["starter"] = 1, ["professional"] = 10, ["advanced"] = 100
    };

    // Providers that run on the machine applying the plan (passwords, keys, certificates) and bill nobody.
    private static readonly string[] LocalProviderPrefixes = ["random_", "tls_", "time_", "null_", "terraform_data"];

    /// <param name="resources">The environment's resources, from its state or a saved plan.</param>
    /// <param name="subscriptions">What Scaleway reports the project subscribes to outside Terraform, when it was read.</param>
    public async Task<MonthlyCostEstimate> EstimateAsync(IReadOnlyList<TerraformResource> resources, LiveSubscriptions? subscriptions = null)
    {
        var lines = new List<CostLine>();
        var unpriced = new List<UnpricedResource>();
        var usageBilled = new SortedSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();

        // Edge Services products are priced per plan, and a project has one plan. Terraform owns it, but the plan
        // Scaleway bills is the one that costs money: a console change shows up here before the next apply reverts it.
        var edgePlan = resources.Where(resource => resource.Type == "scaleway_edge_services_plan").Select(resource => String(resource, "name")).FirstOrDefault();
        if (subscriptions?.EdgeServicesPlan is { Length: > 0 } livePlan && edgePlan is not null && !livePlan.Equals(edgePlan, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Scaleway bills the Edge Services {livePlan} plan, but Terraform configures {edgePlan}; the estimate prices {livePlan}, and the next apply moves it back to {edgePlan}.");
            edgePlan = livePlan;
        }

        var edgeWafPriced = false;

        foreach (var resource in resources)
        {
            if (UsageBilledTypes.TryGetValue(resource.Type, out var usage)) usageBilled.Add(usage);

            switch (resource.Type)
            {
                case "scaleway_k8s_cluster":
                    await PriceAsync(resource, CatalogLocality.ForRegion(String(resource, "region")), "Kubernetes", ControlPlaneProduct(String(resource, "type")), 1, true, "Kubernetes control plane");
                    break;
                case "scaleway_k8s_pool":
                    await PriceNodePoolAsync(resource);
                    break;
                case "scaleway_lb":
                    await PriceAsync(resource, CatalogLocality.ForZone(String(resource, "zone")), "Loadbalancer", String(resource, "type").ToUpperInvariant(), 1, true, "Load Balancer");
                    break;
                case "scaleway_lb_ip":
                    await PriceAsync(resource, CatalogLocality.ForZone(String(resource, "zone")), "Loadbalancer", "Load Balancer IP", 1, true, "Load Balancer IP");
                    break;
                case "scaleway_vpc_public_gateway":
                    await PriceAsync(resource, CatalogLocality.ForZone(String(resource, "zone")), "Public Gateway", $"VPC Public Gateway {String(resource, "type").Replace("VPC-GW-", "", StringComparison.OrdinalIgnoreCase)}", 1, true, "Public Gateway");
                    break;
                case "scaleway_vpc_public_gateway_ip":
                    await PriceAsync(resource, CatalogLocality.ForZone(String(resource, "zone")), "Public Gateway", "VPC Public Gateway IP", 1, true, "Public Gateway IP");
                    break;
                case "scaleway_rdb_instance":
                    await PriceDatabaseAsync(resource);
                    break;
                case "scaleway_edge_services_plan":
                    await PriceAsync(resource, CatalogLocality.Global, "Edge Services", "Edge Services - Plan", 1, false, $"Edge Services {edgePlan} plan", $"- {edgePlan}");
                    break;
                case "scaleway_edge_services_pipeline":
                    // Priced together after the loop: the plan includes some, and only the rest are billed.
                    break;
                case "scaleway_edge_services_waf_stage":
                    // The starter plan sells the WAF as a monthly add-on; the larger plans include it and bill its
                    // requests. Either way it is paid once per plan, however many pipelines run a WAF stage.
                    var wafAddOn = edgePlan is null ? null : await FindAsync(CatalogLocality.Global, "Edge Services", "Edge Services - WAF", $"- {edgePlan}");
                    if (wafAddOn is null)
                    {
                        usageBilled.Add("Edge Services WAF: per request inspected");
                    }
                    else if (!edgeWafPriced)
                    {
                        lines.Add(new CostLine(resource.Address, $"Edge Services WAF ({edgePlan} add-on)", wafAddOn.Price));
                        edgeWafPriced = true;
                    }

                    break;
                default:
                    if (!UsageBilledTypes.ContainsKey(resource.Type) && !FreeTypes.Contains(resource.Type) && !LocalProviderPrefixes.Any(resource.Type.StartsWith))
                    {
                        unpriced.Add(new UnpricedResource(resource.Address, $"no pricing rule for {resource.Type}; add one to CostEstimator"));
                    }

                    break;
            }
        }

        await PriceEdgePipelinesAsync(resources.Where(resource => resource.Type == "scaleway_edge_services_pipeline").ToArray());

        if (subscriptions?.TransactionalEmailOffer is { Length: > 0 } emailOffer)
        {
            await PriceEmailOfferAsync(emailOffer, subscriptions.TransactionalEmailRegion);
        }

        return new MonthlyCostEstimate(lines.ToArray(), usageBilled.ToArray(), unpriced.ToArray(), warnings.ToArray());

        // The offer subscription is not a Terraform resource (the deploy subscribes to the free essential offer when
        // there is none), so it is priced from what Scaleway reports. An offer with a monthly fee has a plan line in the
        // catalog (/paas/tem/scale_plan/<region>); one with only per-email lines has no fixed cost.
        async Task PriceEmailOfferAsync(string offer, string region)
        {
            const string address = "Transactional Email offer (outside Terraform)";
            var products = await catalogProducts(CatalogLocality.ForRegion(region));
            var plan = products.FirstOrDefault(product => product.Sku.Equals($"/paas/tem/{offer}_plan/{region}", StringComparison.OrdinalIgnoreCase));
            if (plan is not null)
            {
                lines.Add(new CostLine(address, $"Transactional Email {offer} offer", plan.Price));
                return;
            }

            if (products.Any(product => product.Sku.Equals($"/paas/tem/{offer}_emails/{region}", StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add(new CostLine(address, $"Transactional Email {offer} offer (no monthly fee)", 0m));
                return;
            }

            unpriced.Add(new UnpricedResource(address, $"the catalog lists neither a plan nor per-email pricing for the Transactional Email '{offer}' offer in {region}"));
        }

        async Task PriceEdgePipelinesAsync(TerraformResource[] pipelines)
        {
            if (pipelines.Length == 0) return;

            if (edgePlan is null || !EdgePipelinesIncluded.TryGetValue(edgePlan, out var included))
            {
                unpriced.AddRange(pipelines.Select(pipeline => new UnpricedResource(pipeline.Address, $"Edge Services pipelines are priced against the plan's allowance, and no allowance is known for the plan '{edgePlan}'")));
                return;
            }

            var additional = Math.Max(0, pipelines.Length - included);
            var description = $"{pipelines.Length} Edge Services pipeline{(pipelines.Length == 1 ? "" : "s")} ({included} included in {edgePlan}{(additional > 0 ? $", {additional} additional" : "")})";
            if (additional == 0)
            {
                lines.Add(new CostLine(pipelines[0].Address, description, 0m));
                return;
            }

            await PriceAsync(pipelines[0], CatalogLocality.Global, "Edge Services", "Edge Services - Pipelines", additional, true, description, $"- {edgePlan}");
        }

        async Task PriceNodePoolAsync(TerraformResource resource)
        {
            var zone = CatalogLocality.ForZone(String(resource, "zone"));
            var nodeType = InstanceType(String(resource, "node_type"));
            var nodes = Number(resource, "size");
            await PriceAsync(resource, zone, "Instance", nodeType, nodes, true, $"{nodes:0} × {nodeType} node{(nodes == 1 ? "" : "s")}");

            // Local SSD root volumes come with the Instance; block volumes are billed per GB on top of it.
            var volumeProduct = String(resource, "root_volume_type") switch
            {
                "sbs_5k" => "Volume Low Latency 5K",
                "sbs_15k" => "Volume Low Latency 15K",
                "b_ssd" => "Volume SSD",
                _ => null
            };
            var volumeSize = Number(resource, "root_volume_size_in_gb");
            if (volumeProduct is not null && volumeSize > 0)
            {
                await PriceAsync(resource, zone, "Block Storage", volumeProduct, nodes * volumeSize, true, $"{nodes:0} × {volumeSize:0} GB node root volume{(nodes == 1 ? "" : "s")}");
            }
        }

        async Task PriceDatabaseAsync(TerraformResource resource)
        {
            var nodeType = InstanceType(String(resource, "node_type"));
            var highlyAvailable = resource.Values.TryGetProperty("is_ha_cluster", out var ha) && ha.ValueKind == JsonValueKind.True;
            var product = highlyAvailable ? $"Database management Multi-AZ {nodeType}" : $"Database Management {nodeType}";
            await PriceAsync(resource, CatalogLocality.ForRegion(String(resource, "region")), "PostgreSQL and MySQL", product, 1, true, $"Managed Database {nodeType}{(highlyAvailable ? " (Multi-AZ)" : "")}");

            // Local storage comes with the node; Block Storage volumes are billed per GB, by a product the regional
            // catalog does not list for databases.
            if (String(resource, "volume_type").StartsWith("sbs", StringComparison.Ordinal))
            {
                usageBilled.Add("Managed Database Block Storage volume: per GB provisioned");
            }
        }

        async Task PriceAsync(TerraformResource resource, CatalogLocality locality, string category, string product, decimal quantity, bool hourly, string description, string? variantSuffix = null)
        {
            var catalogProduct = await FindAsync(locality, category, product, variantSuffix);
            if (catalogProduct is null)
            {
                unpriced.Add(new UnpricedResource(resource.Address, $"the catalog lists no {category} '{product}{(variantSuffix is null ? "" : $" {variantSuffix}")}' in {locality.Key}"));
                return;
            }

            var monthlyUnitPrice = hourly ? catalogProduct.Price * HoursPerMonth : catalogProduct.Price;
            lines.Add(new CostLine(resource.Address, description, quantity * monthlyUnitPrice / catalogProduct.UnitSize));
        }
    }

    private async Task<CatalogProduct?> FindAsync(CatalogLocality locality, string category, string product, string? variantSuffix)
    {
        var products = await catalogProducts(locality);

        // A running resource may be on a product the catalog has retired, so a retired line is the last resort.
        return products
            .Where(candidate => candidate.Category.Equals(category, StringComparison.OrdinalIgnoreCase) && candidate.Product.Equals(product, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => variantSuffix is null || candidate.Variant.EndsWith(variantSuffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.Status == "retired")
            .FirstOrDefault();
    }

    // Kapsule reports node types the way its API spells them (play2_nano); the catalog names the offer PLAY2-NANO.
    private static string InstanceType(string nodeType)
    {
        return nodeType.Replace('_', '-').ToUpperInvariant();
    }

    // Kapsule's mutualized control plane is free; a dedicated one is named after its size (kapsule-dedicated-4).
    private static string ControlPlaneProduct(string clusterType)
    {
        const string dedicatedPrefix = "kapsule-dedicated-";
        return clusterType.StartsWith(dedicatedPrefix, StringComparison.OrdinalIgnoreCase)
            ? $"Kapsule Dedicated-{clusterType[dedicatedPrefix.Length..]}"
            : "Kapsule Mutualized";
    }

    private static string String(TerraformResource resource, string attribute)
    {
        return resource.Values.TryGetProperty(attribute, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    }

    private static decimal Number(TerraformResource resource, string attribute)
    {
        return resource.Values.TryGetProperty(attribute, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : 0m;
    }
}

/// <summary>Reads the managed resources from <c>terraform show -json</c>, for a state or a saved plan alike.</summary>
public static class TerraformResources
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <param name="terraformJson">
    ///     The output of <c>terraform show -json</c> (state) or
    ///     <c>terraform show -json &lt;plan&gt;</c>.
    /// </param>
    public static TerraformResource[] Read(string terraformJson)
    {
        using var document = JsonDocument.Parse(terraformJson);

        // A state holds its resources under "values"; a plan holds the resources as they will be after the apply
        // under "planned_values". Both nest child modules the same way.
        var root = document.RootElement.TryGetProperty("planned_values", out var planned) ? planned : document.RootElement.TryGetProperty("values", out var values) ? values : default;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("root_module", out var rootModule)) return [];

        var resources = new List<TerraformResource>();
        Collect(rootModule);
        return resources.ToArray();

        void Collect(JsonElement module)
        {
            if (module.TryGetProperty("resources", out var moduleResources))
            {
                foreach (var resource in moduleResources.EnumerateArray().Where(resource => resource.GetProperty("mode").GetString() == "managed"))
                {
                    // Cloned so the resources outlive the document; a planned resource's unknown attributes are absent.
                    var attributes = resource.TryGetProperty("values", out var resourceValues) ? resourceValues.Clone() : EmptyObject;
                    resources.Add(new TerraformResource(resource.GetProperty("address").GetString()!, resource.GetProperty("type").GetString()!, attributes));
                }
            }

            if (!module.TryGetProperty("child_modules", out var childModules)) return;

            foreach (var childModule in childModules.EnumerateArray())
            {
                Collect(childModule);
            }
        }
    }
}
