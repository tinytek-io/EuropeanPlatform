using Aspire.Hosting.EuropeanPlatform.Deployment;
using Aspire.Hosting.EuropeanPlatform.Kubernetes;
using Aspire.Hosting.EuropeanPlatform.Profiles;
using Aspire.Hosting.EuropeanPlatform.SelfContainedSystems;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.EuropeanPlatform;

/// <summary>
///     The platform the self-contained systems run on, in both of Aspire's modes. Locally: PostgreSQL, an S3-compatible
///     object store and a mail catcher as containers. Published: a Scaleway Kapsule environment with the private
///     Container Registry, the cluster preparation and registry prune steps, the chart patches, managed databases and
///     Object Storage as deploy-time connection strings, and a Secret Manager identity per workload.
/// </summary>
public sealed class EuropeanPlatform
{
    // In-cluster traffic is TLS on this port end to end (SharedKernel's AddInternalTls); the chart patches convert the
    // generated plain-HTTP contract to it.
    private const int InternalTlsPort = 8443;

    private static readonly string[] CockpitSignals = ["Metrics", "Logs", "Traces"];

    private readonly List<string> _internalTlsWorkloads = [];
    private readonly IResourceBuilder<PostgresServerResource>? _postgres;
    private readonly EnvironmentProfiles? _profiles;
    private readonly List<string> _publicRouteWorkloads = [];
    private readonly Dictionary<string, WorkloadSizing> _workloadSizing = [];

    internal EuropeanPlatform(IDistributedApplicationBuilder builder, EuropeanPlatformOptions options)
    {
        Builder = builder;

        if (IsPublishMode)
        {
            Deployment = ScalewayDeploymentSettings.Read(builder.Configuration);
            _profiles = EnvironmentProfiles.Load(EnvironmentProfiles.DefaultPath(builder.AppHostDirectory));
            Kubernetes = AddKapsuleEnvironment(Deployment);
            BlobStorage = builder.AddConnectionString("blob-storage");
            return;
        }

        _postgres = AddLocalPostgres(options);
        BlobStorage = AddLocalObjectStorage(options);
        AddLocalMailServer(options);
    }

    public IDistributedApplicationBuilder Builder { get; }

    public bool IsPublishMode => Builder.ExecutionContext.IsPublishMode;

    /// <summary>The Terraform-owned values of the environment being deployed; null locally.</summary>
    public ScalewayDeploymentSettings? Deployment { get; }

    /// <summary>The Kapsule environment the chart deploys to; null locally.</summary>
    public IResourceBuilder<KubernetesEnvironmentResource>? Kubernetes { get; }

    /// <summary>
    ///     S3-compatible object storage: Scaleway Object Storage when deployed, SeaweedFS locally, one connection string
    ///     shape.
    /// </summary>
    public IResourceBuilder<IResourceWithConnectionString> BlobStorage { get; }

