using System.Text.Json;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace DeveloperCli.Utilities;

/// <summary>
///     Looks up DNS records the way the public internet sees them, through Cloudflare's DNS-over-HTTPS JSON API.
///     .NET's own resolver returns addresses only, so CNAME, TXT and MX records need a DNS client; asking a public
///     resolver over HTTPS needs no tool on the machine (dig is missing on Windows) and no NuGet package. A public
///     resolver is also closer to what Edge Services and Transactional Email see when they verify a record than
///     whatever the local machine has cached. The names looked up are public records.
/// </summary>
public static class PublicDns
{
    private const string ResolverUrl = "https://cloudflare-dns.com/dns-query";

    // DNS response codes (RFC 1035 §4.1.1) that mean the lookup worked: the name exists, or it does not. Every other
    // code (SERVFAIL, REFUSED, ...) says the resolver could not answer, which is not the same as no records.
    private const int NoError = 0;
    private const int NameError = 3;

    private static readonly HttpClient ResolverHttpClient = CreateResolverHttpClient();

    // The record type numbers DNS answers carry, by the names the callers use.
    private static readonly Dictionary<string, int> RecordTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = 1, ["CNAME"] = 5, ["MX"] = 15, ["TXT"] = 16, ["AAAA"] = 28
    };

    /// <summary>
    ///     The values of the records of <paramref name="recordType" /> at <paramref name="name" />, written as dig
    ///     prints them (a trailing dot on names, TXT values in quoted chunks), or empty when there are none. Null when
    ///     the resolver could not be reached or could not answer, which callers must report as unchecked rather than
    ///     as missing.
    /// </summary>
    public static async Task<string[]?> ResolveAsync(string recordType, string name)
    {
        try
        {
            var json = await ResolverHttpClient.GetStringAsync($"{ResolverUrl}?name={Uri.EscapeDataString(name)}&type={recordType.ToUpperInvariant()}");
            return ParseResponse(json, recordType);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Reads a DNS-over-HTTPS JSON answer. A resolver failure arrives as HTTP 200 with a failing DNS status
    ///     (SERVFAIL, REFUSED) and no answers, so the status decides: success or NXDOMAIN yields the records (empty when
    ///     there are none), any other status yields null.
    /// </summary>
    public static string[]? ParseResponse(string json, string recordType)
    {
        var response = JsonSerializer.Deserialize<DnsJsonResponse>(json);
        if (response is null || response.Status is not (NoError or NameError))
        {
            return null;
        }

        // Answers to a lookup that follows a CNAME chain carry the chain's CNAME records too; dig +short <type> prints
        // those as well, but every caller wants the records of the type it asked for.
        var typeNumber = RecordTypes[recordType];
        return response.Answer?.Where(answer => answer.Type == typeNumber).Select(answer => answer.Data).ToArray() ?? [];
    }

    private static HttpClient CreateResolverHttpClient()
    {
        var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(10);
        httpClient.DefaultRequestHeaders.Add("Accept", "application/dns-json");
        return httpClient;
    }

    // Instantiated by the JSON deserializer only.
    [UsedImplicitly]
    private sealed record DnsJsonResponse(
        [property: JsonPropertyName("Status")] int? Status,
        [property: JsonPropertyName("Answer")] DnsJsonAnswer[]? Answer
    );

    [UsedImplicitly]
    private sealed record DnsJsonAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
