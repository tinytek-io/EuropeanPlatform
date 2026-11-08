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
///     Secret Manager keys come from Terraform outputs, and pending infrastructure changes are applied
///     (after confirmation) before containers roll out, so the chart and the cloud always agree.
/// </summary>
/// <remarks>
///     Needs only the environment name and an API key: the environment's operator key from the scw
///     profile &lt;prefix&gt;-&lt;environment&gt; that the bootstrap command writes, or
///     SCW_ACCESS_KEY/SCW_SECRET_KEY (CI). The project, organization, and Terraform state credentials
///     derive from those (see ScalewayEnvironment).
/// </remarks>
public sealed partial class DeployCommand : Command
{
    /// <summary>
    ///     Deletes stale deploy tags from the Container Registry, keeping the newest few per image.
    ///     Every deploy pushes a full tag set; without pruning the registry fills its storage quota
    ///     and pushes start failing with HTTP 500 (found live: 24 deploys filled 18 GB before the
    ///     quota broke every subsequent deploy). The pipeline key's registry access authorizes the
    ///     prune, so it runs as the last step of every deploy.
    /// </summary>
    private const int RegistryTagsToKeep = 3;

    public DeployCommand() : base(
        "deploy",
        "Deploy an environment to Scaleway: applies pending Terraform changes, resolves deploy-time secrets from Terraform outputs, and runs the Aspire build/publish/deploy pipeline"
    )
    {
        var environmentOption = new Option<string>("--environment", "-e")
        {
            Description = "The environment to deploy (staging)",
            DefaultValueFactory = _ => "staging"
        };
        var skipPlanCheckOption = new Option<bool>("--skip-plan-check")
        {
            Description = "Deploy without planning or applying Terraform changes"
        };

        var autoApproveOption = new Option<bool>("--auto-approve")
        {
            Description = "Apply pending Terraform changes without asking (required when there is no terminal, as in CI)"
        };

        var skipDnsCheckOption = new Option<bool>("--skip-dns-check")
        {
            Description = "Deploy even when the hosting zone's DNS records do not match the environment (an environment's first bring-up)"
        };

        Options.Add(environmentOption);
        Options.Add(skipPlanCheckOption);
        Options.Add(autoApproveOption);
        Options.Add(skipDnsCheckOption);

        SetAction(parseResult => Execute(
                parseResult.GetValue(environmentOption)!,
                parseResult.GetValue(skipPlanCheckOption),
                parseResult.GetValue(autoApproveOption),
                parseResult.GetValue(skipDnsCheckOption)
            ).GetAwaiter().GetResult()
        );
    }

    [GeneratedRegex("^(?:\\s*)(\\w+_image):\\s*\"([^\"]+)\"")]
    private static partial Regex ChartImagePattern { get; }