    /// <summary>
    ///     Adds a self-contained system: <c>&lt;name&gt;-database</c>, <c>-workers</c> (waiting for the database), <c>-api</c>
    ///     (waiting for the workers) and, deployed, <c>-migrator</c>. Deployed, the API serves internal TLS with health
    ///     probes, and every workload gets its memory and its Secret Manager identity.
    /// </summary>
    /// <param name="name">
    ///     The system's name, e.g. <c>back-office</c>; the database is named with underscores (
    ///     <c>back_office</c>).
    /// </param>
    /// <param name="ports">The development ports of the API and the workers.</param>
    /// <param name="options">How the system differs from the platform's defaults.</param>
    public SelfContainedSystem AddSelfContainedSystem<TApi, TWorkers, TMigrator>(string name, SelfContainedSystemPorts ports, SelfContainedSystemOptions? options = null)
        where TApi : IProjectMetadata, new()
        where TWorkers : IProjectMetadata, new()
        where TMigrator : IProjectMetadata, new()
    {
        options ??= new SelfContainedSystemOptions();
        var database = AddDatabase($"{name}-database", name.Replace('-', '_'));

        var workers = Builder.AddProject<TWorkers>($"{name}-workers")
            .WithReference(database)
            .WaitFor(database);

        var api = Builder.AddProject<TApi>($"{name}-api")
            .WithReference(database)
            .WaitFor(workers);

        if (options.UsesBlobStorage)
        {
            workers.WithReference(BlobStorage);
            api.WithReference(BlobStorage);
        }

        if (!IsPublishMode)
        {
            // A development-only listener: AddDevelopmentPort binds it in Development only, so in containers the image
            // default (8080) applies and the workers need no inbound listener at all.
            workers.WithEnvironment("KESTREL_PORT", ports.Workers.ToString());
            api.WithEnvironment("KESTREL_PORT", ports.Api.ToString());
            return new SelfContainedSystem(name, false, options.UsesBlobStorage, database, workers, api, null);
        }

        // Only the app gateway is public: every public hostname routes to it, and it reaches the APIs in the cluster.
        api.WithHttpsEndpoint(InternalTlsPort, InternalTlsPort, "https")
            .WithKubernetesProbes("/internal-api/ready", "/internal-api/live");

        if (options.OwnsDataProtectionKeyRing)
        {
            // Single-writer keyring: this API generates and rotates the keys, every other API reads the shared ring.
            api.WithEnvironment("DATA_PROTECTION_KEY_RING_OWNER", "true");
        }

        Size($"{name}-api", api, WorkloadRole.Api, options.ApiClass);
        Size($"{name}-workers", workers, WorkloadRole.Workers, options.WorkersClass);
        _internalTlsWorkloads.Add($"{name}-api");

        // A run-once console app applying the schema and data migrations with the structure-owner role; the chart
        // patch turns its generated Deployment into a pre-upgrade Job.
        var migrator = Builder.AddProject<TMigrator>($"{name}-migrator");
        Size($"{name}-migrator", migrator, WorkloadRole.Migrator, WorkloadClass.Standard);

        AddDeploymentIdentity($"{name}-workers", workers);
        AddDeploymentIdentity($"{name}-api", api);
        AddDeploymentIdentity($"{name}-migrator", migrator);

        return new SelfContainedSystem(name, true, options.UsesBlobStorage, database, workers, api, migrator);
    }

    /// <summary>
    ///     Adds the YARP app gateway in front of the systems, and deployed, the public Gateway that routes every public
    ///     hostname to it with a Let's Encrypt certificate. It starts once every system's API is up. Deployed, it reaches
    ///     each system's API through <c>&lt;SYSTEM&gt;_API_URL</c> and proxies the blob-storing systems' avatars and logos
    ///     from Object Storage.
    /// </summary>
    /// <param name="port">The development port the gateway serves HTTPS on.</param>
    /// <param name="systems">The systems the gateway routes to.</param>
    /// <param name="publicHostnames">The public hostnames the deployed Gateway serves; all of them route to this gateway.</param>
    public IResourceBuilder<ProjectResource> AddAppGateway<TGateway>(int port, SelfContainedSystem[] systems, string[] publicHostnames)
        where TGateway : IProjectMetadata, new()
    {
        var gateway = Builder.AddProject<TGateway>("app-gateway");
        foreach (var system in systems)
        {
            gateway.WithReference(system.Api).WaitFor(system.Api);
        }

        if (!IsPublishMode)
        {
            gateway.WithEnvironment("ASPNETCORE_URLS", "https://localhost:" + port);
            return gateway;
        }

        // The Cilium Gateway routes the public hostnames here. The storage URLs proxy avatars and logos from Object
        // Storage, and the bucket prefix maps those paths to the physical bucket names (Scaleway's bucket namespace is
        // shared by every customer of a region, so deployed buckets carry an environment prefix). The gateway serves no
        // health endpoints, so the probes hit its Scalar OpenAPI page.
        gateway.WithHttpsEndpoint(InternalTlsPort, InternalTlsPort, "https").WithExternalHttpEndpoints();
        foreach (var system in systems)
        {
            gateway.WithEnvironment($"{system.EnvironmentPrefix}_API_URL", system.Api.GetEndpoint("https"));
        }

        foreach (var system in systems.Where(system => system.UsesBlobStorage))
        {
            gateway.WithEnvironment($"{system.EnvironmentPrefix}_STORAGE_URL", Deployment!.StorageEndpoint);
        }

        gateway
            .WithEnvironment("BLOB_STORAGE_BUCKET_PREFIX", Deployment!.StorageBucketPrefix)
            .WithKubernetesProbes("/openapi", "/openapi");
        Size("app-gateway", gateway, WorkloadRole.Gateway, WorkloadClass.Standard);
        _internalTlsWorkloads.Add("app-gateway");
        AddDeploymentIdentity("app-gateway", gateway);
        AddPublicGateway(gateway, publicHostnames);

        return gateway;
    }

