using YamlDotNet.Serialization;

namespace Scaleway.Sdk.Generator;

/// <summary>
///     The shared front door for turning a checked-in schema into a <see cref="ParsedApi" />: the slug→service and
///     service→resource-group mappings, plus the YAML→JSON step. Both the generator (which emits) and the drift
///     check (which compares) go through here, so they can never disagree on how a schema is parsed.
/// </summary>
public static class SchemaSource
{
    /// <summary>
    ///     Parses a schema's YAML into the typed-client model, or <c>null</c> when the service has no generated
    ///     client (nothing to compare or emit). JSON is valid YAML, so callers may pass JSON here too.
    /// </summary>
    public static ParsedApi? ParseClientApi(string slug, string yaml)
    {
        var service = ServiceForSlug(slug);
        var groups = ClientResourceGroups(service);
        return groups.Length == 0 ? null : OpenApiClientParser.Parse(service, YamlToJson(yaml), groups, ClientActionOperations(service));
    }

    // Scaleway publishes each schema as YAML; the parser walks it with System.Text.Json. YamlDotNet's
    // JSON-compatible serializer re-emits the parsed YAML graph as valid JSON, so no converter is checked in.
    public static string YamlToJson(string yaml)
    {
        var yamlObject = new DeserializerBuilder().Build().Deserialize<object>(yaml);
        return new SerializerBuilder().JsonCompatible().Build().Serialize(yamlObject);
    }

    // Maps a checked-in schema's doc slug to the ServiceCatalog service name (the short scaleway-sdk name the
    // emitter's category/prefix scheme is keyed on), e.g. serverless-containers -> container.
    public static string ServiceForSlug(string slug)
    {
        return slug switch
        {
            "serverless-containers" => "container",
            "managed-database-postgre-mysql" => "rdb",
            "managed-database-redis" => "redis",
            "secret-manager" => "secret",
            "serverless-functions" => "function",
            "serverless-jobs" => "jobs",
            "serverless-databases" => "serverless_sqldb",
            "transactional-email" => "tem",
            "kubernetes" => "k8s",
            "load-balancer-zoned" => "lb",
            "account-project" => "account",
            _ => slug // registry, vpc, iam already match
        };
    }

    // The deploy-path resources to generate a typed client for, per service, as (Singular, Plural) — Scaleway's
    // plurals are irregular (Policy → Policies, APIKey → APIKeys), so they're spelled out rather than derived.
    public static (string Singular, string Plural)[] ClientResourceGroups(string service)
    {
        return service switch
        {
            "container" => [("Container", "Containers"), ("Namespace", "Namespaces"), ("Domain", "Domains")],
            "rdb" => [("Instance", "Instances")],
            "redis" => [("Cluster", "Clusters")],
            "vpc" => [("PrivateNetwork", "PrivateNetworks"), ("VPC", "VPCs")],
            // Image and Tag serve the deploy's registry prune: list a namespace's images, list each image's tags,
            // delete the stale ones.
            "registry" => [("Namespace", "Namespaces"), ("Image", "Images"), ("Tag", "Tags")],
            "secret" => [("Secret", "Secrets"), ("SecretVersion", "SecretVersions")],
            "iam" => [("Application", "Applications"), ("Policy", "Policies"), ("APIKey", "APIKeys")],
            "tem" => [("Domain", "Domains"), ("Email", "Emails")],
            // Pool is absent by scope, not by capability: the emitter handles its split paths (listed under the
            // cluster, addressed at /pools/{pool_id}), so adding it here generates correctly. It joins when the
            // remaining Kubernetes resource groups do. CreateCluster takes its pools inline, which is all the
            // bootstrap needs today.
            // Version is generated so a bootstrap can ask Scaleway which Kubernetes versions exist rather than
            // carry a constant that silently goes stale. Its paths are symmetric (/versions, /versions/{name}).
            "k8s" => [("Cluster", "Clusters"), ("Version", "Versions")],
            // Serverless Jobs. JobRun has no Create/Update/Delete upstream and is not meant to: a run is created
            // by starting a definition, so the group emitting only List and Get is the schema, not defect (d).
            // Secret and Trigger are top-level collections in v1alpha2 (Secret was nested under a job definition in
            // v1alpha1) and are absent by scope: only job-definitions and job-runs are generated. Worth knowing before
            // admitting Secret: its create operation is 'CreateSecrets', a bulk create with no singular sibling, so
            // the CRUD matcher leaves the group without a Create rather than failing — visible in the manifest, but
            // it is a decision to take rather than to discover.
            "jobs" => [("JobDefinition", "JobDefinitions"), ("JobRun", "JobRuns")],
            // The zoned Load Balancer API. Frontends are listed under their Load Balancer and ACLs under their
            // frontend (split paths, like Kubernetes pools); the back-office security checks read all three.
            "lb" => [("Lb", "Lbs"), ("Frontend", "Frontends"), ("Acl", "Acls")],
            // Account v3 projects: resolving an environment's project by name and its organization by id.
            "account" => [("Project", "Projects")],
            _ => []
        };
    }

