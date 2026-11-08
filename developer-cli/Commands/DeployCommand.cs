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

    // Aspire environment name -> Terraform environment variable value (cloud-infrastructure/scaleway).
    // Production is added here when that environment is provisioned.
    private static readonly Dictionary<string, string> TerraformEnvironmentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["staging"] = "stage"
    };

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
        var prefixOption = new Option<string>("--prefix")
        {
            Description = $"The infrastructure prefix shared with the Terraform modules ({DefaultPrefix})",
            DefaultValueFactory = _ => DefaultPrefix
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
        Options.Add(prefixOption);
        Options.Add(skipPlanCheckOption);
        Options.Add(autoApproveOption);
        Options.Add(skipDnsCheckOption);

        SetAction(parseResult => Execute(
                parseResult.GetValue(environmentOption)!,
                parseResult.GetValue(prefixOption)!,
                parseResult.GetValue(skipPlanCheckOption),
                parseResult.GetValue(autoApproveOption),
                parseResult.GetValue(skipDnsCheckOption)
            ).GetAwaiter().GetResult()
        );
    }

    [GeneratedRegex("^(?:\\s*)(\\w+_image):\\s*\"([^\"]+)\"")]
    private static partial Regex ChartImagePattern { get; }

    // A resource line of `terraform plan` output: "  # <address> will be created", "must be replaced",
    // "has moved to <address>", and so on.
    [GeneratedRegex(@"^\s*# (.+?) (will be|must be|has moved to) (.+)$")]
    private static partial Regex PlanChangePattern { get; }

    private static async Task Execute(string aspireEnvironment, string prefix, bool skipPlanCheck, bool autoApprove, bool skipDnsCheck)
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

        // Each environment's bootstrap runs on its own local state file (the project resource is keyed
        // by the environment name, so one shared file would replace one environment's project with
        // another's), and the environment module's remote state is per environment too. Both are
        // selected explicitly here, so a deploy can never read or plan against another environment.
        var bootstrapState = $"terraform.{terraformEnvironment}.tfstate";
        if (!File.Exists(Path.Combine(bootstrapModuleFolder, bootstrapState)))
        {
            AnsiConsole.MarkupLine($"[red]No bootstrap state found at {Markup.Escape(Path.Combine(bootstrapModuleFolder, bootstrapState))}.[/] Apply the bootstrap with -state={bootstrapState} (an existing default terraform.tfstate for this environment can be renamed).");
            Environment.Exit(1);
        }

        var backendConfiguration = $"environments/{terraformEnvironment}.backend.hcl";
        if (!File.Exists(Path.Combine(environmentModuleFolder, backendConfiguration)))
        {
            AnsiConsole.MarkupLine($"[red]No backend configuration found at {Markup.Escape(Path.Combine(environmentModuleFolder, backendConfiguration))}.[/] Every environment declares its state bucket there (see environments/stage.backend.hcl).");
            Environment.Exit(1);
        }

        await RunTerraformAsync(environmentModuleFolder, $"init -reconfigure -input=false -no-color -backend-config={backendConfiguration}", "initialize the environment module's remote state (are the state-bucket credentials exported as AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY?)");

        // The environment module needs the project id from the bootstrap module's (local) state.
        var projectId = (await RunTerraformAsync(bootstrapModuleFolder, $"output -state={bootstrapState} -raw environment_project_id", "read the bootstrap outputs (has the bootstrap module been applied?)")).Trim();

        if (!skipPlanCheck)
        {
            PrintHeader("Applying pending infrastructure changes");
            await ApplyInfrastructureChangesAsync(environmentModuleFolder, terraformEnvironment, prefix, projectId, autoApprove, "Deploy cancelled before anything rolled out.");
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
        var kubeconfig = await RunTerraformAsync(environmentModuleFolder, "output -raw kubeconfig", "read the cluster kubeconfig output");

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
        var region = (await RunTerraformAsync(environmentModuleFolder, "output -raw region", "read the region output")).Trim();
        var storageEndpoint = (await RunTerraformAsync(environmentModuleFolder, "output -raw object_storage_endpoint", "read the Object Storage endpoint output")).Trim();
        var bucketPrefix = (await RunTerraformAsync(environmentModuleFolder, "output -raw bucket_prefix", "read the bucket prefix output")).Trim();
        var databaseEndpointIp = (await RunTerraformAsync(environmentModuleFolder, "output -raw database_endpoint_ip", "read the database endpoint IP output")).Trim();
        var databaseEndpointPort = (await RunTerraformAsync(environmentModuleFolder, "output -raw database_endpoint_port", "read the database endpoint port output")).Trim();

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
        // identity (Kapsule nodes have no native registry authentication). Applied idempotently
        // here — before the release's first pod pulls — so every deploy converges the Secret to
        // the current pull key.
        PrintHeader("Applying the cluster's registry pull secret");

        var registryEndpoint = (await RunTerraformAsync(bootstrapModuleFolder, $"output -state={bootstrapState} -raw registry_endpoint", "read the registry endpoint output")).Trim();
        var registryHost = registryEndpoint.Split('/')[0];
        var registryPullSecretKey = (await RunTerraformAsync(bootstrapModuleFolder, $"output -state={bootstrapState} -raw registry_pull_secret_key", "read the registry pull key output (has the bootstrap module been applied with the registry-pull identity?)")).Trim();
        var kubeconfigPath = WriteSecretTempFile($"pp-{terraformEnvironment}-kubeconfig-{Guid.NewGuid():N}.yaml", kubeconfig);
        ApplyRegistryPullSecret(kubeconfigPath, registryHost, registryPullSecretKey);
        AdoptHandAppliedNetworkPolicies(kubeconfigPath, repositoryRoot);

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

        await PruneRegistryTagsAsync(bootstrapModuleFolder, bootstrapState);

        // The rollout can change what Terraform reads from the cluster: the Kapsule controller creates
        // (or recreates) the gateway Load Balancer's 443 and 80 frontends, and the edge-only ACLs on
        // them are found by port. A second pass closes any frontend the rollout opened.
        if (!skipPlanCheck)
        {
            PrintHeader("Applying infrastructure changes the rollout caused");
            await ApplyInfrastructureChangesAsync(environmentModuleFolder, terraformEnvironment, prefix, projectId, autoApprove, "The rollout succeeded, but these changes are not applied; a gateway frontend the rollout created stays open until they are.");
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

    private static async Task PruneRegistryTagsAsync(string bootstrapModuleFolder, string bootstrapState)
    {
        var secretKey = Environment.GetEnvironmentVariable("SCW_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            AnsiConsole.MarkupLine("[yellow]Registry prune skipped:[/] SCW_SECRET_KEY is not set.");
            return;
        }

        var namespaceId = (await RunTerraformAsync(bootstrapModuleFolder, $"output -state={bootstrapState} -raw registry_namespace_id", "read the registry namespace output (has the bootstrap module output been added and applied?)")).Trim();
        if (string.IsNullOrWhiteSpace(namespaceId))
        {
            AnsiConsole.MarkupLine("[yellow]Registry prune skipped:[/] no registry namespace output.");
            return;
        }

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
    ///     Plans the environment module and, when the plan has changes, lists them and applies exactly
    ///     that plan after confirmation (or right away with --auto-approve). Without a terminal and
    ///     without --auto-approve, pending changes stop the deploy.
    /// </summary>
    private static async Task ApplyInfrastructureChangesAsync(string environmentModuleFolder, string terraformEnvironment, string prefix, string projectId, bool autoApprove, string declinedMessage)
    {
        // The tier file is the environment's configuration contract (the domain, sizing,
        // durability); a missing one means the environment is not configured, not that defaults are
        // fine.
        var tierFile = Path.Combine(environmentModuleFolder, "environments", $"{terraformEnvironment}.tfvars");
        if (!File.Exists(tierFile))
        {
            AnsiConsole.MarkupLine($"[red]No tier configuration found at {tierFile}.[/] Every environment must declare its tier file (see environments/stage.tfvars).");
            Environment.Exit(1);
        }

        // An operator's git-ignored <environment>.local.tfvars (a test domain, say) is layered on top
        // of the committed tier file: Terraform lets the later var file win.
        var varFiles = $"-var-file=environments/{terraformEnvironment}.tfvars";
        if (File.Exists(Path.Combine(environmentModuleFolder, "environments", $"{terraformEnvironment}.local.tfvars")))
        {
            varFiles += $" -var-file=environments/{terraformEnvironment}.local.tfvars";
            AnsiConsole.MarkupLine($"[yellow]Using the local overrides in environments/{terraformEnvironment}.local.tfvars.[/]");
        }

        // A saved plan holds every sensitive value in plain text, so it lives in an owner-only
        // folder that is removed even when the deploy exits early.
        var planFolder = Directory.CreateTempSubdirectory($"pp-{terraformEnvironment}-plan-");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteFolder(planFolder);
        var planFile = Path.Combine(planFolder.FullName, "environment.tfplan");

        var plan = await ProcessHelper.ExecuteQuietlyAsync(
            $"terraform plan -detailed-exitcode -input=false -no-color -out={planFile} {varFiles} -var prefix={prefix} -var environment={terraformEnvironment} -var project_id={projectId}",
            environmentModuleFolder,
            ("SCW_DEFAULT_PROJECT_ID", projectId)
        );

        // -detailed-exitcode: 0 = no changes, 1 = error, 2 = changes present.
        if (plan.ExitCode == 0)
        {
            DeleteFolder(planFolder);
            AnsiConsole.MarkupLine("[green]Infrastructure matches the configuration.[/]");
            return;
        }

        if (plan.ExitCode != 2)
        {
            AnsiConsole.MarkupLine("[red]terraform plan failed:[/] " + Markup.Escape(plan.GetErrorSummary("plan")));
            Environment.Exit(plan.ExitCode);
        }

        PrintPlanChanges(plan);

        if (!autoApprove)
        {
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]terraform plan reports pending changes and there is no terminal to confirm them.[/] Pass --auto-approve to apply them, or --skip-plan-check to deploy without them.");
                Environment.Exit(1);
            }

            if (!await AnsiConsole.ConfirmAsync("Apply these changes?", false))
            {
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(declinedMessage)}[/] Run the deploy again to apply them, or pass --skip-plan-check to deploy without them.");
                Environment.Exit(1);
            }
        }

        // Applying the saved plan applies exactly what was listed; if the infrastructure changed
        // since, Terraform refuses the stale plan instead of applying something else.
        ProcessHelper.StartProcess($"terraform apply -input=false {planFile}", environmentModuleFolder, environmentVariables: ("SCW_DEFAULT_PROJECT_ID", projectId));
        DeleteFolder(planFolder);
        AnsiConsole.MarkupLine("[green]Infrastructure changes applied.[/]");
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

    // Terraform outputs carry secrets (the kubeconfig, IAM keys), so they are captured in memory
    // only, and a failure reports stderr alone.
    private static async Task<string> RunTerraformAsync(string workingDirectory, string arguments, string failureHint)
    {
        var result = await ProcessHelper.ExecuteSensitiveAsync($"terraform {arguments}", workingDirectory);
        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]terraform {arguments} failed:[/] " + Markup.Escape(result.StdErr.Trim()));
            AnsiConsole.MarkupLine($"[dim]{failureHint}[/]");
            Environment.Exit(result.ExitCode);
        }

        return result.StdOut;
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
