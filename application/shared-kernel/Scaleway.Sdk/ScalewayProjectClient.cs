using ScwContainers = Scaleway.Sdk.ServerlessContainer.V1;
using ScwJobs = Scaleway.Sdk.Jobs.V1Alpha2;
using ScwK8s = Scaleway.Sdk.K8s.V1;
using ScwRdb = Scaleway.Sdk.Rdb.V1;
using ScwRedis = Scaleway.Sdk.Redis.V1;
using ScwRegistry = Scaleway.Sdk.Registry.V1;
using ScwSecret = Scaleway.Sdk.Secret.V1Beta1;
using ScwTem = Scaleway.Sdk.Tem.V1Alpha1;
using ScwVpc = Scaleway.Sdk.Vpc.V2;

namespace Scaleway.Sdk;

/// <summary>
///     A <see cref="ScalewayClient" /> narrowed to one project: the resources that live inside a project, plus the
///     project ID to stamp on them. Reached only through <see cref="ScalewayClient.WithProject" />, so these
///     operations are unreachable without a project — the requirement is carried by the type rather than checked
///     at runtime.
/// </summary>
public sealed class ScalewayProjectClient
{
    internal ScalewayProjectClient(ScalewayClient account, ProjectId projectId)
    {
        Account = account;
        ProjectId = projectId;
        Transport = account.Transport;

        // Every request that declares project_id (or, on a ProjectIdentifier pair, either) is filled from this
        // scope by the generated methods — the caller never repeats it. The account's default region rides along
        // so region-scoped calls need no region argument either.
        var scope = new ScalewayRequestScope(account.OrganizationId.Value, projectId.Value, account.DefaultRegion.ToApiString());
        Containers = new ScwContainers.ServerlessContainerApi(Transport, scope);
        Jobs = new ScwJobs.JobsApi(Transport, scope);
        Kubernetes = new ScwK8s.K8sApi(Transport, scope);
        Databases = new ScwRdb.RdbApi(Transport, scope);
        Redis = new ScwRedis.RedisApi(Transport, scope);
        Registry = new ScwRegistry.RegistryApi(Transport, scope);
        Secrets = new ScwSecret.SecretApi(Transport, scope);
        Tem = new ScwTem.TemApi(Transport, scope);
        Vpc = new ScwVpc.VpcApi(Transport, scope);
    }

    /// <summary>
    ///     The account client this project scope was narrowed from — the way back to the organization-scoped
    ///     operations (IAM) without widening this client's own surface.
    /// </summary>
    public ScalewayClient Account { get; }

    /// <summary>The project stamped on every request that declares one.</summary>
    public ProjectId ProjectId { get; }

    /// <summary>The underlying transport, for the lower-level find/list paths that work on raw JSON.</summary>
    internal IScalewayApiClient Transport { get; }

    /// <summary>Serverless Containers — the namespaces, containers and custom domains the deploy provisions.</summary>
    public ScwContainers.ServerlessContainerApi Containers { get; }

    /// <summary>
    ///     Serverless Jobs — job definitions and the runs they produce. A run is created by starting a definition,
    ///     which this client cannot do: <c>StartJobDefinition</c> is a POST carrying a body and the action emitter
    ///     supports only body-less GETs and empty-body POSTs, so it is recorded as excluded at the generator's
    ///     <c>jobs</c> action entry rather than emitted. Stopping a run is here.
    /// </summary>
    public ScwJobs.JobsApi Jobs { get; }

    /// <summary>
    ///     Kapsule clusters. Node pools are created inline through <c>CreateClusterRequest.Pools</c> rather than
    ///     through a pool API — see the note on the <c>k8s</c> entry in the generator's resource allow-list.
    /// </summary>
    public ScwK8s.K8sApi Kubernetes { get; }

    /// <summary>Managed PostgreSQL and MySQL instances (Scaleway's <c>rdb</c>).</summary>
    public ScwRdb.RdbApi Databases { get; }

    /// <summary>Managed Redis clusters.</summary>
    public ScwRedis.RedisApi Redis { get; }

    /// <summary>
    ///     Container Registry namespaces. The registry's own data plane is the Docker HTTP API and is not part of this
    ///     SDK.
    /// </summary>
    public ScwRegistry.RegistryApi Registry { get; }

    /// <summary>Secret Manager. <see cref="ScalewaySecretClient" /> wraps this for the read/write paths the platform uses.</summary>
    public ScwSecret.SecretApi Secrets { get; }

    /// <summary>Transactional Email — sending domains and their DNS validation state.</summary>
    public ScwTem.TemApi Tem { get; }

    /// <summary>VPCs and the private networks inside them — one API family at Scaleway.</summary>
    public ScwVpc.VpcApi Vpc { get; }
}
