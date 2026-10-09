using System.CommandLine;
using System.Diagnostics;
using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using DeveloperCli.Utilities;
using Spectre.Console;

namespace DeveloperCli.Commands;

/// <summary>
///     Verifies the Scaleway Edge Services pipelines fronting the public hostnames and the HTTPS
///     endpoints they serve. Read-only: it never mutates Scaleway, the cluster, or DNS. Meant to run
///     after terraform apply / aspire deploy — the edge API reports stages as "active" while pipelines
///     silently lose their head stage or their managed certificate material, and neither Terraform
///     (the provider's head_stage Read is a no-op) nor the deploy pipeline surfaces that.
/// </summary>
/// <remarks>
///     Uses the raw edge-services HTTP API rather than the generated SDK: the endpoints needed
///     (head-stages in particular) are not cherry-picked into Scaleway.Sdk yet, and scw has no
///     head-stage command at all. Authentication comes from the SCW_SECRET_KEY environment variable.
/// </remarks>
public sealed class EdgeHealthCommand : Command
{
    private const string ApiBaseUrl = "https://api.scaleway.com/edge-services/v1beta1";
    private const int MinimumDaysBeforeCertificateExpiry = 7;

    // A plain shared client for the public endpoint checks; it carries no credentials and its
    // default handler validates certificates, which is exactly the behavior under test.
    private static readonly HttpClient EndpointHttpClient = new();

    public EdgeHealthCommand() : base(
        "edge-health",
        "Verify the Scaleway Edge Services pipelines, their stage chains, and the public HTTPS endpoints (read-only). Requires SCW_SECRET_KEY."
    )
    {
        SetAction(_ => ExecuteAsync().GetAwaiter().GetResult());
    }

    private static async Task ExecuteAsync()
    {
        var secretKey = Environment.GetEnvironmentVariable("SCW_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] SCW_SECRET_KEY is not set. Export it before running (see cloud-infrastructure/scaleway/environment).[/]");
            Environment.Exit(1);
        }

        var projectFilter = Environment.GetEnvironmentVariable("SCW_DEFAULT_PROJECT_ID");

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("X-Auth-Token", secretKey);

