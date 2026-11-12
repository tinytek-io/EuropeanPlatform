using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeveloperCli.Installation;
using DeveloperCli.Utilities;
using Scaleway.Sdk;
using Spectre.Console;
using ScwEdge = Scaleway.Sdk.EdgeServices.V1Beta1;
using ScwTem = Scaleway.Sdk.Tem.V1Alpha1;

namespace DeveloperCli.Commands;

/// <summary>
///     Prints every DNS record the environment's hosting zone is expected to carry and verifies each
///     one against live DNS. The expected values are derived from the Terraform state itself (no
///     apply, no new outputs) and the live values are resolved through a public resolver (PublicDns). The only writes ask
///     Scaleway to
///     check again what it checks once and then trusts: Transactional Email to validate a domain whose
///     records resolve (until it does, it refuses every send from that domain), and Edge Services to
///     verify a hostname whose CNAME resolves (until it does, the pipeline warns and its managed
///     certificate depends on a hostname Scaleway considers unverified). The deploy runs
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
    private static readonly ScwTem.DomainStatus[] InProgressEmailDomainStatuses = [ScwTem.DomainStatus.Unchecked, ScwTem.DomainStatus.Pending, ScwTem.DomainStatus.Autoconfiguring];

    private static readonly TimeSpan EmailDomainCheckTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CheckPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EdgeHostnameCheckTimeout = TimeSpan.FromMinutes(2);

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

        var table = new Table().Title("[bold] Expected DNS records[/]");
        table.AddColumn("Type");
        table.AddColumn("Name");
        table.AddColumn("Expected");
        table.AddColumn("Live");
        table.AddColumn("Status");

        var mismatches = 0;
        var uncheckedRecords = 0;

        // Records that are wrong, or that could not be looked up: the domains and pipelines they belong to are not
        // asked to verify, since Scaleway would check them against the same records.
        var emailDomainsWithDrift = new HashSet<string>();
        var edgePipelinesWithDrift = new HashSet<string>();
        foreach (var record in expected)
        {
            var live = await ResolveRecordAsync(record);
            var status = live is null
                ? "[yellow]not checked[/]"
                : live.Count == 0
                    ? "[red]missing[/]"
                    : live.Contains(record.Value, StringComparer.OrdinalIgnoreCase)
                        ? "[green]ok[/]"
                        : "[red]mismatch[/]";
            if (live is null || !status.Contains("ok"))
            {
                if (live is null)
                {
                    uncheckedRecords++;
                }
                else
                {
                    mismatches++;
                }

                if (record.EmailDomainId is not null) emailDomainsWithDrift.Add(record.EmailDomainId);
                if (record.EdgePipelineId is not null) edgePipelinesWithDrift.Add(record.EdgePipelineId);
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
        if (uncheckedRecords > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{uncheckedRecords} record(s) could not be looked up[/] (the public resolver did not answer); run dns-check again to verify them.");
        }

        AnsiConsole.MarkupLine(mismatches == 0
            ? "[green]Every record that was looked up resolves as expected.[/] See cloud-infrastructure/scaleway/docs/dns-guide.md for how to fix any record by hand."
            : $"[red]{mismatches} record(s) missing or mismatched.[/] See cloud-infrastructure/scaleway/docs/dns-guide.md for the exact values to create in the DNS console."
        );

        using var apiClient = new ScalewayApiClient(new ScalewayCredentialConfig
            {
                AccessKey = credentials.AccessKey, SecretKey = credentials.SecretKey, OrganizationId = OrganizationId.Parse(organizationId)
            }
        );

        var edgePipelineIds = expected.Select(record => record.EdgePipelineId).OfType<string>().Where(id => !edgePipelinesWithDrift.Contains(id)).Distinct().ToArray();
        mismatches += await VerifyEdgeHostnamesAsync(new ScwEdge.EdgeServicesApi(apiClient), edgePipelineIds);

        var emailDomainIds = expected.Select(record => record.EmailDomainId).OfType<string>().Distinct().ToArray();
        if (emailDomainIds.Length > 0)
        {
            mismatches += await VerifyEmailDomainsAsync(new ScwTem.TemApi(apiClient), emailDomainIds, emailDomainsWithDrift);
        }

        return mismatches;
    }

    /// <summary>
    ///     Records resolving is not enough to send mail: Transactional Email refuses every send from a domain it has
    ///     not validated (invalid from.email), and it only validates when asked or on its own slow schedule. So once a
    ///     domain's records all resolve, a domain not yet validated is checked here and its outcome reported.
    /// </summary>
    private static async Task<int> VerifyEmailDomainsAsync(ScwTem.TemApi temApi, string[] domainIds, HashSet<string> domainsWithDrift)
    {
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

            if (domain.Status != ScwTem.DomainStatus.Checked && !domainsWithDrift.Contains(regionAndId))
            {
                domain = await WaitForEmailDomainCheckAsync(temApi, domainId, region);
            }

            var status = domain.Status ?? ScwTem.DomainStatus.Unknown;
            var error = string.IsNullOrWhiteSpace(domain.LastError) ? "" : $" Scaleway reports: {domain.LastError}";
            if (status == ScwTem.DomainStatus.Checked)
            {
                AnsiConsole.MarkupLine($"[green]Transactional Email has validated {Markup.Escape(domain.Name ?? domainId)}[/]: mail from it is accepted.");
            }
            else if (domainsWithDrift.Contains(regionAndId))
            {
                AnsiConsole.MarkupLine($"[red]Transactional Email refuses mail from {Markup.Escape(domain.Name ?? domainId)}[/] (status {Markup.Escape(status.Value)}) until the records above resolve; it is checked once they do.");
            }
            else if (InProgressEmailDomainStatuses.Contains(status))
            {
                AnsiConsole.MarkupLine($"[yellow]Transactional Email is still validating {Markup.Escape(domain.Name ?? domainId)}[/]; run dns-check again in a few minutes.{Markup.Escape(error)}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Transactional Email did not validate {Markup.Escape(domain.Name ?? domainId)}[/] (status {Markup.Escape(status.Value)}), so it refuses mail from it.{Markup.Escape(error)}");
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
        while (domain.Status != ScwTem.DomainStatus.Checked && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(CheckPollInterval);
            domain = await temApi.GetDomainAsync(domainId, region) ?? domain;
        }

        return domain;
    }

    /// <summary>
    ///     Edge Services verifies a hostname's CNAME when its DNS stage is written, and does not retry on its own
    ///     (found live: a stage written while its record was being changed stayed unverified, type <c>auto</c>
    ///     instead of <c>custom</c> with the pipeline in <c>dns_cname_resolve</c>, for days after the record was
    ///     right). The API has no check action, so for a pipeline whose CNAME now resolves the hostname is written
    ///     again unchanged: the one write short of recreating the stage that can make Scaleway look again.
    /// </summary>
    private static async Task<int> VerifyEdgeHostnamesAsync(ScwEdge.EdgeServicesApi edgeServices, string[] pipelineIds)
    {
        var failures = 0;
        foreach (var pipelineId in pipelineIds)
        {
            // Found by its FQDNs, not its type: the type is exactly what an unverified stage gets wrong.
            var hostnameStage = (await edgeServices.ListDnsStagesAsync(pipelineId)).FirstOrDefault(stage => stage.Fqdns is { Length: > 0 });
            if (hostnameStage is null || hostnameStage.Type == ScwEdge.DNSStageType.Custom) continue;

            var hostname = string.Join(", ", hostnameStage.Fqdns!);
            AnsiConsole.MarkupLine($"[yellow]Edge Services has not verified {Markup.Escape(hostname)}[/] although its CNAME resolves; asking it to check again...");
            await edgeServices.UpdateDnsStageAsync(hostnameStage.Id!, new ScwEdge.UpdateDnsStageRequest { Fqdns = hostnameStage.Fqdns });

            if (await WaitForEdgeHostnameCheckAsync(edgeServices, hostnameStage.Id!))
            {
                AnsiConsole.MarkupLine($"[green]Edge Services has verified {Markup.Escape(hostname)}.[/]");
                continue;
            }

            AnsiConsole.MarkupLine(
                $"[red]Edge Services still has not verified {Markup.Escape(hostname)}[/] after {EdgeHostnameCheckTimeout.TotalMinutes:F0} minutes. " +
                "Run dns-check again later; if it stays unverified, recreate the pipeline's DNS stage (terraform apply -replace on its gateway_custom stage)."
            );
            failures++;
        }

        return failures;
    }

    private static async Task<bool> WaitForEdgeHostnameCheckAsync(ScwEdge.EdgeServicesApi edgeServices, string dnsStageId)
    {
        var deadline = DateTimeOffset.UtcNow + EdgeHostnameCheckTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(CheckPollInterval);
            var stage = await edgeServices.GetDnsStageAsync(dnsStageId);
            if (stage?.Type == ScwEdge.DNSStageType.Custom) return true;
        }

        return false;
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
                records.Add(new ExpectedRecord("CNAME", domainName, $"{pipelineResource}.svc.edge.scw.cloud.", EdgePipelineId: pipelineResource));
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

    // Null when the resolver could not be reached, so the record reads as unchecked rather than missing.
    private static async Task<List<string>?> ResolveRecordAsync(ExpectedRecord record)
    {
        var values = await PublicDns.ResolveAsync(record.Type, record.Name);

        // TXT values arrive as quoted strings — a value over 255 characters (a 2048-bit DKIM key) as several quoted
        // chunks ("part1" "part2"). Joining the chunks yields the record value, which compares directly against the
        // expected one.
        return values?
            .Select(value => TxtChunkSeparator().Replace(value.Trim(), "").Trim('"'))
            .Where(value => value.Length > 0)
            .ToList();
    }

    [GeneratedRegex("\"\\s+\"")]
    private static partial Regex TxtChunkSeparator();

    private static string Truncate(string value)
    {
        return value.Length > 60 ? value[..57] + "..." : value;
    }

    // EmailDomainId ties a record to the Transactional Email domain it validates, EdgePipelineId to the edge pipeline
    // whose hostname it points at; each is null for the other kind of record.
    private sealed record ExpectedRecord(string Type, string Name, string Value, string? EmailDomainId = null, string? EdgePipelineId = null);
}
