using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeveloperCli.Installation;
using JetBrains.Annotations;
using Spectre.Console;

namespace DeveloperCli.Utilities;

public sealed record ScalewayCredentials(string AccessKey, string SecretKey, string Source);

/// <summary>
///     Resolves a Scaleway environment from its name and an API key alone: the prefix comes from the
///     committed tier file, the project is found by name (&lt;prefix&gt;-&lt;environment&gt;), and the
///     organization from the project. The bootstrap and deploy commands share it, so neither needs
///     project ids, organization ids, or a local bootstrap state.
/// </summary>
public static partial class ScalewayEnvironment
{
    public const string Region = "fr-par";

    private const string EmailOffer = "essential";

    // Aspire environment name -> Terraform environment name (cloud-infrastructure/scaleway).
    // Production is added here when that environment is provisioned.
    private static readonly Dictionary<string, string> TerraformEnvironmentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["staging"] = "stage"
    };

    [GeneratedRegex("""^prefix\s*=\s*"([a-z0-9]{2,6})"\s*$""", RegexOptions.Multiline)]
    private static partial Regex PrefixPattern { get; }

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
    ///     The API key to act with: SCW_ACCESS_KEY/SCW_SECRET_KEY when exported (CI), otherwise the
    ///     named scw CLI profile, or the active one when <paramref name="profile" /> is null.
    /// </summary>
    public static async Task<ScalewayCredentials> ReadCredentialsAsync(string? profile)
    {
        var accessKey = Environment.GetEnvironmentVariable("SCW_ACCESS_KEY");
        var secretKey = Environment.GetEnvironmentVariable("SCW_SECRET_KEY");
        if (!string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(secretKey))
        {
            return new ScalewayCredentials(accessKey, secretKey, "SCW_ACCESS_KEY/SCW_SECRET_KEY");
        }

        var profileArgument = profile is null ? "" : $"-p {profile} ";
        var profileAccessKey = await ProcessHelper.ExecuteSensitiveAsync($"scw {profileArgument}config get access-key");
        var profileSecretKey = await ProcessHelper.ExecuteSensitiveAsync($"scw {profileArgument}config get secret-key");
        if (profileAccessKey.Success && profileSecretKey.Success && profileAccessKey.StdOut.Trim().StartsWith("SCW") && profileSecretKey.StdOut.Trim().Length > 0)
        {
            return new ScalewayCredentials(profileAccessKey.StdOut.Trim(), profileSecretKey.StdOut.Trim(), profile is null ? "the active scw profile" : $"the scw profile '{profile}'");
        }

        var hint = profile is null
            ? "Export SCW_ACCESS_KEY and SCW_SECRET_KEY, or configure the scw CLI (scw init)."
            : $"Export SCW_ACCESS_KEY and SCW_SECRET_KEY, or run the bootstrap command, which writes the '{profile}' scw profile.";
        AnsiConsole.MarkupLine($"[red]No Scaleway API key found.[/] {hint}");
        Environment.Exit(1);
        return null!;
    }

    /// <summary>Who owns the key (a user, or an IAM application) and its preferred project.</summary>
    public static async Task<ApiKey> GetApiKeyAsync(ScalewayCredentials credentials)
    {
        return await GetAsync<ApiKey>(credentials, $"iam/v1alpha1/api-keys/{credentials.AccessKey}", "read the API key's owner (does the key have IAM read access?)");
    }

    public static async Task<string> GetOrganizationIdAsync(ScalewayCredentials credentials, string projectId)
    {
        return (await GetAsync<Project>(credentials, $"account/v3/projects/{projectId}", "read the key's project")).OrganizationId;
    }

    /// <summary>The project named exactly <paramref name="name" />, or null when it does not exist yet.</summary>
    public static async Task<string?> FindProjectIdAsync(ScalewayCredentials credentials, string organizationId, string name)
    {
        var projects = await GetAsync<ProjectPage>(credentials, $"account/v3/projects?organization_id={organizationId}&name={Uri.EscapeDataString(name)}&page_size=100", "list the organization's projects");
        return projects.Projects.SingleOrDefault(project => project.Name == name)?.Id;
    }

    /// <summary>
    ///     Subscribes the project to Transactional Email's free Essential offer when it has no offer
    ///     yet; email domains are refused without one ("No active offer subscription for the
    ///     project"), and no Terraform resource manages it. An existing offer is never changed.
    /// </summary>
    public static async Task EnsureEmailOfferAsync(ScalewayCredentials credentials, string projectId)
    {
        var offersPath = $"transactional-email/v1alpha1/regions/{Region}/offer-subscriptions";
        var subscriptions = await SendAsync<OfferSubscriptionPage>(credentials, HttpMethod.Get, $"{offersPath}?project_id={projectId}", null, "read the project's Transactional Email offer");
        if (subscriptions.TotalCount > 0)
        {
            AnsiConsole.MarkupLine("[green]The project already has a Transactional Email offer.[/]");
            return;
        }

        await SendAsync<JsonElement>(credentials, HttpMethod.Patch, offersPath, new { project_id = projectId, name = EmailOffer }, "subscribe the project to Transactional Email's Essential offer");
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

    private static Task<T> GetAsync<T>(ScalewayCredentials credentials, string path, string action)
    {
        return SendAsync<T>(credentials, HttpMethod.Get, path, null, action);
    }

    private static async Task<T> SendAsync<T>(ScalewayCredentials credentials, HttpMethod method, string path, object? body, string action)
    {
        using var httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri("https://api.scaleway.com/");
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        httpClient.DefaultRequestHeaders.Add("X-Auth-Token", credentials.SecretKey);

        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var hint = credentials.Source.StartsWith("SCW_")
                ? " Exported variables take precedence over the scw profiles; unset SCW_ACCESS_KEY and SCW_SECRET_KEY to use the profile instead."
                : "";
            AnsiConsole.MarkupLine($"[red]The key {Markup.Escape(credentials.AccessKey)} from {Markup.Escape(credentials.Source)} is not valid[/] (revoked, expired, or mistyped).{Markup.Escape(hint)}");
            Environment.Exit(1);
        }

        if (!response.IsSuccessStatusCode)
        {
            AnsiConsole.MarkupLine($"[red]Could not {Markup.Escape(action)}:[/] HTTP {(int)response.StatusCode} with the key from {Markup.Escape(credentials.Source)}.");
            Environment.Exit(1);
        }

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public sealed record ApiKey(
        [property: JsonPropertyName("user_id")]
        string? UserId,
        [property: JsonPropertyName("application_id")]
        string? ApplicationId,
        [property: JsonPropertyName("default_project_id")]
        string DefaultProjectId
    );

    [UsedImplicitly]
    private sealed record Project(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("organization_id")]
        string OrganizationId
    );

    [UsedImplicitly]
    private sealed record OfferSubscriptionPage(
        [property: JsonPropertyName("total_count")]
        int TotalCount
    );

    [UsedImplicitly]
    private sealed record ProjectPage(
        [property: JsonPropertyName("projects")]
        Project[] Projects
    );
}
