using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeveloperCli.Installation;
using DeveloperCli.Utilities;
using Scaleway.Sdk;
using Spectre.Console;
using ScwTem = Scaleway.Sdk.Tem.V1Alpha1;

namespace DeveloperCli.Commands;

/// <summary>
///     Prints every DNS record the environment's hosting zone is expected to carry and verifies each
///     one against live DNS. The expected values are derived from the Terraform state itself (no
///     apply, no new outputs) and the live values are resolved with dig; the only write is asking
///     Transactional Email to validate a domain whose records resolve but which it has not validated
///     yet (until it does, it refuses every send from that domain). The deploy runs
///     it first and stops on drift; it also runs standalone at any time — a missing or wrong record is
///     exactly the class of silent failure that turns into "the site is down" only when someone looks.
/// </summary>
/// <remarks>
///     Expected records are derived from state resources: each edge pipeline's backend stage carries
///     the public hostname (domain_name) and the pipeline id forms the CNAME target
///     ({id}.svc.edge.scw.cloud.); each Transactional Email domain carries the SPF and DKIM values.
///     Terraform never owns the public zone (it lives at Hetzner today — see docs/dns-guide.md), so
///     Terraform alone cannot report drift: this command is the drift detector.
/// </remarks>
public sealed partial class DnsCheckCommand : Command
{
    // Transactional Email's domain statuses (the SDK's generated DomainStatus carries the email statuses instead).
    private const string ValidatedEmailDomainStatus = "checked";
    private static readonly string[] InProgressEmailDomainStatuses = ["unchecked", "pending", "autoconfiguring"];

    private static readonly TimeSpan EmailDomainCheckTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EmailDomainCheckPollInterval = TimeSpan.FromSeconds(5);

    public DnsCheckCommand() : base(
        "dns-check",
        "Print and verify the DNS records the environment expects its hosting zone to carry, and have Transactional Email validate its domains once their records resolve (acts with the environment's operator scw profile)."
    )
    {
        var environmentOption = new Option<string>("--environment", "-e")
        {
            Description = "The environment to check (staging)",
            DefaultValueFactory = _ => "staging"
        };
        Options.Add(environmentOption);

        SetAction(parseResult => RunAsync(parseResult.GetValue(environmentOption)!).GetAwaiter().GetResult());
    }

    private static async Task RunAsync(string environment)
    {
        Prerequisite.Ensure(Prerequisite.Terraform);

        var resolved = await ScalewayEnvironment.ConnectAsync(environment);
        var mismatches = await PrintDnsReportAsync(resolved.Credentials, resolved.OrganizationId);
        if (mismatches > 0)
        {
            Environment.Exit(1);
        }
    }

