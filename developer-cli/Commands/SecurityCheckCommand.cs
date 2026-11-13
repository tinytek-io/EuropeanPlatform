using System.CommandLine;
using DeveloperCli.Commands.SecurityCheck;
using DeveloperCli.Installation;
using DeveloperCli.Utilities;
using Scaleway.Sdk;
using Spectre.Console;

namespace DeveloperCli.Commands;

/// <summary>
///     Verifies the guarantees that keep the edge WAF from being bypassed: every gateway Load Balancer frontend admits
///     Edge Services only, no other Load Balancer in the project is public, and every edge pipeline blocks with its
///     WAF and routes all traffic through it. Read-only. The deploy runs it last, after the pass that re-applies the
///     edge-only ACLs to frontends the rollout recreated, and fails on a finding.
/// </summary>
public sealed class SecurityCheckCommand : Command
{
    public SecurityCheckCommand() : base(
        "security-check",
        "Verify that nothing reaches the cluster around the edge WAF: edge-only Load Balancer frontends, no other public Load Balancer, and blocking WAF stages (read-only; acts with the environment's operator scw profile)."
    )
    {
        var environmentOption = new Option<string>("--environment", "-e")
        {
            Description = "The environment to check (staging)",
            DefaultValueFactory = _ => "staging"
        };
        Options.Add(environmentOption);

        SetAction(parseResult => ExecuteAsync(parseResult.GetValue(environmentOption)!).GetAwaiter().GetResult());
    }

    /// <summary>
    ///     Reads the inventory, prints one line per check, and returns the number of failed checks. Shared with the
    ///     deploy, which has connected to the environment already, so the Terraform outputs are readable.
    /// </summary>
    public static async Task<int> PrintSecurityReportAsync(ScalewayCredentials credentials, string organizationId, string projectId)
    {
        var gatewayLoadBalancerId = await TerraformHelper.OutputAsync(ScalewayEnvironment.EnvironmentModuleFolder, "gateway_lb_ref");

        using var scaleway = ScalewayEnvironment.CreateClient(credentials, organizationId);
        var project = scaleway.WithProject(ProjectId.Parse(projectId));
        var reader = new SecurityInventoryReader(project.LoadBalancers, project.EdgeServices, projectId, ScalewayEnvironment.Region);

        SecurityInventory inventory;
        try
        {
            inventory = await reader.ReadAsync(gatewayLoadBalancerId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is ScalewayApiException or ScalewayTransientException)
        {
            AnsiConsole.MarkupLine($"[red]Could not read the project's Load Balancers and edge pipelines:[/] {Markup.Escape(exception.Message)}");
            return 1;
        }

        var failures = 0;
        foreach (var result in SecurityCheckEvaluator.Evaluate(inventory))
        {
            if (result.Passed)
            {
                AnsiConsole.MarkupLine($"  [green]✓[/] {result.Name}");
                continue;
            }

            failures++;
            AnsiConsole.MarkupLine($"  [red]✗[/] {result.Name}");
            foreach (var finding in result.Findings)
            {
                AnsiConsole.MarkupLine($"      {Markup.Escape(finding)}");
            }
        }

        AnsiConsole.MarkupLine(failures == 0
            ? "[green]Every security check passed.[/]"
            : $"[red]{failures} security check(s) failed.[/] Each finding names the resource that lets traffic around the edge WAF."
        );

        return failures;
    }

    private static async Task ExecuteAsync(string environment)
    {
        Prerequisite.Ensure(Prerequisite.Terraform);

        var resolved = await ScalewayEnvironment.ConnectAsync(environment);
        if (await PrintSecurityReportAsync(resolved.Credentials, resolved.OrganizationId, resolved.ProjectId) > 0)
        {
            Environment.Exit(1);
        }
    }
}