    /// <summary>
    ///     Non-CRUD action operations to emit, by exact <c>operationId</c>. The path shape carries no marker to
    ///     discover these, so each is an explicit promise — the parser fails generation if one is missing, or is not
    ///     a body-less GET or empty-body POST. An action's query parameters are emitted as a typed request record,
    ///     the same shape a List gets, so an operation that identifies its target through the query string is
    ///     admissible on the same terms as any other.
    /// </summary>
    public static string[] ClientActionOperations(string service)
    {
        return service switch
        {
            // AccessSecretVersionByPath is absent by scope, not by capability: its three required query parameters
            // (project_id, secret_name, secret_path) are emitted like any other, so adding it here generates
            // correctly. It joins when the remaining Secret Manager resource groups do. ScalewaySecretClient keeps
            // its find-then-access lookup either way — see the note on ScalewaySecretClient.FindByNameAsync.
            "secret" => ["AccessSecretVersion"],
            "rdb" => ["GetInstanceCertificate"],
            "tem" => ["GetDomainLastStatus", "CheckDomain"],
            "k8s" => ["GetClusterKubeConfig"],
            // StartJobDefinition is absent by capability, not by scope: it is a POST carrying a real body
            // (startup_command, args, environment_variables, replicas), and the action emitter supports only
            // body-less GETs and empty-body POSTs — declaring it fails generation. Admitting it means teaching the
            // emitter and the transport a body-carrying action first. StopJobRun is the empty-body POST shape
            // CheckDomain already proves, so it generates.
            "jobs" => ["StopJobRun"],
            _ => []
        };
    }

    /// <summary>
    ///     Where a service's generated <c>*Api</c> hangs off the hand-written entry points, as it appears in the
    ///     manifest. Declared here rather than read from the SDK because the generator does not reference the package
    ///     it generates into — and because a manifest that is incomplete until a test fills it in is not an artifact
    ///     the README can be rendered from. Nothing checks the declaration against the actual property, so a
    ///     service whose entry point moves must be updated here too.
    ///     <para>
    ///         Throwing is deliberate: a service that generates a client nobody can reach is the breadth failure mode
    ///         this guards against, so it fails generation rather than emitting an unreachable client.
    ///     </para>
    /// </summary>
    public static (string Client, string Property) EntryPointFor(string service)
    {
        return service switch
        {
            // Organization-scoped: IAM identities belong to the organization, not to a project.
            "iam" => ("ScalewayClient", "Iam"),
            // Organization-scoped too: projects belong to the organization. Named for what it holds, since a project
            // client's Account property is already the way back to this client.
            "account" => ("ScalewayClient", "Projects"),
            // Project-scoped. The property is the product's common name rather than the schema slug.
            "container" => ("ScalewayProjectClient", "Containers"),
            "jobs" => ("ScalewayProjectClient", "Jobs"),
            "k8s" => ("ScalewayProjectClient", "Kubernetes"),
            "lb" => ("ScalewayProjectClient", "LoadBalancers"),
            "rdb" => ("ScalewayProjectClient", "Databases"),
            "redis" => ("ScalewayProjectClient", "Redis"),
            "registry" => ("ScalewayProjectClient", "Registry"),
            "secret" => ("ScalewayProjectClient", "Secrets"),
            "tem" => ("ScalewayProjectClient", "Tem"),
            "vpc" => ("ScalewayProjectClient", "Vpc"),
            _ => throw new InvalidOperationException(
                $"Service '{service}' generates a typed client but declares no entry-point property. Add it to " +
                "SchemaSource.EntryPointFor and hang the generated *Api off ScalewayClient or ScalewayProjectClient, " +
                "or the client is generated where no caller can reach it."
            )
        };
    }

    /// <summary>
    ///     How thoroughly each generated resource group has actually been checked, rendered into the manifest.
    ///     <para>
    ///         Deliberately conservative, and deliberately not inferred from "the deploy calls it": a tier is a claim
    ///         about verification, and the failure mode this whole manifest exists to stop is a coverage claim that
    ///         drifts upward on its own. <see cref="ValidationTier.RoundTripValidated" /> is declared only where the
    ///         group appears in <c>DeploySimulationTests.Simulation_ExercisesEveryDeployedResourceUnderFaults</c>'s
    ///         asserted resource list — a list a test already enforces, so the claim has an owner that fails loudly.
    ///     </para>
    ///     <para>
    ///         Everything else is <see cref="ValidationTier.StructurallyConformant" />, including groups the deploy
    ///         does touch (TEM's Domain is created by a real deploy). Understating is the safe direction; generated
    ///         live probes would be what raise a group to <see cref="ValidationTier.LiveReadOnly" />, and no
    ///         group is claimed at that tier until those exist.
    ///     </para>
    /// </summary>
    public static ValidationTier ValidationTierFor(string service, string resourceGroup)
    {
        return (service, resourceGroup) switch
        {
            ("container", "Container") or ("container", "Namespace") => ValidationTier.RoundTripValidated,
            ("registry", "Namespace") => ValidationTier.RoundTripValidated,
            ("rdb", "Instance") => ValidationTier.RoundTripValidated,
            ("redis", "Cluster") => ValidationTier.RoundTripValidated,
            ("vpc", "VPC") or ("vpc", "PrivateNetwork") => ValidationTier.RoundTripValidated,
            ("secret", "Secret") or ("secret", "SecretVersion") => ValidationTier.RoundTripValidated,
            ("iam", "Application") or ("iam", "Policy") or ("iam", "APIKey") => ValidationTier.RoundTripValidated,
            _ => ValidationTier.StructurallyConformant
        };
    }
}