    // Shared with DeployCommand, which runs the same report before building and stops on drift
    // (unless --skip-dns-check); standalone runs exit non-zero on drift. Both connect to the
    // environment first (ScalewayEnvironment.ConnectAsync), so the state is readable.
    public static async Task<int> PrintDnsReportAsync(ScalewayCredentials credentials, string organizationId)
    {
        var environmentModuleFolder = Path.Combine(Configuration.SourceCodeFolder, "cloud-infrastructure", "scaleway", "environment");
        if (!Directory.Exists(environmentModuleFolder))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] environment module not found at {environmentModuleFolder}.");
            return 1;
        }

        // The full state carries every secret, so it is captured in memory only (never logged).
        var stateResult = await ProcessHelper.ExecuteSensitiveAsync("terraform show -json", environmentModuleFolder);
        if (!stateResult.Success)
        {
            AnsiConsole.MarkupLine($"[red]Could not read the Terraform state:[/] {Markup.Escape(stateResult.StdErr.Trim())}");
            return 1;
        }

        var expected = DeriveExpectedRecords(stateResult.StdOut);
        if (expected.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]The state holds no edge pipelines or email domains to verify.[/]");
            return 0;
        }

        var digAvailable = (await ProcessHelper.ExecuteQuietlyAsync("which dig")).Success;
        if (!digAvailable)
        {
            AnsiConsole.MarkupLine("[yellow]dig is not installed — printing the expected records without live verification (install dig for the full check).[/]");
        }

        var table = new Table().Title("[bold] Expected DNS records[/]");
        table.AddColumn("Type");
        table.AddColumn("Name");
        table.AddColumn("Expected");
        table.AddColumn("Live");
        table.AddColumn("Status");

        var mismatches = 0;
        var emailDomainsWithDrift = new HashSet<string>();
        foreach (var record in expected)
        {
            var live = digAvailable ? await ResolveRecordAsync(record) : null;
            var status = !digAvailable
                ? "[yellow]not checked[/]"
                : live is null || live.Count == 0
                    ? "[red]missing[/]"
                    : live.Contains(record.Value, StringComparer.OrdinalIgnoreCase)
                        ? "[green]ok[/]"
                        : "[red]mismatch[/]";
            if (status.Contains("missing") || status.Contains("mismatch"))
            {
                mismatches++;
                if (record.EmailDomainId is not null) emailDomainsWithDrift.Add(record.EmailDomainId);
            }

            table.AddRow(
                record.Type,
                record.Name,
                Truncate(record.Value),
                live is null ? "[dim]-[/]" : Truncate(string.Join(" | ", live)),
                status
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(mismatches == 0
            ? "[green]All DNS records resolve as expected.[/] See cloud-infrastructure/scaleway/docs/dns-guide.md for how to fix any record by hand."
            : $"[red]{mismatches} record(s) missing or mismatched.[/] See cloud-infrastructure/scaleway/docs/dns-guide.md for the exact values to create in the DNS console."
        );

        var emailDomainIds = expected.Select(record => record.EmailDomainId).OfType<string>().Distinct().ToArray();
        if (emailDomainIds.Length > 0 && digAvailable)
        {
            mismatches += await VerifyEmailDomainsAsync(credentials, organizationId, emailDomainIds, emailDomainsWithDrift);
        }

        return mismatches;
    }

    /// <summary>
    ///     Records resolving is not enough to send mail: Transactional Email refuses every send from a domain it has
    ///     not validated (invalid from.email), and it only validates when asked or on its own slow schedule. So once a
    ///     domain's records all resolve, a domain not yet validated is checked here and its outcome reported.
    /// </summary>
    private static async Task<int> VerifyEmailDomainsAsync(ScalewayCredentials credentials, string organizationId, string[] domainIds, HashSet<string> domainsWithDrift)
    {
        using var apiClient = new ScalewayApiClient(new ScalewayCredentialConfig
            {
                AccessKey = credentials.AccessKey, SecretKey = credentials.SecretKey, OrganizationId = OrganizationId.Parse(organizationId)
            }
        );
        var temApi = new ScwTem.TemApi(apiClient);

        var failures = 0;
        foreach (var regionAndId in domainIds)
        {
            var (region, domainId) = SplitRegionalId(regionAndId);
            var domain = await temApi.GetDomainAsync(domainId, region);
            if (domain is null)
            {
                AnsiConsole.MarkupLine($"[red]The Transactional Email domain {Markup.Escape(domainId)} in the state no longer exists.[/] Apply the environment to recreate it.");
                failures++;
                continue;
            }

            if (domain.Status?.Value != ValidatedEmailDomainStatus && !domainsWithDrift.Contains(regionAndId))
            {
                domain = await WaitForEmailDomainCheckAsync(temApi, domainId, region);
            }

            var status = domain.Status?.Value ?? "unknown";
            var error = string.IsNullOrWhiteSpace(domain.LastError) ? "" : $" Scaleway reports: {domain.LastError}";
            if (status == ValidatedEmailDomainStatus)
            {
                AnsiConsole.MarkupLine($"[green]Transactional Email has validated {Markup.Escape(domain.Name ?? domainId)}[/]: mail from it is accepted.");
            }
            else if (domainsWithDrift.Contains(regionAndId))
            {
                AnsiConsole.MarkupLine($"[red]Transactional Email refuses mail from {Markup.Escape(domain.Name ?? domainId)}[/] (status {Markup.Escape(status)}) until the records above resolve; it is checked once they do.");
            }
            else if (InProgressEmailDomainStatuses.Contains(status))
            {
                AnsiConsole.MarkupLine($"[yellow]Transactional Email is still validating {Markup.Escape(domain.Name ?? domainId)}[/]; run dns-check again in a few minutes.{Markup.Escape(error)}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Transactional Email did not validate {Markup.Escape(domain.Name ?? domainId)}[/] (status {Markup.Escape(status)}), so it refuses mail from it.{Markup.Escape(error)}");
                failures++;
            }
        }

        return failures;
    }

    // The check runs asynchronously on Scaleway's side: the domain passes through pending before it settles.
    private static async Task<ScwTem.Domain> WaitForEmailDomainCheckAsync(ScwTem.TemApi temApi, string domainId, string region)
    {
        var domain = await temApi.CheckDomainAsync(domainId, region);
        var deadline = DateTimeOffset.UtcNow + EmailDomainCheckTimeout;
        while (domain.Status?.Value is not ValidatedEmailDomainStatus && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(EmailDomainCheckPollInterval);
            domain = await temApi.GetDomainAsync(domainId, region) ?? domain;
        }

        return domain;
    }

    // Terraform ids regional resources as "<region>/<id>".
    private static (string Region, string Id) SplitRegionalId(string regionalId)
    {
        var separator = regionalId.IndexOf('/');
        return separator < 0 ? (ScalewayEnvironment.Region, regionalId) : (regionalId[..separator], regionalId[(separator + 1)..]);
    }

    private static List<ExpectedRecord> DeriveExpectedRecords(string stateJson)
    {
        var records = new List<ExpectedRecord>();

        using var document = JsonDocument.Parse(stateJson);
        var resources = document.RootElement
            .TryGetProperty("values", out var values) && values.TryGetProperty("root_module", out var rootModule) && rootModule.TryGetProperty("resources", out var moduleResources)
            ? moduleResources.EnumerateArray().ToList()
            : [];

        var pipelineIds = new Dictionary<string, string>(); // pipeline_id -> pipeline resource id
        foreach (var resource in resources.Where(r => r.GetProperty("type").GetString() == "scaleway_edge_services_pipeline"))
        {
            var id = resource.GetProperty("values").GetProperty("id").GetString();
            var pipelineId = resource.GetProperty("values").GetProperty("id").GetString();
            if (id is not null)
            {
                pipelineIds[pipelineId!] = id;
            }
        }

        // The backend stage carries the public hostname (its origin SNI is the FQDN the user points
        // the CNAME at) and links the pipeline whose endpoint is the CNAME target.
        foreach (var resource in resources.Where(r => r.GetProperty("type").GetString() == "scaleway_edge_services_backend_stage"))
        {
            var stageValues = resource.GetProperty("values");
            var pipelineId = stageValues.GetProperty("pipeline_id").GetString();
            var domainName = stageValues
                .GetProperty("lb_backend_config").EnumerateArray().First()
                .GetProperty("lb_config").EnumerateArray().First()
                .GetProperty("domain_name").GetString();

            if (pipelineId is not null && domainName is not null && pipelineIds.TryGetValue(pipelineId, out var pipelineResource))
            {
                records.Add(new ExpectedRecord("CNAME", domainName, $"{pipelineResource}.svc.edge.scw.cloud."));
            }
        }

        foreach (var resource in resources.Where(r => r.GetProperty("type").GetString() == "scaleway_tem_domain"))
        {
            var temValues = resource.GetProperty("values");
            var domainName = temValues.GetProperty("name").GetString();
            var spfConfig = temValues.GetProperty("spf_config").GetString();
            var dkimConfig = temValues.GetProperty("dkim_config").GetString();
            var projectId = temValues.GetProperty("project_id").GetString();
            var domainId = temValues.GetProperty("id").GetString();
            if (domainName is null)
            {
                continue;
            }

            records.Add(new ExpectedRecord("MX", domainName, "10 blackhole.tem.scaleway.com.", domainId));
            if (spfConfig is not null)
            {
                records.Add(new ExpectedRecord("TXT", domainName, $"v=spf1 {spfConfig} -all", domainId));
            }

            // Transactional Email signs with the project id as the DKIM selector; until the key is published under
            // that name the domain never validates and every send is rejected (invalid from.email).
            if (dkimConfig is not null && projectId is not null)
            {
                records.Add(new ExpectedRecord("TXT", $"{projectId}._domainkey.{domainName}", dkimConfig, domainId));
            }

            records.Add(new ExpectedRecord("TXT", $"_dmarc.{domainName}", "v=DMARC1; p=none", domainId));
        }

        return records;
    }

    private static async Task<List<string>> ResolveRecordAsync(ExpectedRecord record)
    {
        // ProcessHelper runs dig directly (no shell), so record names from state are plain arguments.
        var result = await ProcessHelper.ExecuteQuietlyAsync($"dig +short +time=3 +tries=1 {record.Type} {record.Name}");
        if (!result.Success) return [];

        // dig prints CNAME chains one per line, and TXT values as quoted strings — a value over 255
        // characters (a 2048-bit DKIM key) as several quoted chunks on one line ("part1" "part2").
        // Joining the chunks yields the record value, which compares directly against the expected one.
        return result.StdOut
            .Split('\n')
            .Select(line => TxtChunkSeparator().Replace(line.Trim(), "").Trim('"'))
            .Where(line => line.Length > 0)
            .ToList();
    }

    [GeneratedRegex("\"\\s+\"")]
    private static partial Regex TxtChunkSeparator();

    private static string Truncate(string value)
    {
        return value.Length > 60 ? value[..57] + "..." : value;
    }

    // EmailDomainId ties a record to the Transactional Email domain it validates (null for edge CNAMEs).
    private sealed record ExpectedRecord(string Type, string Name, string Value, string? EmailDomainId = null);
}