    private static async Task Execute(string aspireEnvironment, bool skipPlanCheck, bool autoApprove, bool skipDnsCheck)
    {
        Prerequisite.Ensure(Prerequisite.Dotnet, Prerequisite.Terraform, Prerequisite.Kubectl);

        var terraformEnvironment = ScalewayEnvironment.TerraformNameOf(aspireEnvironment);
        var repositoryRoot = Configuration.SourceCodeFolder;
        var environmentModuleFolder = ScalewayEnvironment.EnvironmentModuleFolder;
        var applicationFolder = Path.Combine(repositoryRoot, "application");

        PrintHeader($"Deploying {aspireEnvironment} to Scaleway");

        // Everything else derives from the environment name and the API key: the prefix from the
        // committed tier file, the project by name, the organization from the project. The key is
        // the environment's operator (the scw profile the bootstrap command writes) unless
        // SCW_ACCESS_KEY/SCW_SECRET_KEY are exported, as in CI.
        var prefix = ScalewayEnvironment.ReadPrefix(terraformEnvironment);
        var backendConfiguration = ScalewayEnvironment.BackendConfigurationOf(terraformEnvironment);
        var projectName = $"{prefix}-{terraformEnvironment}";
        var credentials = await ScalewayEnvironment.ReadCredentialsAsync(projectName);
        var apiKey = await ScalewayEnvironment.GetApiKeyAsync(credentials);
        var organizationId = await ScalewayEnvironment.GetOrganizationIdAsync(credentials, apiKey.DefaultProjectId);
        var projectId = await ScalewayEnvironment.FindProjectIdAsync(credentials, organizationId, projectName);
        if (projectId is null)
        {
            AnsiConsole.MarkupLine($"[red]No Scaleway project named '{projectName}' exists.[/] Create the environment first: dotnet run --project developer-cli -- bootstrap -e {aspireEnvironment}");
            Environment.Exit(1);
        }

        ScalewayEnvironment.ExportForTerraform(credentials, organizationId, projectId);
        AnsiConsole.MarkupLine($"Acting with the key from {Markup.Escape(credentials.Source)} in project [cyan]{projectName}[/].");

        await TerraformHelper.RunSensitiveAsync(environmentModuleFolder, $"init -reconfigure -input=false -no-color -backend-config={backendConfiguration}", "initialize the environment module's remote state (has the bootstrap run, and may this key read the state bucket?)");

        if (!skipPlanCheck)
        {
            PrintHeader("Applying pending infrastructure changes");
            await ApplyEnvironmentChangesAsync(terraformEnvironment, projectId, autoApprove, "Deploy cancelled before anything rolled out.");
        }

        // DNS drift is invisible to Terraform (the public zone lives at an external provider), and a
        // missing record breaks what the deploy rolls out: the edge serves nothing for a hostname
        // without its CNAME, and Let's Encrypt cannot issue the Gateway certificate. So the records
        // are verified before anything is built; an environment's first bring-up, whose records
        // are still being created, passes --skip-dns-check.
        if (!skipDnsCheck)
        {
            PrintHeader("Verifying the DNS records");
            if (await DnsCheckCommand.PrintDnsReportAsync() > 0)
            {
                AnsiConsole.MarkupLine("[red]The DNS records do not match the environment.[/] Fix them first (the table above has the exact values), or pass --skip-dns-check to deploy anyway.");
                Environment.Exit(1);
            }
        }

        PrintHeader("Resolving deploy-time inputs from Terraform outputs");

        // A deploy that was killed mid-run leaves its temp kubeconfig behind; clean those up before
        // creating this run's file.
        foreach (var stale in Directory.GetFiles(Path.GetTempPath(), $"pp-{terraformEnvironment}-kubeconfig-*.yaml"))
        {
            File.Delete(stale);
        }

        // The cluster kubeconfig is a sensitive Terraform output: held in memory until the cluster
        // steps need it as a file (see WriteSecretTempFile).
        var kubeconfig = await TerraformHelper.RunSensitiveAsync(environmentModuleFolder, "output -raw kubeconfig", "read the cluster kubeconfig output");

        // Per-workload Secret Manager keys are deploy-time parameters of the Aspire pipeline
        // (env-shaped names, read by the AppHost parameter resolution — same shape as the
        // deploy script always used, but never hand-copied).
        var workloadKeysJson = await TerraformHelper.RunSensitiveAsync(environmentModuleFolder, "output -json workload_secret_manager_keys", "read the workload Secret Manager keys");
        var workloadKeys = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(workloadKeysJson)
                           ?? throw new InvalidOperationException("Could not parse workload_secret_manager_keys output.");

        foreach (var workload in workloadKeys.Keys)
        {
            AnsiConsole.MarkupLine($"  [green]✓[/] {workload} deploy key resolved");
        }

        // Cockpit telemetry endpoints (per signal) are not secret — they pass as plain environment
        // values that the AppHost wires into every workload. Environments without Cockpit
        // telemetry simply leave this output absent.
        var cockpitEndpointsJson = await TerraformHelper.RunSensitiveAsync(environmentModuleFolder, "output -json cockpit_otlp_endpoints", "read the Cockpit OTLP endpoints");
        var cockpitEndpoints = JsonSerializer.Deserialize<Dictionary<string, string>>(cockpitEndpointsJson)
                               ?? throw new InvalidOperationException("Could not parse cockpit_otlp_endpoints output.");

        // The public internal CA certificate: the chart turns it into Envoy's BackendTLSPolicy
        // validation ConfigMap, and the app-gateway reads it as configuration to validate the
        // APIs' in-cluster TLS. Public material — like the Cockpit endpoints, it passes as a
        // plain environment value.
        var internalCaPem = (await TerraformHelper.RunSensitiveAsync(environmentModuleFolder, "output -raw internal_ca_pem", "read the internal CA certificate")).Trim();

        // The environment's domain and the hostnames derived from it: the AppHost's Hostnames
        // and the transactional email sender must match the edge pipelines' custom FQDNs and
        // the TEM sending domain, so they resolve from the same Terraform outputs instead of
        // being duplicated in AppHost configuration.
        var edgeHostnamesJson = await TerraformHelper.RunSensitiveAsync(environmentModuleFolder, "output -json edge_hostnames", "read the edge hostnames output");
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

        var domainName = (await TerraformHelper.RunSensitiveAsync(environmentModuleFolder, "output -raw domain_name", "read the domain name output")).Trim();
        var region = (await TerraformHelper.RunSensitiveAsync(environmentModuleFolder, "output -raw region", "read the region output")).Trim();
        var storageEndpoint = (await TerraformHelper.RunSensitiveAsync(environmentModuleFolder, "output -raw object_storage_endpoint", "read the Object Storage endpoint output")).Trim();
        var bucketPrefix = (await TerraformHelper.RunSensitiveAsync(environmentModuleFolder, "output -raw bucket_prefix", "read the bucket prefix output")).Trim();
        var databaseEndpointIp = (await TerraformHelper.RunSensitiveAsync(environmentModuleFolder, "output -raw database_endpoint_ip", "read the database endpoint IP output")).Trim();
        var databaseEndpointPort = (await TerraformHelper.RunSensitiveAsync(environmentModuleFolder, "output -raw database_endpoint_port", "read the database endpoint port output")).Trim();

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

        await ProcessHelper.RunAsync("npm install --silent", Configuration.ApplicationFolder, "npm install", true);
        var buildResult = await ProcessHelper.ExecuteQuietlyAsync("npm run build", Configuration.ApplicationFolder);
        if (!buildResult.Success)
        {
            AnsiConsole.MarkupLine("[red]The frontend build failed; the deploy is aborted so a broken bundle cannot ship.[/]");
            Console.WriteLine(buildResult.CombinedOutput);
            Environment.Exit(1);
        }

        foreach (var system in SelfContainedSystemHelper.GetAvailableSelfContainedSystems())
        {
            ValidateSpaBundle(Configuration.ApplicationFolder, system);
            AnsiConsole.MarkupLine($"  [green]✓[/] {system} SPA bundle consistent");
        }

        // The registry namespace is private: the cluster pulls workload images through a
        // dockerconfigjson Secret built from the bootstrap module's read-only registry-pull
        // identity (Kapsule nodes have no native registry authentication; the environment module
        // passes the bootstrap's registry outputs through). Applied idempotently here — before the
        // release's first pod pulls — so every deploy converges the Secret to the current pull key.
        PrintHeader("Applying the cluster's registry pull secret");

        var registryEndpoint = await TerraformHelper.OutputAsync(environmentModuleFolder, "registry_endpoint");
        var registryHost = registryEndpoint.Split('/')[0];
        var registryPullSecretKey = await TerraformHelper.OutputAsync(environmentModuleFolder, "registry_pull_secret_key");
        var kubeconfigPath = WriteSecretTempFile($"pp-{terraformEnvironment}-kubeconfig-{Guid.NewGuid():N}.yaml", kubeconfig);
        ApplyRegistryPullSecret(kubeconfigPath, registryHost, registryPullSecretKey);
        AdoptHandAppliedNetworkPolicies(kubeconfigPath, repositoryRoot);
        LogInToRegistry(registryHost, credentials);

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
            // Terraform-owned values the AppHost needs at publish time, never hand-copied into
            // appsettings.<environment>.json.
            ("Scaleway__ProjectId", projectId),
            ("Scaleway__Region", region),
            ("Registry__Endpoint", registryHost),
            ("Registry__Repository", registryEndpoint[(registryHost.Length + 1)..]),
            ("Storage__Endpoint", storageEndpoint),
            ("Storage__BucketPrefix", bucketPrefix),
            // The network policies in the chart admit database egress to exactly this endpoint.
            ("Database__EndpointIp", databaseEndpointIp),
            ("Database__EndpointPort", databaseEndpointPort)
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

        // The rollout, migration-job, and internal TLS patches assert themselves inside the AppHost
        // before Helm runs; the image references are only final after the push, so they are
        // checked here.
        ValidateDeployedChartImages(applicationFolder);

        await PruneRegistryTagsAsync(await TerraformHelper.OutputAsync(environmentModuleFolder, "registry_namespace_id"), credentials);

        // The rollout can change what Terraform reads from the cluster: the Kapsule controller creates
        // (or recreates) the gateway Load Balancer's 443 and 80 frontends, and the edge-only ACLs on
        // them are found by port. A second pass closes any frontend the rollout opened.
        if (!skipPlanCheck)
        {
            PrintHeader("Applying infrastructure changes the rollout caused");
            await ApplyEnvironmentChangesAsync(terraformEnvironment, projectId, autoApprove, "The rollout succeeded, but these changes are not applied; a gateway frontend the rollout created stays open until they are.");
        }

        AnsiConsole.MarkupLine($"[green]Deployment of {aspireEnvironment} succeeded.[/] Run [cyan]edge-health[/] for a read-only health check of the edge and the public endpoints.");
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

    private static async Task PruneRegistryTagsAsync(string namespaceId, ScalewayCredentials credentials)
    {
        var secretKey = credentials.SecretKey;

        // Registry namespace IDs are region-qualified (<region>/<uuid>): the region selects the API,
        // which takes the bare uuid.
        var registryApiBaseUrl = $"https://api.scaleway.com/registry/v1/regions/{namespaceId.Split('/')[0]}";
        namespaceId = namespaceId.Split('/').Last();

        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("X-Auth-Token", secretKey);
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            var images = (await GetRegistryPageAsync<RegistryImagePage>(httpClient, $"{registryApiBaseUrl}/images?namespace_id={namespaceId}&page_size=100"))?.Images ?? [];
            var kept = 0;
            var deleted = 0;
            foreach (var image in images)
            {
                var tags = (await GetRegistryPageAsync<RegistryTagPage>(httpClient, $"{registryApiBaseUrl}/images/{image.Id}/tags?order_by=created_at_desc&page_size=100"))?.Tags ?? [];
                foreach (var tag in tags.Skip(RegistryTagsToKeep))
                {
                    var response = await httpClient.DeleteAsync($"{registryApiBaseUrl}/tags/{tag.Id}");
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

    private static async Task<T?> GetRegistryPageAsync<T>(HttpClient httpClient, string url)
    {
        var response = await httpClient.GetAsync(url);
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

    // Secret material that a tool needs as a file (the kubeconfig for kubectl and aspire deploy, the
    // registry pull Secret manifest) is written owner-only to a temp file. It is deleted when the
    // process exits, including every Environment.Exit path (finally blocks do not run there), and
    // callers delete it as soon as they are done; a killed run's kubeconfig is swept by the next
    // deploy.
    private static string WriteSecretTempFile(string fileName, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), fileName);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => File.Delete(path);
        var ownerOnly = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) ownerOnly.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var writer = new StreamWriter(path, ownerOnly);
        writer.Write(content);
        return path;
    }

    /// <summary>
    ///     Plans the environment module and applies its pending changes after confirmation (see
    ///     TerraformHelper.ApplyChangesAsync). The committed tier file is the environment's
    ///     configuration contract; an operator's git-ignored &lt;environment&gt;.local.tfvars (a test
    ///     domain, say) is layered on top of it, and Terraform lets the later var file win.
    /// </summary>
    private static async Task ApplyEnvironmentChangesAsync(string terraformEnvironment, string projectId, bool autoApprove, string declinedMessage)
    {
        var varFiles = $"-var-file={ScalewayEnvironment.TierFileOf(terraformEnvironment)}";
        var localOverrides = Path.Combine(ScalewayEnvironment.EnvironmentModuleFolder, "environments", $"{terraformEnvironment}.local.tfvars");
        if (File.Exists(localOverrides))
        {
            varFiles += $" -var-file={localOverrides}";
            AnsiConsole.MarkupLine($"[yellow]Using the local overrides in environments/{terraformEnvironment}.local.tfvars.[/]");
        }

        await TerraformHelper.ApplyChangesAsync(
            ScalewayEnvironment.EnvironmentModuleFolder,
            $"{varFiles} -var environment={terraformEnvironment} -var project_id={projectId}",
            autoApprove,
            $"{declinedMessage} Run the deploy again to apply them, or pass --skip-plan-check to deploy without them."
        );
    }

    // Aspire pushes the images with the local Docker credentials, so the deploy logs Docker in with
    // the same key it acts with (Scaleway's registry takes any user name; the secret key authenticates).
    private static void LogInToRegistry(string registryHost, ScalewayCredentials credentials)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"login {registryHost} --username nologin --password-stdin",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        ProcessHelper.StartProcess(processStartInfo, credentials.SecretKey);
        AnsiConsole.MarkupLine($"  [green]✓[/] Docker logged in to {registryHost}");
    }

    // The dockerconfigjson Secret the generated chart's imagePullSecrets reference. The pull key
    // exists on disk only in the owner-only temp manifest, only for the duration of the apply.
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

        var manifestPath = WriteSecretTempFile($"pp-registry-pull-secret-{Guid.NewGuid():N}.yaml", secretManifest);
        var result = ProcessHelper.ExecuteQuietly($"kubectl --kubeconfig {kubeconfigPath} apply -f {manifestPath}");
        File.Delete(manifestPath);
        if (!result.Success)
        {
            AnsiConsole.MarkupLine("[red]Applying the registry pull secret failed:[/] " + Markup.Escape(result.GetErrorSummary("kubectl")));
            AnsiConsole.MarkupLine("[dim]The registry is private, so without the pull secret no pod can pull its image.[/]");
            Environment.Exit(result.ExitCode);
        }

        AnsiConsole.MarkupLine("  [green]✓[/] scaleway-registry-pull applied");
    }

