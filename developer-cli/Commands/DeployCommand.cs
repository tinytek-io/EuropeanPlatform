using System.CommandLine;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeveloperCli.Installation;
using DeveloperCli.Utilities;
using JetBrains.Annotations;
using Spectre.Console;

namespace DeveloperCli.Commands;

/// <summary>
///     Deploys an environment to Scaleway through the Aspire publish/deploy pipeline. Everything the
///     pipeline needs is derived, never hand-copied: the cluster kubeconfig and the per-workload
///     Secret Manager keys come from Terraform outputs, and a convergence check refuses to deploy over
///     infrastructure drift (the chart and the cloud must agree before containers roll out).
/// </summary>
/// <remarks>
///     Prerequisites beyond the CLI tools: the bootstrap and environment Terraform modules must be
///     applied (the command reads their state), and the Scaleway credentials for both modules must
///     be in the environment — SCW_ACCESS_KEY/SCW_SECRET_KEY/SCW_DEFAULT_PROJECT_ID for the Scaleway
///     provider and AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY for the S3 state backend (see
///     cloud-infrastructure/README.md).
/// </remarks>
public sealed partial class DeployCommand : Command
{
    private const string DefaultPrefix = "pp";

    /// <summary>
    ///     Deletes stale deploy tags from the Container Registry, keeping the newest few per image.
    ///     Every deploy pushes a full tag set; without pruning the registry fills its storage quota
    ///     and pushes start failing with HTTP 500 (found live: 24 deploys filled 18 GB before the
    ///     quota broke every subsequent deploy). The pipeline key's registry access authorizes the
    ///     prune, so it runs as the last step of every deploy.
    /// </summary>
    private const int RegistryTagsToKeep = 3;

    private const string RegistryApiBaseUrl = "https://api.scaleway.com/registry/v1/regions/fr-par";

