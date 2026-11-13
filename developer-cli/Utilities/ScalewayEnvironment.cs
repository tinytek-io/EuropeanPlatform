using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DeveloperCli.Installation;
using Scaleway.Sdk;
using Spectre.Console;
using ScwAccount = Scaleway.Sdk.Account.V3;
using ScwIam = Scaleway.Sdk.Iam.V1Alpha1;
using ScwTem = Scaleway.Sdk.Tem.V1Alpha1;

namespace DeveloperCli.Utilities;

public sealed record ScalewayCredentials(string AccessKey, string SecretKey, string Source);

public sealed record ResolvedEnvironment(string TerraformEnvironment, string ProjectName, string OrganizationId, string ProjectId, ScalewayCredentials Credentials);

/// <summary>
///     Resolves a Scaleway environment from its name and an API key alone: the prefix comes from the
///     committed tier file, the project is found by name (&lt;prefix&gt;-&lt;environment&gt;), and the
///     organization from the project. The bootstrap and deploy commands share it, so neither needs
///     project ids, organization ids, or a local bootstrap state.
/// </summary>
public static partial class ScalewayEnvironment
{
    public const string Region = "fr-par";

    private static readonly ScwTem.OfferSubscriptionName EmailOffer = ScwTem.OfferSubscriptionName.Essential;

