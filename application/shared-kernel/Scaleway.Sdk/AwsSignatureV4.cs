using System.Security.Cryptography;
using System.Text;

namespace Scaleway.Sdk;

/// <summary>
///     AWS Signature Version 4 request signing, hand-written because Scaleway Object Storage is reachable only
///     through the S3-compatible API (no native management API) and the shipped package takes no NuGet
///     dependencies. Scaleway accepts the SCW access/secret key pair as the AWS-style credential. The algorithm
///     is pinned by a test against AWS's published reference vector.
/// </summary>
internal static class AwsSignatureV4
{
    /// <summary>SHA-256 of an empty payload — S3 requires <c>x-amz-content-sha256</c> even for body-less requests.</summary>
    internal const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    ///     Signs <paramref name="request" /> in place: sets <c>x-amz-date</c>, <c>x-amz-content-sha256</c> and
    ///     <c>Authorization</c>. The canonical request covers the <c>host</c>, <c>x-amz-content-sha256</c> and
    ///     <c>x-amz-date</c> headers — sufficient for the bucket-management calls this package makes.
    /// </summary>
    public static void Sign(HttpRequestMessage request, string payloadHash, string accessKey, string secretKey, string region, string service, DateTimeOffset timestamp)
    {
        var amzDate = timestamp.UtcDateTime.ToString("yyyyMMddTHHmmssZ");
        var dateStamp = timestamp.UtcDateTime.ToString("yyyyMMdd");
        var uri = request.RequestUri!;

        request.Headers.Remove("x-amz-date");
        request.Headers.Add("x-amz-date", amzDate);
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.Add("x-amz-content-sha256", payloadHash);

        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
        var canonicalRequest = string.Join("\n", request.Method.Method, CanonicalUriPath(uri), CanonicalQuery(uri), canonicalHeaders, signedHeaders, payloadHash);

        var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join("\n", "AWS4-HMAC-SHA256", amzDate, credentialScope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = DeriveSigningKey(secretKey, dateStamp, region, service);
        var signature = Hex(HmacSha256(signingKey, stringToSign));

        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"AWS4-HMAC-SHA256 Credential={accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}"
        );
    }

    public static string HashPayload(byte[] payload)
    {
        return Hex(SHA256.HashData(payload));
    }

    /// <summary>
    ///     The HMAC chain <c>AWS4{secret} → date → region → service → aws4_request</c>. Internal so the test
    ///     suite can pin it against AWS's published reference vector.
    /// </summary>
    internal static byte[] DeriveSigningKey(string secretKey, string dateStamp, string region, string service)
    {
        return HmacSha256(HmacSha256(HmacSha256(HmacSha256(Encoding.UTF8.GetBytes($"AWS4{secretKey}"), dateStamp), region), service), "aws4_request");
    }

    // S3 canonical URIs are the URI-encoded path with '/' preserved; bucket names and the paths this client
    // builds are already URI-safe (lowercase alphanumerics and dashes), so the escaped path is used as-is.
    private static string CanonicalUriPath(Uri uri)
    {
        return uri.AbsolutePath.Length == 0 ? "/" : uri.AbsolutePath;
    }

    private static string CanonicalQuery(Uri uri)
    {
        if (uri.Query.Length <= 1)
        {
            return "";
        }

        var parameters = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
                {
                    var separator = pair.IndexOf('=');
                    return separator < 0 ? (Key: pair, Value: "") : (Key: pair[..separator], Value: pair[(separator + 1)..]);
                }
            )
            .OrderBy(parameter => parameter.Key, StringComparer.Ordinal);

        return string.Join("&", parameters.Select(parameter => $"{parameter.Key}={parameter.Value}"));
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    }

    private static string Hex(byte[] bytes)
    {
        return Convert.ToHexStringLower(bytes);
    }
}
