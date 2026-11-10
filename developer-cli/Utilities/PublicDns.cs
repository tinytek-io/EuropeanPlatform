using System.Net.Http.Json;
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

    private static readonly HttpClient ResolverHttpClient = CreateResolverHttpClient();

    // The record type numbers DNS answers carry, by the names the callers use.
    private static readonly Dictionary<string, int> RecordTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = 1, ["CNAME"] = 5, ["MX"] = 15, ["TXT"] = 16, ["AAAA"] = 28
    };

    /// <summary>
    ///     The values of the records of <paramref name="recordType" /> at <paramref name="name" />, written as dig
    ///     prints them (a trailing dot on names, TXT values in quoted chunks), or empty when there are none. Null when
    ///     the resolver could not be reached, which callers must report as unchecked rather than as missing.
    /// </summary>
    public static async Task<string[]?> ResolveAsync(string recordType, string name)
    {
        var typeNumber = RecordTypes[recordType];
        try
        {
            var response = await ResolverHttpClient.GetFromJsonAsync<DnsJsonResponse>($"{ResolverUrl}?name={Uri.EscapeDataString(name)}&type={recordType.ToUpperInvariant()}");

            // Answers to a lookup that follows a CNAME chain carry the chain's CNAME records too; dig +short
            // <type> prints those as well, but every caller wants the records of the type it asked for.
            return response?.Answer?.Where(answer => answer.Type == typeNumber).Select(answer => answer.Data).ToArray() ?? [];
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
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
    private sealed record DnsJsonResponse([property: JsonPropertyName("Answer")] DnsJsonAnswer[]? Answer);

    [UsedImplicitly]
    private sealed record DnsJsonAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
