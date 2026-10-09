using System.Collections.Concurrent;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace SharedKernel.Integrations.BlobStorage;

/// <summary>
///     S3-compatible implementation of <see cref="IBlobStorageClient"/>, used against Scaleway
///     Object Storage in production and MinIO in local development. Buckets are created lazily with
///     a public-read policy when requested, so avatars/logos can be served unsigned through AppGateway
///     exactly like the public containers they replace.
/// </summary>
public sealed class S3BlobStorageClient(
    IAmazonS3 s3Client,
    S3BlobStorageOptions options,
    TimeProvider timeProvider
) : IBlobStorageClient
{
    private readonly ConcurrentDictionary<string, byte> _ensuredBuckets = new(StringComparer.OrdinalIgnoreCase);

    public async Task UploadAsync(string bucketName, string blobName, string contentType, Stream stream, CancellationToken cancellationToken)
    {
        await CreateBucketIfNotExistsAsync(bucketName, publicRead: true, cancellationToken);

        var request = new PutObjectRequest
        {
            BucketName = options.ResolveBucketName(bucketName),
            Key = blobName,
            ContentType = contentType,
            InputStream = stream
        };

        await s3Client.PutObjectAsync(request, cancellationToken);
    }

    public async Task<string[]> ListBlobNamesAsync(string bucketName, string prefix, CancellationToken cancellationToken)
    {
        try
        {
            var response = await s3Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = options.ResolveBucketName(bucketName), Prefix = prefix }, cancellationToken);
            return response.S3Objects?.Select(s3Object => s3Object.Key).ToArray() ?? [];
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    public async Task<(Stream Stream, string ContentType)?> DownloadAsync(string bucketName, string blobName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await s3Client.GetObjectAsync(options.ResolveBucketName(bucketName), blobName, cancellationToken);
            return (response.ResponseStream, response.Metadata["Content-Type"]);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public string GetBlobUrl(string bucketName, string blobName)
    {
        var serviceUrl = options.ServiceUrl.TrimEnd('/');
        // Path-style: https://s3.fr-par.scw.cloud/{bucket}/{key} - virtual-host style prepends the
        // bucket as a subdomain, matching the addressing mode the client itself is configured with.
        var physicalBucket = options.ResolveBucketName(bucketName);
        return options.ForcePathStyle || serviceUrl.Contains("localhost")
            ? $"{serviceUrl}/{physicalBucket}/{blobName}"
            : $"{new Uri(serviceUrl).Scheme}://{physicalBucket}.{new Uri(serviceUrl).Host}/{blobName}";
    }

    public async Task<bool> DeleteIfExistsAsync(string bucketName, string blobName, CancellationToken cancellationToken)
    {
        try
        {
            await s3Client.DeleteObjectAsync(options.ResolveBucketName(bucketName), blobName, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public Uri GetPresignedUri(string bucketName, string blobName, TimeSpan expiresIn)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = options.ResolveBucketName(bucketName),
            Key = blobName,
            Verb = HttpVerb.GET,
            Expires = timeProvider.GetUtcNow().Add(expiresIn).DateTime
        };

        return new Uri(s3Client.GetPreSignedURL(request));
    }

    public async Task CreateBucketIfNotExistsAsync(string bucketName, bool publicRead, CancellationToken cancellationToken)
    {
        var physicalBucketName = options.ResolveBucketName(bucketName);
        if (_ensuredBuckets.ContainsKey(physicalBucketName))
        {
            return;
        }

        var bucketExists = true;
        try
        {
            await s3Client.HeadBucketAsync(new HeadBucketRequest { BucketName = physicalBucketName }, cancellationToken);
        }
        catch (AmazonS3Exception)
        {
            bucketExists = false;
        }

        if (!bucketExists)
        {
            await s3Client.PutBucketAsync(new PutBucketRequest { BucketName = physicalBucketName }, cancellationToken);

            if (publicRead)
            {
                await ApplyPublicReadPolicyAsync(physicalBucketName, cancellationToken);
            }
        }

        _ensuredBuckets.TryAdd(physicalBucketName, 0);
    }

    private async Task ApplyPublicReadPolicyAsync(string bucketName, CancellationToken cancellationToken)
    {
        var policy = new
        {
            Version = "2012-10-17",
            Statement = new[]
            {
                new
                {
                    Effect = "Allow",
                    Principal = "*",
                    Action = "s3:GetObject",
                    Resource = $"arn:scw:s3:*:*:{options.ResolveBucketName(bucketName)}/*"
                }
            }
        };

        await s3Client.PutBucketPolicyAsync(new PutBucketPolicyRequest
        {
            BucketName = bucketName,
            Policy = JsonSerializer.Serialize(policy)
        }, cancellationToken);
    }
}
