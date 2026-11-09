using System.CommandLine;
using DeveloperCli.Installation;
using DeveloperCli.Utilities;
using Spectre.Console;

namespace DeveloperCli.Commands;

/// <summary>
///     Creates or updates an environment's foundation (the bootstrap module): its Scaleway project,
///     the Terraform state bucket, the registry, and the operator, deploy pipeline and registry-pull
///     identities. Safe to run at any time, from any machine: the bootstrap's own state lives in the
///     environment's state bucket, and every run is a reviewed plan.
/// </summary>
/// <remarks>
///     Runs with an administrator's personal API key (the active scw profile, or SCW_ACCESS_KEY and
///     SCW_SECRET_KEY): it creates projects and IAM, which an environment's operator may not. At the
///     end it writes the operator's key into the scw profile &lt;prefix&gt;-&lt;environment&gt;, which
///     the deploy command then uses.
/// </remarks>
public sealed class BootstrapCommand : Command
{
    public BootstrapCommand() : base("bootstrap", "Create or update an environment's Scaleway foundation (project, state bucket, registry, operator identity) and write the operator's scw profile")
    {
        var environmentOption = new Option<string>("--environment", "-e")
        {
            Description = "The environment to bootstrap (staging)",
            DefaultValueFactory = _ => "staging"
        };
        var autoApproveOption = new Option<bool>("--auto-approve")
        {
            Description = "Apply the bootstrap's pending changes without asking"
        };

        Options.Add(environmentOption);
        Options.Add(autoApproveOption);

        SetAction(parseResult => Execute(parseResult.GetValue(environmentOption)!, parseResult.GetValue(autoApproveOption)).GetAwaiter().GetResult());
    }

    private static async Task Execute(string environment, bool autoApprove)
    {
        Prerequisite.Ensure(Prerequisite.Terraform);

        var terraformEnvironment = ScalewayEnvironment.TerraformNameOf(environment);
        var prefix = ScalewayEnvironment.ReadPrefix(terraformEnvironment);
        var backendConfiguration = ScalewayEnvironment.BackendConfigurationOf(terraformEnvironment);
        var projectName = $"{prefix}-{terraformEnvironment}";
        var moduleFolder = ScalewayEnvironment.BootstrapModuleFolder;

        // The bootstrap's state before it is in the bucket.
        var localStateFileName = $"terraform.{terraformEnvironment}.tfstate";
        var localState = Path.Combine(moduleFolder, localStateFileName);

        AnsiConsole.Write(new Rule($"[bold] Bootstrapping {environment} ({projectName}) [/]"));

        var administrator = await ScalewayEnvironment.ReadCredentialsAsync(null);
        var administratorKey = await ScalewayEnvironment.GetApiKeyAsync(administrator);
        if (administratorKey.UserId is null)
        {
            AnsiConsole.MarkupLine($"[red]The key from {Markup.Escape(administrator.Source)} belongs to an IAM application, not a user.[/] The bootstrap creates projects and IAM, so it runs with an administrator's personal key.");
            Environment.Exit(1);
        }

        var organizationId = await ScalewayEnvironment.GetOrganizationIdAsync(administrator, administratorKey.DefaultProjectId);
        var projectId = await ScalewayEnvironment.FindProjectIdAsync(administrator, organizationId, projectName);
        var planArguments = $"-var prefix={prefix} -var environment={terraformEnvironment} -var administrator_user_id={administratorKey.UserId} -var organization_id={organizationId}";

        // A local state means the bucket may not exist yet: a first run, a first run that stopped
        // before the bucket was created, or a state from before the bootstrap kept it in the bucket.
        // Each finishes the same way: apply with the local state, then move it into the bucket.
        if (projectId is null || File.Exists(localState))
        {
            AnsiConsole.MarkupLine(projectId is null
                ? $"No project named [cyan]{projectName}[/] exists: creating the environment."
                : $"Continuing from the local bootstrap state [cyan]{Markup.Escape(localStateFileName)}[/]; it moves into the state bucket once the bucket exists."
            );
            ScalewayEnvironment.ExportForTerraform(administrator, organizationId, projectId);
            projectId = await ApplyWithLocalStateAsync(moduleFolder, localState, planArguments, autoApprove);
            ScalewayEnvironment.ExportForTerraform(administrator, organizationId, projectId);
            await InitializeRemoteStateAsync(moduleFolder, backendConfiguration);
            if (await HasRemoteStateAsync(moduleFolder))
            {
                AnsiConsole.MarkupLine($"[red]The state bucket already holds a bootstrap state, and so does {Markup.Escape(localState)}.[/] Compare the two and remove the stale one; the bootstrap never overwrites a state in the bucket.");
                Environment.Exit(1);
            }

            await MoveLocalStateToBucketAsync(moduleFolder, localState);
        }
        else
        {
            ScalewayEnvironment.ExportForTerraform(administrator, organizationId, projectId);
            await InitializeRemoteStateAsync(moduleFolder, backendConfiguration);

            if (!await HasRemoteStateAsync(moduleFolder))
            {
                AnsiConsole.MarkupLine($"[red]The project '{projectName}' exists, but no bootstrap state was found[/] (neither in its state bucket nor at {Markup.Escape(localState)}). Applying now would try to create everything again. Copy the environment's bootstrap state to that path, and run again to move it into the bucket.");
                Environment.Exit(1);
            }

            await TerraformHelper.ApplyChangesAsync(moduleFolder, planArguments, autoApprove, "Bootstrap cancelled; nothing was changed.");
        }

        await ScalewayEnvironment.EnsureEmailOfferAsync(administrator, projectId);
        await WriteOperatorProfileAsync(moduleFolder, projectName, organizationId, projectId);
    }

