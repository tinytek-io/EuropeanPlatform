using Yarp.ReverseProxy.Configuration;

namespace AppGateway.Filters;

/// <summary>
///     Rewrites the avatar/logo proxy routes to the physical (prefixed) S3 bucket paths. The
///     applications address buckets by logical name, and deployed environments prefix every bucket
///     (Scaleway's bucket namespace is shared across all customers of a region, unlike its projects).
///     Local development (SeaweedFS) sets no prefix and the routes forward unchanged.
/// </summary>
public class StorageBucketPrefixConfigFilter : IProxyConfigFilter
{
    private const string BucketPrefixEnvironmentVariable = "BLOB_STORAGE_BUCKET_PREFIX";

    // Route id -> logical bucket name. Both proxy to path-style S3 addresses, where the first
    // path segment is the bucket name — hence the PathPattern rewrite below.
    private static readonly Dictionary<string, string> StorageRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["avatars"] = "avatars",
        ["logos"] = "logos"
    };

    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel)
    {
        return new ValueTask<ClusterConfig>(cluster);
    }

    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel)
    {
        if (!StorageRoutes.TryGetValue(route.RouteId, out var bucketName))
        {
            return new ValueTask<RouteConfig>(route);
        }

        var bucketPrefix = Environment.GetEnvironmentVariable(BucketPrefixEnvironmentVariable);
        if (string.IsNullOrEmpty(bucketPrefix))
        {
            return new ValueTask<RouteConfig>(route);
        }

        // The browser-facing path stays /avatars/...; the origin path becomes /<prefix>avatars/...
        // so S3's path-style addressing selects the physical bucket.
        var transforms = new List<IReadOnlyDictionary<string, string>>();
        if (route.Transforms is not null)
        {
            transforms.AddRange(route.Transforms);
        }

        transforms.Add(new Dictionary<string, string> { ["PathPattern"] = $"/{bucketPrefix}{bucketName}/{{**catch-all}}" });

        return new ValueTask<RouteConfig>(route with { Transforms = transforms });
    }
}