    // The network policies ship in the chart, but clusters set up before that carry them applied by
    // hand, and Helm refuses to take over a resource it did not create. Each hand-applied policy is
    // labelled as owned by the release (named after the chart's internal CA ConfigMap, which every
    // release carries), so the upgrade adopts it in place. Deleting them instead would leave the
    // namespace allow-all until Helm recreates them.
    private static void AdoptHandAppliedNetworkPolicies(string kubeconfigPath, string repositoryRoot)
    {
        var policiesPath = Path.Combine(repositoryRoot, "cloud-infrastructure", "scaleway", "cluster-ops", "network-policies.yaml");
        var policyNames = Regex.Matches(File.ReadAllText(policiesPath), "^kind: CiliumNetworkPolicy\nmetadata:\n  name: (\\S+)$", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        var kubectl = $"kubectl --kubeconfig {kubeconfigPath} --namespace default";
        var existing = ProcessHelper.ExecuteQuietly($"{kubectl} get ciliumnetworkpolicies --no-headers -o custom-columns=NAME:.metadata.name,MANAGED_BY:.metadata.labels.app\\.kubernetes\\.io/managed-by");
        if (!existing.Success)
        {
            AnsiConsole.MarkupLine("[red]Listing the cluster's network policies failed:[/] " + Markup.Escape(existing.GetErrorSummary("kubectl")));
            Environment.Exit(existing.ExitCode);
        }

        var handApplied = existing.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(columns => columns[1] != "Helm" && policyNames.Contains(columns[0]))
            .Select(columns => columns[0])
            .ToArray();
        if (handApplied.Length == 0)
        {
            return;
        }

        var release = ProcessHelper.ExecuteQuietly($"{kubectl} get configmap internal-root-ca-bundle --no-headers -o custom-columns=NAME:.metadata.annotations.meta\\.helm\\.sh/release-name,NAMESPACE:.metadata.annotations.meta\\.helm\\.sh/release-namespace");
        var releaseColumns = release.StdOut.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!release.Success || releaseColumns.Length != 2 || releaseColumns.Contains("<none>"))
        {
            AnsiConsole.MarkupLine("[red]The cluster has hand-applied network policies but no release to adopt them into[/] (the internal-root-ca-bundle ConfigMap carries no Helm release name).");
            AnsiConsole.MarkupLine("[dim]Delete them (kubectl delete cnp --all -n default) and deploy again; the chart recreates them.[/]");
            Environment.Exit(1);
        }

        foreach (var policyName in handApplied)
        {
            var label = ProcessHelper.ExecuteQuietly($"{kubectl} label ciliumnetworkpolicy {policyName} app.kubernetes.io/managed-by=Helm --overwrite");
            var annotate = ProcessHelper.ExecuteQuietly($"{kubectl} annotate ciliumnetworkpolicy {policyName} meta.helm.sh/release-name={releaseColumns[0]} meta.helm.sh/release-namespace={releaseColumns[1]} --overwrite");
            if (!label.Success || !annotate.Success)
            {
                AnsiConsole.MarkupLine($"[red]Adopting the network policy {policyName} into the release failed:[/] " + Markup.Escape((label.Success ? annotate : label).GetErrorSummary("kubectl")));
                Environment.Exit(1);
            }
        }

        AnsiConsole.MarkupLine($"  [green]✓[/] {handApplied.Length} hand-applied network policies adopted into the release");
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
