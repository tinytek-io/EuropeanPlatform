using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Scaleway.Sdk;

/// <summary>
///     Bucket management against Scaleway Object Storage's S3-compatible API — the only management surface the
///     product has (no native Scaleway API family). Path-style addressing (<c>{endpoint}/{bucket}</c>), SigV4
///     signed with the SCW key pair. <c>SCW_S3_URL</c> overrides the endpoint so tests run against the mock.
/// </summary>
/// <summary>The bucket-management seam, so the deterministic simulation can stand in for the HTTP client.</summary>
internal interface IScalewayS3Client
{
    Task CreateBucketAsync(string bucketName, CancellationToken cancellationToken);

    Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken);

    Task PutBucketPolicyAsync(string bucketName, string policyJson, CancellationToken cancellationToken);

    Task DeleteBucketPolicyAsync(string bucketName, CancellationToken cancellationToken);

    Task DeleteBucketAsync(string bucketName, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListBucketNamesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListObjectKeysAsync(string bucketName, CancellationToken cancellationToken);

    Task DeleteObjectAsync(string bucketName, string objectKey, CancellationToken cancellationToken);
}

internal sealed class ScalewayS3Client(ScalewayCredentialConfig credentials, TimeProvider timeProvider, string? endpoint = null) : IDisposable, IScalewayS3Client
{
    private readonly HttpClient _httpClient = ScalewayUserAgent.ApplyTo(new HttpClient { BaseAddress = new Uri(endpoint ?? ResolveS3Endpoint(credentials.DefaultRegion)) });

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    /// <summary>Creates the bucket; throws <see cref="ScalewayResourceAlreadyExistsException" /> when the name is taken.</summary>
    public async Task CreateBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        using var request = CreateSignedRequest(HttpMethod.Put, $"/{bucketName}", AwsSignatureV4.EmptyPayloadHash);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureS3SuccessAsync(response, $"create bucket '{bucketName}'", cancellationToken);
    }

    public async Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken)
    {
        using var request = CreateSignedRequest(HttpMethod.Head, $"/{bucketName}", AwsSignatureV4.EmptyPayloadHash);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            // 403 is S3's answer for a bucket that exists but is owned by someone else — for "can we use this
            // name" purposes it exists; the create will fail loud with the ownership story.
            return response.StatusCode == HttpStatusCode.Forbidden;
        }

        await EnsureS3SuccessAsync(response, $"check bucket '{bucketName}'", cancellationToken);
        return true;
    }

    /// <summary>Puts the bucket policy (one per bucket; a put overwrites, so re-applying is idempotent).</summary>
    public async Task PutBucketPolicyAsync(string bucketName, string policyJson, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(policyJson);
        using var request = CreateSignedRequest(HttpMethod.Put, $"/{bucketName}?policy", AwsSignatureV4.HashPayload(payload));
        request.Content = new ByteArrayContent(payload);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureS3SuccessAsync(response, $"put bucket policy on '{bucketName}'", cancellationToken);
    }

    /// <summary>
    ///     Deletes the bucket policy. The policy is allowlist-only and locks out every principal it does not name —
    ///     including the deployer — but Scaleway's platform rule is that the Organization owner always retains
    ///     policy management rights, making this the escape hatch before a <see cref="DeleteBucketAsync" />.
    /// </summary>
    public async Task DeleteBucketPolicyAsync(string bucketName, CancellationToken cancellationToken)
    {
        using var request = CreateSignedRequest(HttpMethod.Delete, $"/{bucketName}?policy", AwsSignatureV4.EmptyPayloadHash);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureS3SuccessAsync(response, $"delete bucket policy on '{bucketName}'", cancellationToken);
    }

    /// <summary>Deletes the bucket. S3 refuses to delete a non-empty bucket (<c>BucketNotEmpty</c>).</summary>
    public async Task DeleteBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        using var request = CreateSignedRequest(HttpMethod.Delete, $"/{bucketName}", AwsSignatureV4.EmptyPayloadHash);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureS3SuccessAsync(response, $"delete bucket '{bucketName}'", cancellationToken);
    }

    /// <summary>The names of all buckets in the project — the orphan scan's view.</summary>
    public async Task<IReadOnlyList<string>> ListBucketNamesAsync(CancellationToken cancellationToken)
    {
        using var request = CreateSignedRequest(HttpMethod.Get, "/", AwsSignatureV4.EmptyPayloadHash);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureS3SuccessAsync(response, "list buckets", cancellationToken);

        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return xml.Descendants().Where(element => element.Name.LocalName == "Name").Select(element => element.Value).ToList();
    }

    /// <summary>
    ///     Every object key in the bucket (ListObjectsV2, following continuation tokens) — the purge scan for
    ///     <c>--delete-storage</c>.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListObjectKeysAsync(string bucketName, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        string? continuationToken = null;
        do
        {
            var query = $"/{bucketName}?list-type=2{(continuationToken is null ? "" : $"&continuation-token={Uri.EscapeDataString(continuationToken)}")}";
            using var request = CreateSignedRequest(HttpMethod.Get, query, AwsSignatureV4.EmptyPayloadHash);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            await EnsureS3SuccessAsync(response, $"list objects in '{bucketName}'", cancellationToken);

            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            keys.AddRange(xml.Descendants().Where(element => element.Name.LocalName == "Contents")
                    .Select(contents => contents.Elements().FirstOrDefault(element => element.Name.LocalName == "Key")?.Value)
                    .Where(key => key is not null)!
            );
            continuationToken = xml.Descendants().FirstOrDefault(element => element.Name.LocalName == "NextContinuationToken")?.Value;
        } while (continuationToken is not null);

        return keys;
    }

    public async Task DeleteObjectAsync(string bucketName, string objectKey, CancellationToken cancellationToken)
    {
        // S3 keys keep their slashes as path separators; every other character is encoded per segment.
        var encodedKey = string.Join('/', objectKey.Split('/').Select(Uri.EscapeDataString));
        using var request = CreateSignedRequest(HttpMethod.Delete, $"/{bucketName}/{encodedKey}", AwsSignatureV4.EmptyPayloadHash);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureS3SuccessAsync(response, $"delete object '{objectKey}' in '{bucketName}'", cancellationToken);
    }

    /// <summary>
    ///     The S3 endpoint: in production the regional <c>s3.{region}.scw.cloud</c> host — a different host from
    ///     the management API, speaking a different protocol (S3/SigV4 vs JSON/X-Auth-Token), so one URL cannot
    ///     serve both. In tests a mock serves both surfaces on one URL, so <c>SCW_API_URL</c> (the established
    ///     "point everything at the mock" switch) covers S3 too; <c>SCW_S3_URL</c> overrides only when the two
    ///     must genuinely differ.
    /// </summary>
    internal static string ResolveS3Endpoint(ScalewayRegion region)
    {
        return Environment.GetEnvironmentVariable("SCW_S3_URL")
               ?? Environment.GetEnvironmentVariable("SCW_API_URL")
               ?? ScalewayEndpoints.ObjectStorage(region);
    }

    private HttpRequestMessage CreateSignedRequest(HttpMethod method, string pathAndQuery, string payloadHash)
    {
        var request = new HttpRequestMessage(method, new Uri(_httpClient.BaseAddress!, pathAndQuery));
        AwsSignatureV4.Sign(request, payloadHash, credentials.AccessKey, credentials.SecretKey, credentials.DefaultRegion.ToApiString(), "s3", timeProvider.GetUtcNow());
        return request;
    }

    // S3 errors are XML, not Scaleway's JSON shape; map the cases the provisioning paths act on and surface
    // the rest with the S3 error code, mirroring how the JSON transport surfaces Scaleway's error type.
    private static async Task EnsureS3SuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var errorCode = TryReadS3ErrorCode(body);
        var message = $"Scaleway Object Storage rejected '{operation}' with {(int)response.StatusCode}{(errorCode is null ? "" : $" ({errorCode})")}.";

        // BucketNotEmpty is S3's other 409 — a refusal to delete, not a name collision. It must keep its own
        // error type so destroy's warn-and-keep path can catch it.
        if (errorCode is not "BucketNotEmpty" && (response.StatusCode == HttpStatusCode.Conflict || errorCode is "BucketAlreadyExists" or "BucketAlreadyOwnedByYou"))
        {
            throw new ScalewayResourceAlreadyExistsException(message, (int)response.StatusCode, errorCode);
        }

        if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            throw new ScalewayTransientException(message);
        }

        throw new ScalewayApiException(message, (int)response.StatusCode, errorCode);
    }

    private static string? TryReadS3ErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return XDocument.Parse(body).Descendants().FirstOrDefault(element => element.Name.LocalName == "Code")?.Value;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