    // Aspire environment name -> Terraform environment name (cloud-infrastructure/scaleway).
    // Production is added here when that environment is provisioned.
    private static readonly Dictionary<string, string> TerraformEnvironmentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["staging"] = "stage"
    };

    [GeneratedRegex("""^prefix\s*=\s*"([a-z0-9]{2,6})"\s*$""", RegexOptions.Multiline)]
    private static partial Regex PrefixPattern { get; }

    [GeneratedRegex(@"^monthly_budget_eur\s*=\s*([0-9]+(?:\.[0-9]+)?)\s*(?:#.*)?$", RegexOptions.Multiline)]
    private static partial Regex MonthlyBudgetPattern { get; }

    [GeneratedRegex("""^environment_type\s*=\s*"([a-z]+)"\s*(?:#.*)?$""", RegexOptions.Multiline)]
    private static partial Regex EnvironmentTypePattern { get; }

    public static string ScalewayFolder => Path.Combine(Configuration.SourceCodeFolder, "cloud-infrastructure", "scaleway");

    public static string EnvironmentModuleFolder => Path.Combine(ScalewayFolder, "environment");

    public static string BootstrapModuleFolder => Path.Combine(ScalewayFolder, "bootstrap");

    public static string TerraformNameOf(string environment)
    {
        if (TerraformEnvironmentNames.TryGetValue(environment, out var terraformEnvironment)) return terraformEnvironment;

        AnsiConsole.MarkupLine($"[red]Unknown environment '{Markup.Escape(environment)}'.[/] Known environments: {string.Join(", ", TerraformEnvironmentNames.Keys)}.");
        Environment.Exit(1);
        return null!;
    }

    /// <summary>The environment's committed tier file, the single source of its prefix and configuration.</summary>
    public static string TierFileOf(string terraformEnvironment)
    {
        var tierFile = Path.Combine(EnvironmentModuleFolder, "environments", $"{terraformEnvironment}.tfvars");
        if (File.Exists(tierFile)) return tierFile;

        AnsiConsole.MarkupLine($"[red]No tier configuration found at {Markup.Escape(tierFile)}.[/] Every environment must declare its tier file (see environments/stage.tfvars).");
        Environment.Exit(1);
        return null!;
    }

    /// <summary>The partial backend configuration (state bucket and endpoint) both modules share.</summary>
    public static string BackendConfigurationOf(string terraformEnvironment)
    {
        var backendConfiguration = Path.Combine(EnvironmentModuleFolder, "environments", $"{terraformEnvironment}.backend.hcl");
        if (File.Exists(backendConfiguration)) return backendConfiguration;

        AnsiConsole.MarkupLine($"[red]No backend configuration found at {Markup.Escape(backendConfiguration)}.[/] Every environment declares its state bucket there (see environments/stage.backend.hcl).");
        Environment.Exit(1);
        return null!;
    }

    /// <summary>
    ///     The tier file's monthly_budget_eur, overridden by the operator's git-ignored &lt;environment&gt;.local.tfvars
    ///     when that sets one (the deploy layers it over the tier file the same way).
    /// </summary>
    public static decimal ReadMonthlyBudget(string terraformEnvironment)
    {
        var tierFile = TierFileOf(terraformEnvironment);
        var localOverrides = Path.Combine(EnvironmentModuleFolder, "environments", $"{terraformEnvironment}.local.tfvars");
        var budget = new[] { tierFile, localOverrides }
            .Where(File.Exists)
            .Select(file => MonthlyBudgetPattern.Match(File.ReadAllText(file)))
            .Where(match => match.Success)
            .Select(match => decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .LastOrDefault();
        if (budget > 0) return budget;

        AnsiConsole.MarkupLine($"[red]{Markup.Escape(tierFile)} sets no monthly_budget_eur.[/] Add a line like monthly_budget_eur = 100 (euros per month, excluding VAT).");
        Environment.Exit(1);
        return 0;
    }

    /// <summary>
    ///     The tier file's environment_type (review, test, staging or production), overridden by the operator's
    ///     git-ignored &lt;environment&gt;.local.tfvars when that sets one. Terraform validates the value itself.
    /// </summary>
    public static string ReadEnvironmentType(string terraformEnvironment)
    {
        var tierFile = TierFileOf(terraformEnvironment);
        var localOverrides = Path.Combine(EnvironmentModuleFolder, "environments", $"{terraformEnvironment}.local.tfvars");
        var environmentType = new[] { tierFile, localOverrides }
            .Where(File.Exists)
            .Select(file => EnvironmentTypePattern.Match(File.ReadAllText(file)))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .LastOrDefault();
        if (environmentType is not null) return environmentType;

        AnsiConsole.MarkupLine($"[red]{Markup.Escape(tierFile)} sets no environment_type.[/] Add a line like environment_type = \"staging\" (review, test, staging or production).");
        Environment.Exit(1);
        return null!;
    }

    public static string ReadPrefix(string terraformEnvironment)
    {
        var tierFile = TierFileOf(terraformEnvironment);
        var match = PrefixPattern.Match(File.ReadAllText(tierFile));
        if (match.Success) return match.Groups[1].Value;

        AnsiConsole.MarkupLine($"[red]{Markup.Escape(tierFile)} sets no prefix.[/] Add a line like prefix = \"pp\" (2-6 lowercase letters or digits).");
        Environment.Exit(1);
        return null!;
    }

    /// <summary>
    ///     Resolves the environment's key and project from its name alone. The key is the environment's
    ///     operator (the scw profile the bootstrap command writes) unless SCW_ACCESS_KEY/SCW_SECRET_KEY
    ///     are exported, as in CI.
    /// </summary>
    public static async Task<ResolvedEnvironment> ResolveAsync(string environment)
    {
        var terraformEnvironment = TerraformNameOf(environment);
        var projectName = $"{ReadPrefix(terraformEnvironment)}-{terraformEnvironment}";
        var credentials = ReadCredentials(projectName);
        var apiKey = await GetApiKeyAsync(credentials);
        var organizationId = await GetOrganizationIdAsync(credentials, apiKey.DefaultProjectId);
        var projectId = await FindProjectIdAsync(credentials, organizationId, projectName);
        if (projectId is null)
        {
            AnsiConsole.MarkupLine($"[red]No Scaleway project named '{projectName}' exists.[/] Create the environment first: dotnet run --project developer-cli -- bootstrap -e {environment}");
            Environment.Exit(1);
        }

        AnsiConsole.MarkupLine($"Acting with the key from {Markup.Escape(credentials.Source)} in project [cyan]{projectName}[/].");
        return new ResolvedEnvironment(terraformEnvironment, projectName, organizationId, projectId, credentials);
    }

    /// <summary>
    ///     Resolves the environment, hands its key to Terraform, and initializes the environment
    ///     module's remote state, so any command can read or change the environment with nothing exported.
    /// </summary>
    public static async Task<ResolvedEnvironment> ConnectAsync(string environment)
    {
        var resolved = await ResolveAsync(environment);
        ExportForTerraform(resolved.Credentials, resolved.OrganizationId, resolved.ProjectId);

        var backendConfiguration = BackendConfigurationOf(resolved.TerraformEnvironment);
        await TerraformHelper.RunSensitiveAsync(EnvironmentModuleFolder, $"init -reconfigure -input=false -no-color -backend-config=\"{backendConfiguration}\"", "initialize the environment module's remote state (has the bootstrap run, and may this key read the state bucket?)");
        return resolved;
    }

    /// <summary>
    ///     The API key to act with: SCW_ACCESS_KEY/SCW_SECRET_KEY when exported (CI), otherwise the
    ///     named profile in the Scaleway config file (the one scw and Terraform read), or the active one
    ///     when <paramref name="profile" /> is null.
    /// </summary>
    public static ScalewayCredentials ReadCredentials(string? profile)
    {
        var accessKey = Environment.GetEnvironmentVariable("SCW_ACCESS_KEY");
        var secretKey = Environment.GetEnvironmentVariable("SCW_SECRET_KEY");
        if (!string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(secretKey))
        {
            return new ScalewayCredentials(accessKey, secretKey, "SCW_ACCESS_KEY/SCW_SECRET_KEY");
        }

        var profileKey = ScalewayConfigFile.ReadProfile(profile);
        if (profileKey?.AccessKey?.StartsWith("SCW") == true && !string.IsNullOrWhiteSpace(profileKey.SecretKey))
        {
            return new ScalewayCredentials(profileKey.AccessKey, profileKey.SecretKey, profile is null ? "the active scw profile" : $"the scw profile '{profile}'");
        }

        var hint = profile is null
            ? $"Export SCW_ACCESS_KEY and SCW_SECRET_KEY, or add an API key to {ScalewayConfigFile.DefaultPath} (scw init writes one)."
            : $"Export SCW_ACCESS_KEY and SCW_SECRET_KEY, or run the bootstrap command, which writes the '{profile}' profile to {ScalewayConfigFile.DefaultPath}.";
        AnsiConsole.MarkupLine($"[red]No Scaleway API key found.[/] {Markup.Escape(hint)}");
        Environment.Exit(1);
        return null!;
    }

    /// <summary>
    ///     The generated SDK's client for a resolved environment, for the commands that act inside its project
    ///     (<c>CreateClient(...).WithProject(...)</c>). Unlike the lookups below it fails with the SDK's own exceptions.
    /// </summary>
    public static ScalewayClient CreateClient(ScalewayCredentials credentials, string organizationId)
    {
        return new ScalewayClient(new ScalewayCredentialConfig
            {
                AccessKey = credentials.AccessKey, SecretKey = credentials.SecretKey, OrganizationId = OrganizationId.Parse(organizationId)
            }
        );
    }

    /// <summary>Who owns the key (a user, or an IAM application) and its preferred project.</summary>
    public static Task<ScwIam.APIKey> GetApiKeyAsync(ScalewayCredentials credentials)
    {
        return CallAsync(credentials, "read the API key's owner (does the key have IAM read access?)",
            async transport => await new ScwIam.IamApi(transport).GetApiKeyAsync(credentials.AccessKey)
        );
    }

    public static async Task<string> GetOrganizationIdAsync(ScalewayCredentials credentials, string? projectId)
    {
        if (projectId is null)
        {
            AnsiConsole.MarkupLine($"[red]The key from {Markup.Escape(credentials.Source)} has no default project,[/] so its organization cannot be found. Set one on the key in the Scaleway console.");
            Environment.Exit(1);
        }

        var project = await CallAsync(credentials, "read the key's project", async transport => await new ScwAccount.AccountApi(transport).GetProjectAsync(projectId));
        return project.OrganizationId!;
    }

    /// <summary>The project named exactly <paramref name="name" />, or null when it does not exist yet.</summary>
    public static async Task<string?> FindProjectIdAsync(ScalewayCredentials credentials, string organizationId, string name)
    {
        var projects = await CallAsync(credentials, "list the organization's projects",
            async transport => await new ScwAccount.AccountApi(transport).ListProjectsAsync(new ScwAccount.ListProjectsRequest { OrganizationId = organizationId, Name = name })
        );

        // The name filter is a prefix match upstream, so the exact name is checked here.
        return projects.SingleOrDefault(project => project.Name == name)?.Id;
    }

    /// <summary>
    ///     Subscribes the project to Transactional Email's free Essential offer when it has no offer
    ///     yet; email domains are refused without one ("No active offer subscription for the
    ///     project"), and no Terraform resource manages it. An existing offer is never changed.
    /// </summary>
    public static async Task EnsureEmailOfferAsync(ScalewayCredentials credentials, string projectId)
    {
        var subscriptions = await CallAsync(credentials, "read the project's Transactional Email offer",
            async transport => await new ScwTem.TemApi(transport).ListOfferSubscriptionsAsync(new ScwTem.ListOfferSubscriptionsRequest { ProjectId = projectId }, Region)
        );
        if (subscriptions.OfferSubscriptions is { Count: > 0 })
        {
            AnsiConsole.MarkupLine("[green]The project already has a Transactional Email offer.[/]");
            return;
        }

        await CallAsync(credentials, "subscribe the project to Transactional Email's Essential offer",
            async transport => await new ScwTem.TemApi(transport).UpdateOfferSubscriptionAsync(new ScwTem.UpdateOfferSubscriptionRequest { ProjectId = projectId, Name = EmailOffer }, Region)
        );
        AnsiConsole.MarkupLine($"[green]Subscribed the project to Transactional Email's '{EmailOffer}' offer[/] (free tier, no commitment).");
    }

    /// <summary>
    ///     Hands the key to every Terraform process this command starts: the Scaleway provider reads
    ///     SCW_*, and the S3 state backend reads AWS_*. Object Storage acts in the key's preferred
    ///     project unless the access key carries "@&lt;project-id&gt;", which points it at the
    ///     environment's state bucket whatever the key's preferred project is.
    /// </summary>
    public static void ExportForTerraform(ScalewayCredentials credentials, string organizationId, string? projectId)
    {
        Environment.SetEnvironmentVariable("SCW_ACCESS_KEY", credentials.AccessKey);
        Environment.SetEnvironmentVariable("SCW_SECRET_KEY", credentials.SecretKey);
        Environment.SetEnvironmentVariable("SCW_DEFAULT_ORGANIZATION_ID", organizationId);
        Environment.SetEnvironmentVariable("SCW_DEFAULT_REGION", Region);
        Environment.SetEnvironmentVariable("SCW_DEFAULT_PROJECT_ID", projectId);
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", projectId is null ? credentials.AccessKey : $"{credentials.AccessKey}@{projectId}");
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", credentials.SecretKey);
    }

    // One SDK transport per call, built from the key alone: the first calls are the ones that find the organization,
    // so nothing here can require it. A rejected key and any other failure end the command with a readable reason.
    private static async Task<T> CallAsync<T>(ScalewayCredentials credentials, string action, Func<ScalewayApiClient, Task<T?>> call)
    {
        using var httpClient = ScalewayApiClient.ConfigureHttpClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, credentials.SecretKey);
        using var transport = new ScalewayApiClient(httpClient);

        try
        {
            var result = await call(transport);
            if (result is not null) return result;

            AnsiConsole.MarkupLine($"[red]Could not {Markup.Escape(action)}:[/] not found with the key from {Markup.Escape(credentials.Source)}.");
        }
        catch (ScalewayApiException exception) when (exception.StatusCode == (int)HttpStatusCode.Unauthorized)
        {
            var hint = credentials.Source.StartsWith("SCW_")
                ? " Exported variables take precedence over the scw profiles; unset SCW_ACCESS_KEY and SCW_SECRET_KEY to use the profile instead."
                : "";
            AnsiConsole.MarkupLine($"[red]The key {Markup.Escape(credentials.AccessKey)} from {Markup.Escape(credentials.Source)} is not valid[/] (revoked, expired, or mistyped).{Markup.Escape(hint)}");
        }
        catch (ScalewayApiException exception)
        {
            AnsiConsole.MarkupLine($"[red]Could not {Markup.Escape(action)}:[/] HTTP {exception.StatusCode} with the key from {Markup.Escape(credentials.Source)}.");
        }

        Environment.Exit(1);
        return default!;
    }
}
