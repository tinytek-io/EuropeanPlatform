using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using DeveloperCli.Installation;
using DeveloperCli.Utilities;
using Spectre.Console;

namespace DeveloperCli.Commands;

/// <summary>
///     Prints every DNS record the environment's hosting zone is expected to carry and verifies each
///     one against live DNS. Read-only: the expected values are derived from the Terraform state
///     itself (no apply, no new outputs) and the live values are resolved with dig. Meant to run
///     after aspire deploy (which calls it) and standalone at any time — a missing or wrong record is
///     exactly the class of silent failure that turns into "the site is down" only when someone looks.
/// </summary>
/// <remarks>
///     Expected records are derived from state resources: each edge pipeline's backend stage carries
///     the public hostname (domain_name) and the pipeline id forms the CNAME target
///     ({id}.svc.edge.scw.cloud.); each Transactional Email domain carries the SPF and DKIM values.
///     Terraform never owns the public zone (it lives at Hetzner today — see docs/dns-guide.md), so
///     Terraform alone cannot report drift: this command is the drift detector.
/// </remarks>
public sealed class DnsCheckCommand : Command
{
    public DnsCheckCommand() : base(
        "dns-check",
        "Print and verify the DNS records the environment expects its hosting zone to carry (read-only; requires the Terraform environment variables)."
    )
    {
        SetAction(_ => RunAsync().GetAwaiter().GetResult());
    }

    private static async Task RunAsync()
    {
        var mismatches = await PrintDnsReportAsync();
        if (mismatches > 0)
        {
            Environment.Exit(1);
        }
    }

    // Shared with DeployCommand, which prints the same report at the end of a successful deploy —
    // there it is informational (never fails the deploy); standalone runs exit non-zero on drift.
    public static async Task<int> PrintDnsReportAsync()
    {
        var environmentModuleFolder = Path.Combine(Configuration.SourceCodeFolder, "cloud-infrastructure", "scaleway", "environment");
        if (!Directory.Exists(environmentModuleFolder))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] environment module not found at {environmentModuleFolder}.");
            return 1;
        }

        // terraform show reads the remote state; the state-bucket credentials are the same ones the
        // deploy pipeline exports (see cloud-infrastructure/scaleway/environment/main.tf backend block).
        var stateJson = await RunCaptureAsync("terraform show -json", environmentModuleFolder, "read the Terraform state");
        if (stateJson is null)
        {
            AnsiConsole.MarkupLine("[red]Could not read the Terraform state.[/] Export the Scaleway and state-bucket credentials (see cloud-infrastructure/scaleway/environment) and try again.");
            return 1;
        }

        var expected = DeriveExpectedRecords(stateJson);
        if (expected.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]The state holds no edge pipelines or email domains to verify.[/]");
            return 0;
        }

        var digAvailable = !string.IsNullOrWhiteSpace(await RunCaptureAsync("which dig", null, "locate dig"));
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
        foreach (var record in expected)
        {
            var live = digAvailable ? ResolveRecord(record) : null;
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
            : $"[red]{mismatches} record(s) missing or mismatched.[/] See cloud-infrastructure/scaleway/docs/dns-guide.md for the exact values to create in the DNS console.");
        return mismatches;
    }

    private sealed record ExpectedRecord(string Type, string Name, string Value);

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
            if (domainName is null)
            {
                continue;
            }

            records.Add(new ExpectedRecord("MX", domainName, "10 blackhole.tem.scaleway.com."));
            if (spfConfig is not null)
            {
                records.Add(new ExpectedRecord("TXT", domainName, $"v=spf1 {spfConfig} -all"));
            }
            if (dkimConfig is not null)
            {
                records.Add(new ExpectedRecord("TXT", $"tem._domainkey.{domainName}", dkimConfig));
            }
            records.Add(new ExpectedRecord("TXT", $"_dmarc.{domainName}", "v=DMARC1; p=none"));
        }

        return records;
    }

    private static List<string>? ResolveRecord(ExpectedRecord record)
    {
        var output = RunCaptureAsync($"dig +short +time=3 +tries=1 {record.Type} {record.Name}", null, $"resolve {record.Name}");
        var result = output?.GetAwaiter().GetResult()?.Trim();
        if (string.IsNullOrEmpty(result))
        {
            return new List<string>();
        }

        // dig quotes TXT values and prints CNAME/MX chains one per line; normalize quotes so the
        // expected value compares directly against any line in the chain.
        return result
            .Split('\n')
            .Select(line => line.Trim().Trim('"').Trim('"'))
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static string Truncate(string value)
    {
        return value.Length > 60 ? value[..57] + "..." : value;
    }

    private static async Task<string?> RunCaptureAsync(string command, string? workingDirectory, string operationName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/zsh",
            Arguments = $"-c \"{command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        try
        {
            using var process = Process.Start(startInfo)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Could not {operationName}:[/] {exception.Message}");
            return null;
        }
    }
}
