namespace SharedKernel.Integrations.BlobStorage;

/// <summary>
///     Connection details for an S3-compatible object store (Scaleway Object Storage in production,
///     MinIO in local development), expressed as a single connection string so it flows through the
///     existing `ConnectionStrings:blob-storage` configuration key and deploy-time secret inputs.
///     Format: `ServiceUrl=https://s3.fr-par.scw.cloud;AccessKey=...;SecretKey=...;Region=fr-par;ForcePathStyle=false`.
/// </summary>
/// <remarks>
///     Scaleway's bucket namespace is shared across all customers of a region (unlike its projects),
///     so deployed environments prefix every bucket with an environment-unique string
///     (`BucketPrefix=pp-stage-`). The applications keep using logical bucket names ("avatars",
///     "data-protection") and this prefix turns them into the actual, TF-owned bucket names. Local
///     development (MinIO) has no such constraint and omits the entry.
/// </remarks>
public sealed record S3BlobStorageOptions(string ServiceUrl, string AccessKey, string SecretKey, string Region, bool ForcePathStyle, string BucketPrefix = "")
{
    public static S3BlobStorageOptions Parse(string connectionString)
    {
        var parts = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => part[0], part => part.Length > 1 ? part[1] : string.Empty, StringComparer.OrdinalIgnoreCase);

        if (!parts.TryGetValue("ServiceUrl", out var serviceUrl) || string.IsNullOrWhiteSpace(serviceUrl))
        {
            throw new InvalidOperationException("The blob-storage connection string requires a ServiceUrl entry.");
        }

        if (!parts.TryGetValue("AccessKey", out var accessKey) || string.IsNullOrWhiteSpace(accessKey))
        {
            throw new InvalidOperationException("The blob-storage connection string requires an AccessKey entry.");
        }

        if (!parts.TryGetValue("SecretKey", out var secretKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException("The blob-storage connection string requires a SecretKey entry.");
        }

        // The region is derived from the service URL host (e.g. s3.fr-par.scw.cloud -> fr-par) so
        // Scaleway endpoints work without repeating it; hosts without a region label (e.g. the
        // MinIO localhost endpoint) must state it explicitly.
        var region = parts.GetValueOrDefault("Region");
        if (string.IsNullOrEmpty(region))
        {
            var hostLabels = new Uri(serviceUrl).Host.Split('.');
            region = hostLabels.Length > 1 ? hostLabels[1] : "";
        }

        if (string.IsNullOrEmpty(region))
        {
            throw new InvalidOperationException(
                "The blob-storage connection string requires a Region entry when the ServiceUrl host does not contain one (e.g. localhost)."
            );
        }

        var forcePathStyle = bool.TryParse(parts.GetValueOrDefault("ForcePathStyle"), out var parsed) && parsed;
        var bucketPrefix = parts.GetValueOrDefault("BucketPrefix") ?? "";

        return new S3BlobStorageOptions(serviceUrl, accessKey, secretKey, region, forcePathStyle, bucketPrefix);
    }

    /// <summary>
    ///     Maps a logical bucket name to the physical bucket name: the environment prefix (when
    ///     configured) followed by the logical name. Every S3 operation goes through this.
    /// </summary>
    public string ResolveBucketName(string bucketName)
    {
        return $"{BucketPrefix}{bucketName}";
    }
}
