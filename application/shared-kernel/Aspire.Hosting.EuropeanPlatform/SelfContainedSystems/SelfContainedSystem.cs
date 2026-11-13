namespace Aspire.Hosting.EuropeanPlatform.SelfContainedSystems;

/// <summary>
///     One self-contained system: its own database, a workers host that migrates and serves background work, an API
///     that serves the system's routes and its SPA, and in deployed environments a migrator that runs as a
///     pre-upgrade Job. Resources are named <c>&lt;system&gt;-database</c>, <c>-workers</c>, <c>-api</c> and
///     <c>-migrator</c>; the deployment, the chart patches and the network policies rely on those names.
/// </summary>
public sealed class SelfContainedSystem
{
    private readonly bool _isPublishMode;

    internal SelfContainedSystem(
        string name,
        bool isPublishMode,
        bool usesBlobStorage,
        IResourceBuilder<IResourceWithConnectionString> database,
        IResourceBuilder<ProjectResource> workers,
        IResourceBuilder<ProjectResource> api,
        IResourceBuilder<ProjectResource>? migrator
    )
    {
        Name = name;
        _isPublishMode = isPublishMode;
        UsesBlobStorage = usesBlobStorage;
        Database = database;
        Workers = workers;
        Api = api;
        Migrator = migrator;
    }

    /// <summary>The system's name, which every resource name and the <c>&lt;SYSTEM&gt;_API_URL</c> variables derive from.</summary>
    public string Name { get; }

    public IResourceBuilder<IResourceWithConnectionString> Database { get; }

    public IResourceBuilder<ProjectResource> Workers { get; }

    public IResourceBuilder<ProjectResource> Api { get; }

    /// <summary>The migrator, in deployed environments only: locally the workers apply the migrations at startup.</summary>
    public IResourceBuilder<ProjectResource>? Migrator { get; }

    internal bool UsesBlobStorage { get; }

    /// <summary>The system's name as an environment variable prefix: <c>back-office</c> becomes <c>BACK_OFFICE</c>.</summary>
    internal string EnvironmentPrefix => Name.ToUpperInvariant().Replace('-', '_');

    /// <summary>
    ///     Lets this system's API call another system's internal API, starting only once that API is up. Deployed, the call
    ///     goes over in-cluster TLS through <c>&lt;OTHER&gt;_API_URL</c>; locally the API resolves the other system's
    ///     development port itself.
    /// </summary>
    public SelfContainedSystem UsesInternalApiOf(SelfContainedSystem other)
    {
        Api.WaitFor(other.Api);
        if (_isPublishMode)
        {
            Api.WithEnvironment($"{other.EnvironmentPrefix}_API_URL", other.Api.GetEndpoint("https"));
        }

        return this;
    }
}
