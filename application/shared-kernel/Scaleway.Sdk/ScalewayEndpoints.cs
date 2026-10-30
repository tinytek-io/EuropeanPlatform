namespace Scaleway.Sdk;

/// <summary>
///     The Scaleway service hostnames that are not the management API — Object Storage, Container Registry,
///     Cockpit and the domain Scaleway generates for a Serverless Container. These are *data*: values handed to
///     workloads as environment variables, to docker as an image tag, or predicted before a resource exists.
///     They are not called from here.
///     <para>
///         They live in the SDK anyway so the URL scheme is written down once. Scaleway revs these hosts
///         independently of the API version, and a scheme spelled out at six call sites drifts one site at a
///         time; spelled out here it drifts all at once, visibly.
///     </para>
/// </summary>
public static class ScalewayEndpoints
{
    /// <summary>The regional Object Storage (S3) endpoint, e.g. <c>https://s3.fr-par.scw.cloud</c>.</summary>
    public static string ObjectStorage(ScalewayRegion region)
    {
        return $"https://s3.{region.ToApiString()}.scw.cloud";
    }

    /// <summary>The Container Registry host without a namespace, e.g. <c>rg.fr-par.scw.cloud</c>.</summary>
    public static string ContainerRegistryHost(ScalewayRegion region)
    {
        return $"rg.{region.ToApiString()}.scw.cloud";
    }

    /// <summary>
    ///     A Cockpit OTLP push endpoint for one signal, e.g.
    ///     <c>https://{dataSourceId}.traces.cockpit.fr-par.scw.cloud/otlp/v1/traces</c>. The signal name appears
    ///     twice by design — it is both the host label and the path segment.
    /// </summary>
    public static string CockpitOtlp(string dataSourceId, ScalewayRegion region, ScalewayOtlpSignal signal)
    {
        var signalName = signal switch
        {
            ScalewayOtlpSignal.Traces => "traces",
            ScalewayOtlpSignal.Metrics => "metrics",
            ScalewayOtlpSignal.Logs => "logs",
            _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, "Unknown OTLP signal.")
        };

        return $"https://{dataSourceId}.{signalName}.cockpit.{region.ToApiString()}.scw.cloud/otlp/v1/{signalName}";
    }

    /// <summary>
    ///     The domain Scaleway generates for a Serverless Container:
    ///     <c>{namespaceName}{first-8-of-namespace-id}-{containerName}.functions.fnc.{region}.scw.cloud</c>
    ///     (observed live). Derivable before the container exists, which is what lets a deploy inject one
    ///     container's URL into another's environment in a single pass.
    /// </summary>
    public static string ServerlessContainerDomain(string namespaceName, string namespaceId, string containerName, string region)
    {
        var namespaceIdPrefix = namespaceId.Length >= 8 ? namespaceId[..8] : namespaceId;
        return $"{namespaceName}{namespaceIdPrefix}-{containerName}.functions.fnc.{region}.scw.cloud";
    }
}

/// <summary>The OpenTelemetry signal a Cockpit OTLP endpoint carries. Each has its own host, not one host with a path.</summary>
public enum ScalewayOtlpSignal
{
    /// <summary>Distributed traces — <c>traces</c>.</summary>
    Traces,

    /// <summary>Metrics — <c>metrics</c>.</summary>
    Metrics,

    /// <summary>Logs — <c>logs</c>.</summary>
    Logs
}