    /// <summary>
    ///     Applies with a local state file (a temporary backend override), for when the state bucket
    ///     may not exist yet, and returns the environment's project id.
    /// </summary>
    private static async Task<string> ApplyWithLocalStateAsync(string moduleFolder, string localState, string planArguments, bool autoApprove)
    {
        var overrideFile = Path.Combine(moduleFolder, "first_run_override.tf");
        await File.WriteAllTextAsync(overrideFile, $"terraform {{\n  backend \"local\" {{\n    path = \"{Path.GetFileName(localState)}\"\n  }}\n}}\n");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => File.Delete(overrideFile);
        try
        {
            await TerraformHelper.RunSensitiveAsync(moduleFolder, "init -reconfigure -input=false -no-color", "initialize the bootstrap with local state");
            await TerraformHelper.ApplyChangesAsync(moduleFolder, planArguments, autoApprove, "Bootstrap cancelled; nothing was changed.");
            return await TerraformHelper.OutputAsync(moduleFolder, "environment_project_id");
        }
        finally
        {
            File.Delete(overrideFile);
        }
    }

    private static async Task InitializeRemoteStateAsync(string moduleFolder, string backendConfiguration)
    {
        await TerraformHelper.RunSensitiveAsync(moduleFolder, $"init -reconfigure -input=false -no-color -backend-config={backendConfiguration}", "initialize the bootstrap's state in the environment's state bucket");
    }

    private static async Task<bool> HasRemoteStateAsync(string moduleFolder)
    {
        // An empty remote state makes `state list` fail with "No state file was found!" instead of
        // listing nothing.
        var result = await ProcessHelper.ExecuteSensitiveAsync("terraform state list -no-color", moduleFolder);
        if (result.Success) return !string.IsNullOrWhiteSpace(result.StdOut);
        if (result.StdErr.Contains("No state file was found")) return false;

        AnsiConsole.MarkupLine("[red]terraform state list failed:[/] " + Markup.Escape(result.StdErr.Trim()));
        Environment.Exit(result.ExitCode);
        return false;
    }

    /// <summary>
    ///     Pushes a local bootstrap state into the (empty) remote state and keeps the local file as
    ///     a backup next to it, renamed so no later run mistakes it for the live state.
    /// </summary>
    private static async Task MoveLocalStateToBucketAsync(string moduleFolder, string localState)
    {
        await TerraformHelper.RunSensitiveAsync(moduleFolder, $"state push {localState}", "move the local bootstrap state into the state bucket");
        var backup = Path.ChangeExtension(localState, ".moved-to-bucket.tfstate");
        File.Move(localState, backup);
        AnsiConsole.MarkupLine($"[green]Bootstrap state moved into the state bucket.[/] The local copy is kept as {Markup.Escape(Path.GetFileName(backup))} (git-ignored); delete it once you trust the bucket copy.");
    }

    /// <summary>
    ///     Writes the operator's key into the scw profile the deploy command reads, so day-to-day
    ///     deploys never use the administrator's key. Rewritten only when the key changed (a rotation).
    /// </summary>
    private static async Task WriteOperatorProfileAsync(string moduleFolder, string profile, string organizationId, string projectId)
    {
        var accessKey = await TerraformHelper.OutputAsync(moduleFolder, "operator_access_key");
        var current = await ProcessHelper.ExecuteSensitiveAsync($"scw -p {profile} config get access-key");
        if (current.Success && current.StdOut.Trim() == accessKey)
        {
            AnsiConsole.MarkupLine($"[green]The scw profile '{profile}' already holds the operator's key.[/]");
            return;
        }

        var secretKey = await TerraformHelper.OutputAsync(moduleFolder, "operator_secret_key");
        var result = await ProcessHelper.ExecuteSensitiveAsync(
            $"scw -p {profile} config set access-key={accessKey} secret-key={secretKey} default-organization-id={organizationId} default-project-id={projectId} default-region={ScalewayEnvironment.Region} default-zone={ScalewayEnvironment.Region}-1"
        );
        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]Could not write the scw profile '{profile}':[/] {Markup.Escape(result.StdErr.Trim())} Is the scw CLI installed?");
            Environment.Exit(1);
        }

        AnsiConsole.MarkupLine($"[green]The scw profile '{profile}' now holds the operator's key;[/] deploys use it automatically.");
    }
}
