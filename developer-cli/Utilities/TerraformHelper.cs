using System.Text.RegularExpressions;
using Spectre.Console;

namespace DeveloperCli.Utilities;

/// <summary>Terraform steps the bootstrap and deploy commands share.</summary>
public static partial class TerraformHelper
{
    // A resource line of `terraform plan` output: "  # <address> will be created", "must be replaced",
    // "has moved to <address>", and so on.
    [GeneratedRegex(@"^\s*# (.+?) (will be|must be|has moved to) (.+)$")]
    private static partial Regex PlanChangePattern { get; }

    /// <summary>
    ///     Runs a Terraform command whose output can carry secrets (outputs, state), captured in memory
    ///     only; a failure reports stderr alone and exits.
    /// </summary>
    public static async Task<string> RunSensitiveAsync(string moduleFolder, string arguments, string failureHint)
    {
        var result = await ProcessHelper.ExecuteSensitiveAsync($"terraform {arguments}", moduleFolder);
        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]terraform {Markup.Escape(arguments)} failed:[/] " + Markup.Escape(result.StdErr.Trim()));
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(failureHint)}[/]");
            Environment.Exit(result.ExitCode);
        }

        return result.StdOut;
    }

    public static async Task<string> OutputAsync(string moduleFolder, string name)
    {
        return (await RunSensitiveAsync(moduleFolder, $"output -raw {name}", $"read the {name} output")).Trim();
    }

    /// <summary>
    ///     Reads an output the state may not have yet (one a newer module version added and no apply has recorded), as
    ///     null. Any other failure reports and exits like <see cref="OutputAsync" />.
    /// </summary>
    public static async Task<string?> TryOutputAsync(string moduleFolder, string name)
    {
        var result = await ProcessHelper.ExecuteSensitiveAsync($"terraform output -raw {name}", moduleFolder);
        if (result.Success) return result.StdOut.Trim();
        if (result.StdErr.Contains($"Output \"{name}\" not found", StringComparison.Ordinal)) return null;

        AnsiConsole.MarkupLine($"[red]terraform output -raw {Markup.Escape(name)} failed:[/] " + Markup.Escape(result.StdErr.Trim()));
        Environment.Exit(result.ExitCode);
        return null;
    }

    /// <summary>
    ///     Plans the module and, when the plan has changes, lists them and applies exactly that plan
    ///     after confirmation (or right away with <paramref name="autoApprove" />). Without a terminal
    ///     and without auto-approve, pending changes stop the command.
    /// </summary>
    /// <param name="moduleFolder">The Terraform module to plan and apply.</param>
    /// <param name="planArguments">Arguments for terraform plan (var files and -var values).</param>
    /// <param name="autoApprove">Apply without asking.</param>
    /// <param name="declinedMessage">Printed when the changes are declined, before the command stops.</param>
    /// <param name="reviewPlan">
    ///     Given the saved plan as JSON (<c>terraform show -json</c>) before the confirmation, to judge what it would
    ///     do beyond the listed changes (the deploy prices it); it stops the command itself if the plan must not apply.
    /// </param>
    /// <returns>Whether the plan had changes.</returns>
    public static async Task<bool> ApplyChangesAsync(string moduleFolder, string planArguments, bool autoApprove, string declinedMessage, Func<string, Task>? reviewPlan = null)
    {
        // A saved plan holds every sensitive value in plain text, so it lives in an owner-only
        // folder that is removed even when the command exits early.
        var planFolder = Directory.CreateTempSubdirectory("pp-terraform-plan-");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteFolder(planFolder);
        var planFile = Path.Combine(planFolder.FullName, "module.tfplan");

        var plan = await ProcessHelper.ExecuteQuietlyAsync($"terraform plan -detailed-exitcode -input=false -no-color -out=\"{planFile}\" {planArguments}", moduleFolder);

        // -detailed-exitcode: 0 = no changes, 1 = error, 2 = changes present.
        if (plan.ExitCode == 0)
        {
            DeleteFolder(planFolder);
            AnsiConsole.MarkupLine("[green]Infrastructure matches the configuration.[/]");
            return false;
        }

        if (plan.ExitCode != 2)
        {
            AnsiConsole.MarkupLine("[red]terraform plan failed:[/] " + Markup.Escape(plan.GetErrorSummary("plan")));
            Environment.Exit(plan.ExitCode);
        }

        PrintPlanChanges(plan);

        if (reviewPlan is not null)
        {
            // The plan JSON carries every sensitive value in plain text, so it is held in memory only.
            await reviewPlan(await RunSensitiveAsync(moduleFolder, $"show -json \"{planFile}\"", "read the saved plan"));
        }

        if (!autoApprove)
        {
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]terraform plan reports pending changes and there is no terminal to confirm them.[/] Pass --auto-approve to apply them.");
                Environment.Exit(1);
            }

            if (!await AnsiConsole.ConfirmAsync("Apply these changes?", false))
            {
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(declinedMessage)}[/]");
                Environment.Exit(1);
            }
        }

        // Applying the saved plan applies exactly what was listed; if the infrastructure changed
        // since, Terraform refuses the stale plan instead of applying something else.
        ProcessHelper.StartProcess($"terraform apply -input=false \"{planFile}\"", moduleFolder);
        DeleteFolder(planFolder);
        AnsiConsole.MarkupLine("[green]Infrastructure changes applied.[/]");
        return true;
    }

    private static void PrintPlanChanges(ProcessResult plan)
    {
        var lines = plan.StdOut.Split('\n');
        foreach (var match in lines.Select(line => PlanChangePattern.Match(line)).Where(match => match.Success))
        {
            var change = $"{match.Groups[2].Value} {match.Groups[3].Value}";
            var color = change.Contains("destroyed") || change.Contains("replaced") ? "red" : "yellow";
            AnsiConsole.MarkupLine($"  [{color}]{Markup.Escape(change)}[/]  {Markup.Escape(match.Groups[1].Value)}");
        }

        var summary = lines.FirstOrDefault(line => line.StartsWith("Plan: ")) ?? "Only output values change.";
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(summary.Trim())}[/] [dim]Full plan: {Markup.Escape(plan.TempFilePathWithSize)}[/]");
    }

    private static void DeleteFolder(DirectoryInfo folder)
    {
        if (Directory.Exists(folder.FullName)) folder.Delete(true);
    }
}