    // A deployed workload's sizing comes from the profile its environment type selects, for its role and class: the
    // memory applies to the generated workload here, and the chart patches apply the replicas (and a migrator's memory,
    // which lives in its Job).
    private void Size(string name, IResourceBuilder<ProjectResource> workload, WorkloadRole role, WorkloadClass workloadClass)
    {
        var sizing = _profiles!.For(role, workloadClass, Deployment!.EnvironmentType);
        _workloadSizing[name] = sizing;
        if (role != WorkloadRole.Migrator)
        {
            workload.WithKubernetesMemory(sizing.MemoryRequest, sizing.MemoryLimit);
        }
    }

    private IResourceBuilder<IResourceWithConnectionString> AddDatabase(string resourceName, string databaseName)
    {
        // Deployed, the managed database's connection string is a deploy-time secret filled from Terraform's outputs.
        return _postgres?.AddDatabase(resourceName, databaseName) ?? Builder.AddConnectionString(resourceName);
    }

    private IResourceBuilder<KubernetesEnvironmentResource> AddKapsuleEnvironment(ScalewayDeploymentSettings deployment)
    {
        // frontend-build is a build-only container: Aspire wants its output composed into a consuming image rather than
        // referenced as a service, and the AppHost excludes it from the manifest instead. Without this, publish aborts
        // on that validation.
        Builder.Pipeline.DisableBuildOnlyContainerValidation();

        var kubernetes = Builder.AddKubernetesEnvironment("kubernetes")
            .WithHelm(helm => helm
                .WithChartName("platformplatform")
                .WithChartDescription("EuropeanPlatform self-contained systems on Kubernetes")
            );

        // Images publish to the Container Registry the bootstrap Terraform module creates.
        Builder.AddContainerRegistry("scaleway-registry", deployment.RegistryEndpoint, deployment.RegistryRepository);

        // What the cluster needs before the Helm rollout, and the registry housekeeping after it.
        Builder.AddClusterPreparationSteps(deployment.RegistryEndpoint, Builder.Configuration["Registry:PullSecretKey"], deployment.Region);
        Builder.AddRegistryPruneStep(Builder.Configuration["Registry:NamespaceId"], deployment.ProjectId);

        // What the Aspire publisher cannot express yet (migration Jobs, internal TLS, network policies, the adopted Load
        // Balancer, rollout strategy, replicas, image pull secrets), patched into the generated chart before Helm runs.
        // The workload list is read when the action runs, after every system has been added.
        Builder.Pipeline.WithFinalAction("publish-kubernetes", context =>
            new HelmChartPatches(Builder, _internalTlsWorkloads.ToArray(), _publicRouteWorkloads.ToArray(), deployment.Region, _workloadSizing).PatchGeneratedChartAsync(context)
        );

        return kubernetes;
    }