    // Aspire environment name -> Terraform environment variable value (cloud-infrastructure/scaleway).
    // Production is added here when that environment is provisioned.
    private static readonly Dictionary<string, string> TerraformEnvironmentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["staging"] = "stage"
    };

    public DeployCommand() : base(
        "deploy",
        "Deploy an environment to Scaleway: verifies Terraform convergence, resolves deploy-time secrets from Terraform outputs, and runs the Aspire build/publish/deploy pipeline"
    )
    {
        var environmentOption = new Option<string>("--environment", "-e")
        {
            Description = "The environment to deploy (staging)",
            DefaultValueFactory = _ => "staging"
        };
        var prefixOption = new Option<string>("--prefix")
        {
            Description = $"The infrastructure prefix shared with the Terraform modules ({DefaultPrefix})",
            DefaultValueFactory = _ => DefaultPrefix
        };
        var skipPlanCheckOption = new Option<bool>("--skip-plan-check")
        {
            Description = "Deploy even when `terraform plan` reports infrastructure drift"
        };

        Options.Add(environmentOption);
        Options.Add(prefixOption);
        Options.Add(skipPlanCheckOption);

        SetAction(parseResult => Execute(
                parseResult.GetValue(environmentOption)!,
                parseResult.GetValue(prefixOption)!,
                parseResult.GetValue(skipPlanCheckOption)
            ).GetAwaiter().GetResult()
        );
    }

    [GeneratedRegex("^(?:\\s*)(\\w+_image):\\s*\"([^\"]+)\"")]
    private static partial Regex ChartImagePattern { get; }

    private static async Task Execute(string aspireEnvironment, string prefix, bool skipPlanCheck)
    {
        Prerequisite.Ensure(Prerequisite.Dotnet, Prerequisite.Terraform, Prerequisite.Kubectl);

        if (!TerraformEnvironmentNames.TryGetValue(aspireEnvironment, out var terraformEnvironment))
        {
            AnsiConsole.MarkupLine($"[red]Unknown environment '{aspireEnvironment}'.[/] Known environments: {string.Join(", ", TerraformEnvironmentNames.Keys)}.");
            Environment.Exit(1);
        }

        var repositoryRoot = Configuration.SourceCodeFolder;
        var bootstrapModuleFolder = Path.Combine(repositoryRoot, "cloud-infrastructure", "scaleway", "bootstrap");
        var environmentModuleFolder = Path.Combine(repositoryRoot, "cloud-infrastructure", "scaleway", "environment");
        var applicationFolder = Path.Combine(repositoryRoot, "application");

        PrintHeader($"Deploying {aspireEnvironment} to Scaleway");

        // The environment module needs the project id from the bootstrap module's (local) state.
        var projectId = (await RunTerraformAsync(bootstrapModuleFolder, "output -raw environment_project_id", "read the bootstrap outputs (has the bootstrap module been applied?)")).Trim();

        if (!skipPlanCheck)
        {
            PrintHeader("Checking Terraform convergence");

            // The tier file is the environment's configuration contract (the domain, sizing,
            // durability); a missing one means the environment is not configured, not that
            // defaults are fine.
            var tierFile = Path.Combine(environmentModuleFolder, "environments", $"{terraformEnvironment}.tfvars");
            if (!File.Exists(tierFile))
            {
                AnsiConsole.MarkupLine($"[red]No tier configuration found at {tierFile}.[/] Every environment must declare its tier file (see environments/stage.tfvars).");
                Environment.Exit(1);
            }

            var plan = await ProcessHelper.ExecuteQuietlyAsync(
                $"terraform plan -detailed-exitcode -input=false -no-color -var-file=environments/{terraformEnvironment}.tfvars -var prefix={prefix} -var environment={terraformEnvironment} -var project_id={projectId}",
                environmentModuleFolder,
                ("SCW_DEFAULT_PROJECT_ID", projectId)
            );
            // -detailed-exitcode: 0 = no changes, 1 = error, 2 = changes present.
            if (plan.ExitCode == 2)
            {
                AnsiConsole.MarkupLine("[red]terraform plan reports pending changes.[/] The deployment chart and the cloud infrastructure must agree before deploying — run `terraform apply` first, or pass --skip-plan-check to deploy anyway.");
                AnsiConsole.MarkupLine($"[dim]Plan output: {Markup.Escape(plan.TempFilePathWithSize)}[/]");
                Environment.Exit(1);
            }

            if (plan.ExitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]terraform plan failed:[/] " + Markup.Escape(plan.GetErrorSummary("plan")));
                Environment.Exit(plan.ExitCode);
            }

            AnsiConsole.MarkupLine("[green]Infrastructure matches the configuration.[/]");
        }

        PrintHeader("Resolving deploy-time inputs from Terraform outputs");

        // A deploy that was killed mid-run leaves its temp kubeconfig behind; clean those up before
        // creating this run's file.
        foreach (var stale in Directory.GetFiles(Path.GetTempPath(), $"pp-{terraformEnvironment}-kubeconfig-*.yaml"))
        {
            File.Delete(stale);
        }

        // The cluster kubeconfig is a sensitive Terraform output; it goes to a temp file that only
        // the deploy process reads and is deleted afterwards.
        var kubeconfigPath = Path.Combine(Path.GetTempPath(), $"pp-{terraformEnvironment}-kubeconfig-{Guid.NewGuid():N}.yaml");
        var kubeconfig = await RunTerraformAsync(environmentModuleFolder, "output -raw kubeconfig", "read the cluster kubeconfig output");
        await File.WriteAllTextAsync(kubeconfigPath, kubeconfig);

        // Per-workload Secret Manager keys are deploy-time parameters of the Aspire pipeline
        // (env-shaped names, read by the AppHost parameter resolution — same shape as the
        // deploy script always used, but never hand-copied).
        var workloadKeysJson = await RunTerraformAsync(environmentModuleFolder, "output -json workload_secret_manager_keys", "read the workload Secret Manager keys");
        var workloadKeys = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(workloadKeysJson)
                           ?? throw new InvalidOperationException("Could not parse workload_secret_manager_keys output.");

        foreach (var workload in workloadKeys.Keys)
        {
            AnsiConsole.MarkupLine($"  [green]✓[/] {workload} deploy key resolved");
        }

        // Cockpit telemetry endpoints (per signal) are not secret — they pass as plain environment
        // values that the AppHost wires into every workload. Environments without Cockpit
        // telemetry simply leave this output absent.
        var cockpitEndpointsJson = await RunTerraformAsync(environmentModuleFolder, "output -json cockpit_otlp_endpoints", "read the Cockpit OTLP endpoints");
        var cockpitEndpoints = JsonSerializer.Deserialize<Dictionary<string, string>>(cockpitEndpointsJson)
                               ?? throw new InvalidOperationException("Could not parse cockpit_otlp_endpoints output.");

        // The public internal CA certificate: the chart turns it into Envoy's BackendTLSPolicy
        // validation ConfigMap, and the app-gateway reads it as configuration to validate the
        // APIs' in-cluster TLS. Public material — like the Cockpit endpoints, it passes as a
        // plain environment value.
        var internalCaPem = (await RunTerraformAsync(environmentModuleFolder, "output -raw internal_ca_pem", "read the internal CA certificate")).Trim();

        // The environment's domain and the hostnames derived from it: the AppHost's Hostnames
        // and the transactional email sender must match the edge pipelines' custom FQDNs and
        // the TEM sending domain, so they resolve from the same Terraform outputs instead of
        // being duplicated in AppHost configuration.
        var edgeHostnamesJson = await RunTerraformAsync(environmentModuleFolder, "output -json edge_hostnames", "read the edge hostnames output");
        var edgeHostnames = JsonSerializer.Deserialize<Dictionary<string, string>>(edgeHostnamesJson)
                            ?? throw new InvalidOperationException("Could not parse edge_hostnames output.");
        // The platform serves exactly two edge pipelines; both hostnames must be present.
        if (!edgeHostnames.TryGetValue("app", out var appHostname))
        {
            AnsiConsole.MarkupLine("[red]The edge_hostnames output is missing the 'app' hostname.[/] The platform serves exactly two edge pipelines; check the environment module.");
            Environment.Exit(1);
        }

        if (!edgeHostnames.TryGetValue("back-office", out var backOfficeHostname))
        {
            AnsiConsole.MarkupLine("[red]The edge_hostnames output is missing the 'back-office' hostname.[/] The platform serves exactly two edge pipelines; check the environment module.");
            Environment.Exit(1);
        }

        var domainName = (await RunTerraformAsync(environmentModuleFolder, "output -raw domain_name", "read the domain name output")).Trim();

        // The registry namespace is private: the cluster pulls workload images through a
        // dockerconfigjson Secret built from the bootstrap module's read-only registry-pull
        // identity (Kapsule nodes have no native registry authentication). Applied idempotently
        // here — before the release's first pod pulls — so every deploy converges the Secret to
        // the current pull key.
        PrintHeader("Applying the cluster's registry pull secret");

        var registryEndpoint = (await RunTerraformAsync(bootstrapModuleFolder, "output -raw registry_endpoint", "read the registry endpoint output")).Trim();
        var registryHost = registryEndpoint.Split('/')[0];
        var registryPullSecretKey = (await RunTerraformAsync(bootstrapModuleFolder, "output -raw registry_pull_secret_key", "read the registry pull key output (has the bootstrap module been applied with the registry-pull identity?)")).Trim();
        ApplyRegistryPullSecret(kubeconfigPath, registryHost, registryPullSecretKey);

        // The API images compose the SPA bundles straight from the working tree (the csproj
        // Content includes ../WebApp/dist), so whatever dist is on disk ships verbatim. A
        // running dev stack can leave a dev-mode index.html (unhashed asset references) on top
        // of production artifacts — deploying that state serves a white screen. Build fresh
        // and verify every referenced asset exists before handing over to the pipeline.
        PrintHeader("Building and validating the SPA bundles");

        if (RunCommand.IsAspireRunning())
        {
            AnsiConsole.MarkupLine("[yellow]Warning: a local Aspire dev stack is running. Its dev servers can rewrite dist/index.html mid-deploy; if the bundle validation below fails, stop it and deploy again.[/]");
        }

        ProcessHelper.Run("npm install --silent", Configuration.ApplicationFolder, "npm install", true);
        var buildResult = ProcessHelper.ExecuteQuietly("npm run build", Configuration.ApplicationFolder);
        if (!buildResult.Success)
        {
            AnsiConsole.MarkupLine("[red]The frontend build failed; the deploy is aborted so a broken bundle cannot ship.[/]");
            Console.WriteLine(buildResult.CombinedOutput);
            return;
        }

        foreach (var system in SelfContainedSystemHelper.GetAvailableSelfContainedSystems())
        {
            ValidateSpaBundle(Configuration.ApplicationFolder, system);
            AnsiConsole.MarkupLine($"  [green]✓[/] {system} SPA bundle consistent");
        }

        PrintHeader("Running the Aspire deploy pipeline");

        // The Aspire chart is regenerated on every deploy; a stale chart folder confuses
        // the publish step, so start clean.
        var aspireOutputFolder = Path.Combine(applicationFolder, "AppHost", "aspire-output");
        if (Directory.Exists(aspireOutputFolder))
        {
            Directory.Delete(aspireOutputFolder, true);
        }

        // The deploy runs with a fresh environment: the kubeconfig points at this cluster,
        // OpenAPI document generation is skipped for speed, and Docker builds target the
        // cluster's architecture regardless of the machine running the deploy.
        var environmentVariables = new List<(string Name, string Value)>
        {
            ("KUBECONFIG", kubeconfigPath),
            ("SKIP_OPENAPI_DOCUMENTS", "true"),
            ("DOCKER_DEFAULT_PLATFORM", "linux/amd64"),
            ("SCW_DEFAULT_PROJECT_ID", projectId)
        };
        environmentVariables.AddRange(workloadKeys.Select(key => ($"Parameters__{key.Key}-secret-manager-key", key.Value["secret_key"])));
        environmentVariables.AddRange(cockpitEndpoints.Select(endpoint => ($"Cockpit__{endpoint.Key}OtlpEndpoint", endpoint.Value)));
        environmentVariables.Add(("InternalTls__CaPem", internalCaPem));
        environmentVariables.Add(("Hostnames__App", appHostname));
        environmentVariables.Add(("Hostnames__BackOffice", backOfficeHostname));
        environmentVariables.Add(("Email__Sender", $"noreply@{domainName}"));

        int exitCode;
        try
        {
            // Live output: the Aspire pipeline renders its own progress, so it streams straight to
            // the console instead of being captured.
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"aspire deploy -e {aspireEnvironment} --non-interactive",
                WorkingDirectory = applicationFolder
            };
            foreach (var (name, value) in environmentVariables)
            {
                processStartInfo.Environment[name] = value;
            }

            using var process = Process.Start(processStartInfo)!;
            await process.WaitForExitAsync();
            exitCode = process.ExitCode;
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }

        if (exitCode != 0)
        {
            AnsiConsole.MarkupLine($"[red]Aspire deploy failed with exit code {exitCode}.[/] The pipeline output above has the failing step; Aspire also writes full logs to ~/.aspire/logs.");
            Environment.Exit(exitCode);
        }

        ValidateDeployedChartImages(applicationFolder);
        ValidateChartRolloutStrategy(applicationFolder);

        await PruneRegistryTagsAsync(bootstrapModuleFolder);

        AnsiConsole.MarkupLine($"[green]Deployment of {aspireEnvironment} succeeded.[/] Run [cyan]edge-health[/] for a read-only health check of the edge and the public endpoints.");

        // DNS drift is invisible to Terraform (the public zone lives at an external provider), so
        // every deploy ends by printing the expected records and their live resolution —
        // informational here, never failing the deploy; the standalone dns-check exits non-zero.
        PrintHeader("Verifying the DNS records");
        await DnsCheckCommand.PrintDnsReportAsync();
    }

    /// <summary>
    ///     Guards against the upstream Aspire regression where image references fail to reach the
    ///     generated chart (everything stays a local `name:latest` reference): the chart that just
    ///     deployed must reference registry-qualified images with deploy-specific tags. A silent
    ///     regression here means pods run stale or missing images, so it fails the deploy loudly.
    /// </summary>
    private static void ValidateDeployedChartImages(string applicationFolder)
    {
        var valuesPath = Path.Combine(applicationFolder, "AppHost", "aspire-output", "values.kubernetes.yaml");
        if (!File.Exists(valuesPath))
        {
            AnsiConsole.MarkupLine($"[red]Chart validation failed:[/] {Markup.Escape(valuesPath)} does not exist — the generated chart layout changed, update the deploy command.");
            Environment.Exit(1);
        }

        var images = File.ReadAllLines(valuesPath)
            .Select(line => ChartImagePattern.Match(line))
            .Where(match => match.Success)
            .Select(match => (Name: match.Groups[1].Value, Image: match.Groups[2].Value))
            .ToList();

        if (images.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Chart validation failed:[/] no image references found in the chart values — the generated chart layout changed, update the deploy command.");
            Environment.Exit(1);
        }

        var invalidImages = images.Where(image => !image.Image.Contains('/') || image.Image.EndsWith(":latest")).ToList();
        foreach (var image in invalidImages)
        {
            AnsiConsole.MarkupLine($"  [red]✗[/] {image.Name} = '{Markup.Escape(image.Image)}' — expected a registry-qualified image with a deploy-specific tag");
        }

        if (invalidImages.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]Chart validation failed:[/] the deployed chart references non-registry images (an upstream Aspire regression?).");
            Environment.Exit(1);
        }

        AnsiConsole.MarkupLine($"[green]Chart validated:[/] {images.Count} registry-qualified image references.");
    }

    /// <summary>
    ///     Guards the AppHost's rollout patch (see Program.cs): every deployment in the deployed
    ///     chart must roll out with maxUnavailable 0, otherwise a single-replica workload drops its
    ///     last ready pod before the replacement is ready — deploys surface as edge 504 windows.
    /// </summary>
    private static void ValidateChartRolloutStrategy(string applicationFolder)
    {
        var templatesFolder = Path.Combine(applicationFolder, "AppHost", "aspire-output", "templates");
        var deployments = Directory.Exists(templatesFolder)
            ? Directory.GetFiles(templatesFolder, "deployment.yaml", SearchOption.AllDirectories)
            : [];

        if (deployments.Length == 0)
        {
            AnsiConsole.MarkupLine($"[red]Chart validation failed:[/] no deployment.yaml found under {Markup.Escape(templatesFolder)} — the generated chart layout changed, update the deploy command.");
            Environment.Exit(1);
        }

        var unpatched = deployments.Where(deployment => !File.ReadAllText(deployment).Contains("maxUnavailable: 0")).ToList();
        if (unpatched.Count > 0)
        {
            foreach (var deployment in unpatched)
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(Path.GetDirectoryName(deployment) ?? "unknown")} rolls out with unavailable pods");
            }

            AnsiConsole.MarkupLine("[red]Chart validation failed:[/] the AppHost rollout patch did not reach every deployment — deploys would cause 504 windows.");
            Environment.Exit(1);
        }

        AnsiConsole.MarkupLine($"[green]Chart validated:[/] {deployments.Length} deployments roll out with zero unavailable pods.");

        var migrationJobs = Directory.GetFiles(templatesFolder, "job.yaml", SearchOption.AllDirectories);
        if (migrationJobs.Length != 2)
        {
            AnsiConsole.MarkupLine($"[red]Chart validation failed:[/] expected 2 migration job templates (one per self-contained system), found {migrationJobs.Length} — the AppHost migration patch did not reach the chart.");
            Environment.Exit(1);
        }

        AnsiConsole.MarkupLine($"[green]Chart validated:[/] {migrationJobs.Length} pre-upgrade migration jobs.");
    }

    private static async Task PruneRegistryTagsAsync(string bootstrapModuleFolder)
    {
        var secretKey = Environment.GetEnvironmentVariable("SCW_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            AnsiConsole.MarkupLine("[yellow]Registry prune skipped:[/] SCW_SECRET_KEY is not set.");
            return;
        }

        var namespaceId = (await RunTerraformAsync(bootstrapModuleFolder, "output -raw registry_namespace_id", "read the registry namespace output (has the bootstrap module output been added and applied?)")).Trim();
        if (string.IsNullOrWhiteSpace(namespaceId))
        {
            AnsiConsole.MarkupLine("[yellow]Registry prune skipped:[/] no registry namespace output.");
            return;
        }

        // Registry namespace IDs are region-qualified (fr-par/<uuid>); the API takes the bare uuid.
        namespaceId = namespaceId.Split('/').Last();

        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("X-Auth-Token", secretKey);
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            var images = (await GetRegistryPageAsync<RegistryImagePage>(httpClient, $"images?namespace_id={namespaceId}&page_size=100"))?.Images ?? [];
            var kept = 0;
            var deleted = 0;
            foreach (var image in images)
            {
                var tags = (await GetRegistryPageAsync<RegistryTagPage>(httpClient, $"images/{image.Id}/tags?order_by=created_at_desc&page_size=100"))?.Tags ?? [];
                foreach (var tag in tags.Skip(RegistryTagsToKeep))
                {
                    var response = await httpClient.DeleteAsync($"{RegistryApiBaseUrl}/tags/{tag.Id}");
                    if (response.IsSuccessStatusCode)
                    {
                        deleted++;
                    }
                }

                kept += Math.Min(tags.Count, RegistryTagsToKeep);
            }

            AnsiConsole.MarkupLine($"[green]Registry pruned:[/] kept {kept} tags, deleted {deleted} stale tags.");
        }
        catch (Exception exception)
        {
            // Pruning is housekeeping: a failure must never fail an otherwise successful deploy.
            AnsiConsole.MarkupLine($"[yellow]Registry prune failed[/] (the deploy itself succeeded): {Markup.Escape(exception.Message)}");
        }
    }

    private static async Task<T?> GetRegistryPageAsync<T>(HttpClient httpClient, string relativeUrl)
    {
        var response = await httpClient.GetAsync($"{RegistryApiBaseUrl}/{relativeUrl}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }

    // Fails loud when a SPA's index.html references an asset that is not in the dist — the
    // signature of a dev-polluted or stale dist (dev servers write unhashed references over
    // hashed production bundles), which would deploy a white screen.
    private static void ValidateSpaBundle(string applicationFolder, string system)
    {
        var distFolder = Path.Combine(applicationFolder, system, "WebApp", "dist");
        var indexPath = Path.Combine(distFolder, "index.html");
        if (!File.Exists(indexPath))
        {
            throw new InvalidOperationException($"Missing {indexPath}. The frontend build should have produced it; investigate before deploying.");
        }

        var html = File.ReadAllText(indexPath);
        foreach (Match match in Regex.Matches(html, "(?:src|href)=\"([^\"]+)\""))
        {
            var reference = match.Groups[1].Value;
            if (reference.StartsWith("http", StringComparison.Ordinal) || reference.StartsWith("data:", StringComparison.Ordinal)) continue;

            var relativePath = reference.Replace("%CDN_URL%/", "", StringComparison.Ordinal).TrimStart('/');
            if (!File.Exists(Path.Combine(distFolder, relativePath)))
            {
                throw new InvalidOperationException(
                    $"{indexPath} references '{reference}', which does not exist in the dist. The dist is stale or was overwritten by a running dev stack. Stop the dev stack, rerun the frontend build, and deploy again."
                );
            }
        }
    }

    private static async Task<string> RunTerraformAsync(string workingDirectory, string arguments, string failureHint)
    {
        var result = await ProcessHelper.ExecuteQuietlyAsync($"terraform {arguments}", workingDirectory);
        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]terraform {arguments} failed:[/] " + Markup.Escape(result.GetErrorSummary("terraform")));
            AnsiConsole.MarkupLine($"[dim]{failureHint}[/]");
            Environment.Exit(result.ExitCode);
        }

        return result.StdOut;
    }

    // The dockerconfigjson Secret the generated chart's imagePullSecrets reference. The pull key
    // exists on disk only in the temp manifest, only for the duration of the apply.
    private static void ApplyRegistryPullSecret(string kubeconfigPath, string registryHost, string secretKey)
    {
        var dockerConfigJson = JsonSerializer.Serialize(new
            {
                auths = new Dictionary<string, object>
                {
                    // Scaleway registry auth: the username is a fixed placeholder, the secret key
                    // authenticates.
                    [registryHost] = new { username = "nologin", password = secretKey }
                }
            }
        );
        var secretManifest = $"""
                              ---
                              apiVersion: "v1"
                              kind: "Secret"
                              metadata:
                                name: "scaleway-registry-pull"
                                namespace: "default"
                              type: "kubernetes.io/dockerconfigjson"
                              data:
                                .dockerconfigjson: "{Convert.ToBase64String(Encoding.UTF8.GetBytes(dockerConfigJson))}"
                              """;

        var manifestPath = Path.Combine(Path.GetTempPath(), $"pp-registry-pull-secret-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(manifestPath, secretManifest);
        try
        {
            var result = ProcessHelper.ExecuteQuietly($"kubectl --kubeconfig {kubeconfigPath} apply -f {manifestPath}");
            if (!result.Success)
            {
                AnsiConsole.MarkupLine("[red]Applying the registry pull secret failed:[/] " + Markup.Escape(result.GetErrorSummary("kubectl")));
                AnsiConsole.MarkupLine("[dim]The registry is private, so without the pull secret no pod can pull its image.[/]");
                Environment.Exit(result.ExitCode);
            }

            AnsiConsole.MarkupLine("  [green]✓[/] scaleway-registry-pull applied");
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    private static void PrintHeader(string title)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold] {title} [/]"));
        AnsiConsole.WriteLine();
    }

    // Deserialization-only shapes for the registry prune: the JSON serializer instantiates them
    // (hence UsedImplicitly), and only the ids are needed — kept tags are deleted by id, not name.
    [UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
    private sealed record RegistryImagePage(List<RegistryImage> Images);

    [UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
    private sealed record RegistryTagPage(List<RegistryTag> Tags);

    [UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
    private sealed record RegistryImage(string Id);

    [UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
    private sealed record RegistryTag(string Id);
}