        var pipelines = await ListPipelinesAsync(httpClient, projectFilter);
        if (pipelines.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] no Edge Services pipelines found.[/]");
            Environment.Exit(1);
        }

        AnsiConsole.MarkupLine($"Checking [bold]{pipelines.Count}[/] pipeline(s)...\n");

        var failures = 0;

        foreach (var (pipelineId, pipelineName) in pipelines)
        {
            AnsiConsole.MarkupLine($"[bold]{pipelineName}[/] ([dim]{pipelineId}[/])");

            var pipelineChecks = new List<(string Check, bool Passed, string Detail)>
            {
                await CheckPipelineStatusAsync(httpClient, pipelineId)
            };

            var stages = await ListStagesAsync(httpClient, pipelineId);
            pipelineChecks.Add(HeadStageCheck(stages));
            pipelineChecks.AddRange(StageChecks(stages));

            failures += RenderChecks(pipelineChecks);

            var hostnames = stages.DnsStages
                .Where(stage => stage["type"]?.GetValue<string>() == "custom")
                .SelectMany(GetFqdns)
                .ToList();

            // The pipeline's endpoint: recreated pipelines get a new one, which silently orphans
            // every CNAME still pointing at the old endpoint (the API exposes no stable alias).
            var pipelineEndpoint = stages.DnsStages
                .Where(stage => stage["type"]?.GetValue<string>() == "custom")
                .Select(stage => stage["default_fqdn"]?.GetValue<string>())
                .FirstOrDefault(fqdn => !string.IsNullOrWhiteSpace(fqdn));

            foreach (var hostname in hostnames)
            {
                var dnsChecks = pipelineEndpoint is null
                    ? [("dns record", false, "no active pipeline endpoint to point the DNS at — attach the head stage")]
                    : await CheckDnsRecordAsync(hostname, pipelineEndpoint);
                failures += RenderChecks(dnsChecks);

                var endpointChecks = await CheckPublicEndpointAsync(hostname);
                failures += RenderChecks(endpointChecks);
            }

            AnsiConsole.WriteLine();
        }

        if (failures > 0)
        {
            AnsiConsole.MarkupLine($"[red]{failures} check(s) failed.[/]");
            Environment.Exit(1);
        }

        AnsiConsole.MarkupLine("[green]All edge health checks passed.[/]");
    }

    private static async Task<List<(string Id, string Name)>> ListPipelinesAsync(HttpClient httpClient, string? projectFilter)
    {
        var url = $"{ApiBaseUrl}/pipelines?page_size=100" + (projectFilter is null ? "" : $"&project_id={projectFilter}");
        var pipelines = await ReadJsonArrayAsync(httpClient, url, "pipelines");
        return pipelines
            .Select(node => (node["id"]!.GetValue<string>(), node["name"]?.GetValue<string>() ?? node["id"]!.GetValue<string>()))
            .ToList();
    }

    private static async Task<(string Check, bool Passed, string Detail)> CheckPipelineStatusAsync(HttpClient httpClient, string pipelineId)
    {
        try
        {
            var pipeline = await ReadJsonAsync(httpClient, $"{ApiBaseUrl}/pipelines/{pipelineId}");
            var status = pipeline["status"]?.GetValue<string>();
            var errors = pipeline["errors"]?.AsArray();

            if (status != "ready")
            {
                return ("pipeline status", false, $"status is '{status}', expected 'ready'");
            }

            if (errors is { Count: > 0 })
            {
                var messages = string.Join("; ", errors.Select(error =>
                        $"{error?["code"]?.GetValue<string>()}: {error?["message"]?.GetValue<string>()}"
                    )
                );
                return ("pipeline status", false, $"status ready but has errors: {messages}");
            }

            return ("pipeline status", true, "ready, no errors");
        }
        catch (Exception exception)
        {
            return ("pipeline status", false, exception.Message);
        }
    }

    private static async Task<PipelineStages> ListStagesAsync(HttpClient httpClient, string pipelineId)
    {
        async Task<List<JsonNode>> StageList(string stageType)
        {
            return await ReadJsonArrayAsync(httpClient, $"{ApiBaseUrl}/pipelines/{pipelineId}/{stageType}-stages", "stages");
        }

        var dns = await StageList("dns");
        var tls = await StageList("tls");
        var cache = await StageList("cache");
        var waf = await StageList("waf");
        var backend = await StageList("backend");
        var heads = await ReadJsonArrayAsync(httpClient, $"{ApiBaseUrl}/pipelines/{pipelineId}/head-stages", "head_stages");

        return new PipelineStages(dns, tls, cache, waf, backend, heads);
    }

    /// <summary>
    ///     A missing head stage leaves every stage inactive with no other error anywhere (found live:
    ///     the pipeline reads "error: pipeline_missing_head_stage" while all stages claim green in
    ///     their own listings) — this is the one check neither Terraform nor the deploy provides.
    /// </summary>
    private static (string Check, bool Passed, string Detail) HeadStageCheck(PipelineStages stages)
    {
        if (stages.HeadStages.Count == 0)
        {
            return ("head stage", false, "no head stage — the pipeline has no entry point and will not serve");
        }

        var headDnsStageId = stages.HeadStages[0]["dns_stage_id"]?.GetValue<string>();
        var headExists = stages.DnsStages.Any(stage => stage["id"]?.GetValue<string>() == headDnsStageId);
        return headExists
            ? ("head stage", true, $"head points at DNS stage {headDnsStageId?[..8]}")
            : ("head stage", false, $"head points at DNS stage '{headDnsStageId}' which does not exist (stale head)");
    }

    private static List<string> GetFqdns(JsonNode stage)
    {
        var fqdns = stage["fqdns"]?.AsArray();
        return fqdns is null
            ? []
            : fqdns.Select(fqdn => fqdn?.GetValue<string>()).OfType<string>().ToList();
    }

    private static IEnumerable<(string Check, bool Passed, string Detail)> StageChecks(PipelineStages stages)
    {
        // DNS: the custom stage carries the FQDNs this pipeline serves; it must be active.
        var customDns = stages.DnsStages.Where(stage => stage["type"]?.GetValue<string>() == "custom").ToList();
        if (customDns.Count == 0)
        {
            yield return ("dns stage", false, "no custom DNS stage");
        }
        else
        {
            var fqdns = customDns.SelectMany(GetFqdns).ToList();
            var active = customDns.All(stage => stage["status"]?.GetValue<string>() == "active");
            yield return active && fqdns.Count > 0
                ? ("dns stage", true, $"active, serving {string.Join(", ", fqdns)}")
                : ("dns stage", false, $"status '{string.Join(",", customDns.Select(stage => stage["status"]?.GetValue<string>()))}', fqdns: [{string.Join(", ", fqdns)}]");
        }

        // TLS: active is not enough — the managed certificate must have materialized as a secret
        // reference. Issuance failures (found live: "tls_managed_internal") leave the stage active
        // with no secret, and the pipeline then serves no certificate for the custom hostnames.
        var tls = stages.TlsStages.FirstOrDefault();
        if (tls is null)
        {
            yield return ("tls stage", false, "no TLS stage");
        }
        else
        {
            var secretCount = tls["secrets"]?.AsArray().Count ?? 0;
            var status = tls["status"]?.GetValue<string>();
            yield return status == "active" && secretCount > 0
                ? ("tls stage", true, $"active, certificate materialized ({secretCount} secret(s))")
                : ("tls stage", false, $"status '{status}', certificate secrets: {secretCount} (managed certificate did not issue)");
        }

        foreach (var (name, nodes) in new[] { ("cache stage", stages.CacheStages), ("waf stage", stages.WafStages), ("backend stage", stages.BackendStages) })
        {
            if (nodes.Count == 0)
            {
                yield return (name, false, $"no {name.Split(' ')[0]} stage");
                continue;
            }

            var status = nodes[0]["status"]?.GetValue<string>();
            yield return status == "active"
                ? (name, true, "active")
                : (name, false, $"status '{status}'");
        }
    }

    /// <summary>
    ///     Verifies that the public hostname's CNAME points at the pipeline's current endpoint. A
    ///     recreated pipeline gets a new &lt;id&gt;.svc.edge.scw.cloud endpoint, and every DNS record still
    ///     pointing at the old one silently serves nothing — the edge API has no stable alias, so this
    ///     drift must be caught on the DNS side.
    /// </summary>
    private static async Task<List<(string Check, bool Passed, string Detail)>> CheckDnsRecordAsync(string hostname, string endpoint)
    {
        var checks = new List<(string Check, bool Passed, string Detail)>();

        try
        {
            var cname = await ResolveCnameAsync(hostname);
            if (string.IsNullOrWhiteSpace(cname))
            {
                checks.Add(("dns record", false, $"no CNAME record for {hostname} — point it at {endpoint}"));
                return checks;
            }

            if (string.Equals(cname, endpoint.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(("dns record", true, $"CNAME points at the pipeline endpoint ({endpoint})"));
            }
            else
            {
                checks.Add((
                        "dns record",
                        false,
                        $"CNAME points at {cname} but the pipeline endpoint is {endpoint} — the pipeline was recreated; update the DNS record"
                    )
                );
            }
        }
        catch (Exception exception)
        {
            checks.Add(("dns record", false, $"CNAME lookup failed: {exception.Message}"));
        }

        return checks;
    }

    /// <summary>
    ///     Resolves the CNAME target of a hostname through `dig` (.NET's DNS client cannot see the
    ///     CNAME chain, only the final addresses).
    /// </summary>
    private static async Task<string> ResolveCnameAsync(string hostname)
    {
        var output = await Task.Run(() => ProcessHelper.StartProcess(
                new ProcessStartInfo
                {
                    FileName = "dig",
                    Arguments = $"+short {hostname} CNAME",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                exitOnError: false
            )
        );

        return output.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim().TrimEnd('.') ?? "";
    }

    private static async Task<List<(string Check, bool Passed, string Detail)>> CheckPublicEndpointAsync(string hostname)
    {
        var checks = new List<(string Check, bool Passed, string Detail)>();

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname);
            checks.Add(("dns", true, $"resolves to {string.Join(", ", addresses.Select(address => address.ToString()))}"));
        }
        catch (Exception exception)
        {
            checks.Add(("dns", false, exception.Message));
            return checks;
        }

        // Connect and inspect the certificate the endpoint actually serves, without trusting it
        // yet — the details (SAN match, expiry) are reported as checks themselves.
        try
        {
            using var tcpClient = new TcpClient(hostname, 443);
            await using var sslStream = new SslStream(tcpClient.GetStream(), false,
                static (_, _, _, _) => true
            );
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = hostname,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }
            );

            var certificate = new X509Certificate2(sslStream.RemoteCertificate!);
            var subjectMatches = CertificateMatchesHostname(certificate, hostname);
            var daysLeft = (certificate.NotAfter - DateTime.UtcNow).TotalDays;
            checks.Add(subjectMatches
                ? ("certificate", true, $"issued for {hostname}, expires in {daysLeft:F0} day(s)")
                : ("certificate", false, $"serves certificate for '{certificate.Subject}', which does not cover {hostname}")
            );
            checks.Add(daysLeft >= MinimumDaysBeforeCertificateExpiry
                ? ("certificate expiry", true, $"{daysLeft:F0} day(s) left")
                : ("certificate expiry", false, $"only {daysLeft:F0} day(s) left (threshold {MinimumDaysBeforeCertificateExpiry})")
            );
        }
        catch (Exception exception)
        {
            checks.Add(("certificate", false, $"TLS handshake failed: {exception.Message}"));
            return checks;
        }

        try
        {
            using var response = await EndpointHttpClient.GetAsync($"https://{hostname}/");
            checks.Add(response.IsSuccessStatusCode || (int)response.StatusCode < 500
                ? ("endpoint", true, $"HTTPS {(int)response.StatusCode} {response.ReasonPhrase}")
                : ("endpoint", false, $"HTTPS {(int)response.StatusCode} {response.ReasonPhrase}")
            );
        }
        catch (Exception exception)
        {
            checks.Add(("endpoint", false, exception.Message));
        }

        return checks;
    }

    private static bool CertificateMatchesHostname(X509Certificate2 certificate, string hostname)
    {
        var commonName = certificate.GetNameInfo(X509NameType.SimpleName, false);
        if (string.Equals(commonName, hostname, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var sanExtension = certificate.Extensions.FirstOrDefault(extension => extension.Oid?.Value == "2.5.29.17");
        if (sanExtension is null)
        {
            return false;
        }

        var sequence = new AsnReader(sanExtension.RawData, AsnEncodingRules.DER).ReadSequence();
        while (sequence.HasData)
        {
            // GeneralName [2] dNSName is a context-specific primitive tag holding an IA5String.
            if (sequence.PeekTag() is { TagClass: TagClass.ContextSpecific, TagValue: 2 } dnsNameTag)
            {
                var name = sequence.ReadCharacterString(UniversalTagNumber.IA5String, dnsNameTag);
                if (string.Equals(name, hostname, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else
            {
                sequence.ReadEncodedValue();
            }
        }

        return false;
    }

    private static int RenderChecks(IEnumerable<(string Check, bool Passed, string Detail)> checks)
    {
        var failures = 0;
        foreach (var (check, passed, detail) in checks)
        {
            AnsiConsole.MarkupLine($"  {(passed ? "[green]✓[/]" : "[red]✗[/]")} [bold]{check,-12}[/] {detail.EscapeMarkup()}");
            if (!passed)
            {
                failures++;
            }
        }

        return failures;
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpClient httpClient, string url)
    {
        using var response = await httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase} from {url}");
        }

        var content = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(content) ?? throw new HttpRequestException($"empty response from {url}");
    }

    private static async Task<List<JsonNode>> ReadJsonArrayAsync(HttpClient httpClient, string url, string propertyName)
    {
        using var response = await httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase} from {url}");
        }

        var content = await response.Content.ReadAsStringAsync();
        var node = JsonNode.Parse(content) ?? throw new HttpRequestException($"empty response from {url}");
        return node[propertyName]?.AsArray().Where(item => item is not null).Select(item => item!).ToList() ?? [];
    }

    private sealed record PipelineStages(
        List<JsonNode> DnsStages,
        List<JsonNode> TlsStages,
        List<JsonNode> CacheStages,
        List<JsonNode> WafStages,
        List<JsonNode> BackendStages,
        List<JsonNode> HeadStages
    );
}