    // Each deployed workload reads its secrets from Secret Manager with its own IAM key (a deploy-time parameter whose
    // policy allows only the workload's own path), and sends Transactional Email through the HTTP API with that key.
    // The chart carries no ASPNETCORE_/DOTNET_ENVIRONMENT, and SharedKernel reads the raw variable for
    // IsRunningInCloud, so both spellings are pinned. Cockpit's per-signal endpoints arrive as configuration from the
    // deploy; the token authenticating them is the workload's Secret Manager secret.
    private void AddDeploymentIdentity(string name, IResourceBuilder<IResourceWithEnvironment> workload)
    {
        var secretManagerKey = Builder.AddParameter($"{name}-secret-manager-key", true);
        workload
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
            .WithEnvironment("DOTNET_ENVIRONMENT", "Production")
            .WithEnvironment("SCW_SECRET_KEY", secretManagerKey)
            .WithEnvironment("SecretManager__Path", $"/{name}")
            .WithEnvironment("SCW_DEFAULT_PROJECT_ID", Deployment!.ProjectId)
            .WithEnvironment("SCW_DEFAULT_REGION", Deployment.Region)
            .WithEnvironment("SENDER_EMAIL_ADDRESS", Deployment.EmailSender);

        foreach (var signal in CockpitSignals)
        {
            var endpoint = Builder.Configuration[$"Cockpit:{signal}OtlpEndpoint"];
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                workload.WithEnvironment($"Cockpit__{signal}OtlpEndpoint", endpoint);
            }
        }
    }

    // One Cilium (Envoy) Gateway serving every public hostname, all terminating at the app gateway, which routes them on.
    // cert-manager is installed by the chart and owns the Let's Encrypt production issuer; ACME challenges route through
    // the Gateway (an HTTPRoute on /.well-known/acme-challenge).
    private void AddPublicGateway(IResourceBuilder<ProjectResource> gateway, string[] publicHostnames)
    {
        var kubernetes = Kubernetes!;
        var issuer = kubernetes.AddCertManager("cert-manager")
            .AddIssuer("letsencrypt")
            .WithLetsEncryptProduction(Deployment!.AcmeEmail)
            .WithHttp01Solver();

        var publicGateway = kubernetes.AddGateway("public-gateway").WithGatewayClass("cilium");
        foreach (var hostname in publicHostnames)
        {
            publicGateway.WithHostname(hostname);
        }

        publicGateway.WithTls(issuer);
        foreach (var hostname in publicHostnames)
        {
            publicGateway.WithRoute(hostname, "/", gateway.GetEndpoint("https"));
        }

        _publicRouteWorkloads.Add(gateway.Resource.Name);
    }

    private IResourceBuilder<PostgresServerResource> AddLocalPostgres(EuropeanPlatformOptions options)
    {
        return Builder.AddPostgres("postgres", password: options.PostgresPassword(), port: options.PostgresPort)
            .WithDataVolume($"{options.VolumeNamePrefix}-postgres-data")
            .WithLifetime(ContainerLifetime.Persistent)
            .WithArgs("-c", "wal_level=logical");
    }

    private IResourceBuilder<IResourceWithConnectionString> AddLocalObjectStorage(EuropeanPlatformOptions options)
    {
        // Fixed credentials, safe for a local-only emulator: SeaweedFS's default IAM accepts any, so they only keep the
        // connection string's shape identical to the deployed one. Buckets are created lazily by the S3 client on first
        // use, and the S3 gateway runs on the container's 8333 so the default `weed server` ports stay free.
        const string storageAccessKey = "seaweed-admin";
        const string storageSecretKey = "seaweed-admin";

        Builder
            .AddContainer("seaweedfs", "chrislusf/seaweedfs:latest")
            .WithHttpEndpoint(options.ObjectStoragePort, 8333)
            .WithArgs("server", "-dir=/data", "-s3", "-s3.port=8333")
            .WithVolume($"{options.VolumeNamePrefix}-seaweed-data", "/data")
            .WithLifetime(ContainerLifetime.Persistent)
            .WithUrlForEndpoint("http", url => url.DisplayText = "Object storage (SeaweedFS)");

        // AddConnectionString reads its value from ConnectionStrings:{name} (its optional second argument is the
        // environment variable name), so set this worktree's object storage port in configuration first.
        Builder.Configuration["ConnectionStrings:blob-storage"] = $"ServiceUrl=http://localhost:{options.ObjectStoragePort};AccessKey={storageAccessKey};SecretKey={storageSecretKey};ForcePathStyle=true;Region=fr-par";
        return Builder.AddConnectionString("blob-storage");
    }

    // Deployed environments send email through Transactional Email, so the mail catcher is local only.
    private void AddLocalMailServer(EuropeanPlatformOptions options)
    {
        Builder
            .AddContainer("mail-server", "axllent/mailpit")
            .WithHttpEndpoint(options.MailHttpPort, 8025)
            .WithEndpoint(options.MailSmtpPort, 1025)
            .WithLifetime(ContainerLifetime.Persistent)
            .WithUrlForEndpoint("http", url => url.DisplayText = "Read mail here");
    }
}

public static class EuropeanPlatformExtensions
{
    /// <summary>Adds the platform the self-contained systems run on, locally or deployed to Scaleway, by the builder's mode.</summary>
    public static EuropeanPlatform AddEuropeanPlatform(this IDistributedApplicationBuilder builder, EuropeanPlatformOptions options)
    {
        return new EuropeanPlatform(builder, options);
    }
}
