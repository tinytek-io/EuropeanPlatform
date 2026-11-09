using System.CommandLine;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private const string EdgeServicesApiUrl = "https://api.scaleway.com/edge-services/v1beta1";

    // The Gateway API CRDs and the Cilium version whose Envoy CRDs the cluster gets; bump the Cilium
    // one with Kapsule's Cilium version.
    private const string GatewayApiVersion = "v1.6.1";
    private const string CiliumVersion = "v1.20.2";

    // The staff identity file an environment starts from when it has none; {0} is the Terraform environment.
    private const string EmptyStaffIdentityTemplate = """
                                                      # The {0} staff identity: who may sign in to the back-office, and which tools may sign them in.
                                                      # See README.md for the format. Nobody has a password; sign-in is by an emailed one-time code.
                                                      staff: []
                                                      clients: []

                                                      """;

    private static readonly TimeSpan EdgeCertificateIssuanceTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EdgeCertificatePollInterval = TimeSpan.FromSeconds(20);

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

        var repositoryRoot = Configuration.SourceCodeFolder;
        var environmentModuleFolder = ScalewayEnvironment.EnvironmentModuleFolder;
        var applicationFolder = Path.Combine(repositoryRoot, "application");

        PrintHeader($"Deploying {aspireEnvironment} to Scaleway");

        var (terraformEnvironment, _, _, projectId, credentials) = await ScalewayEnvironment.ConnectAsync(aspireEnvironment);

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

        // Nobody could sign in to an environment whose staff list is empty (there is no password and
        // no break-glass account), so the deploy asks for the first admin.
        EnsureFirstAdmin(repositoryRoot, terraformEnvironment);

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
        PrepareClusterForGateway(kubeconfigPath, repositoryRoot);
        ApplyRegistryPullSecret(kubeconfigPath, registryHost, registryPullSecretKey);
        AdoptHandAppliedNetworkPolicies(kubeconfigPath, repositoryRoot);
        AdmitMigrationJobsUnderTheLivePolicy(kubeconfigPath, repositoryRoot);
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
            ("Database__EndpointPort", databaseEndpointPort),
            // The gateway Service adopts the Terraform-owned Load Balancer (zone/lb-id).
            ("Gateway__LoadBalancerId", await TerraformHelper.OutputAsync(environmentModuleFolder, "gateway_lb_ref"))
        };
        environmentVariables.AddRange(workloadKeys.Select(key => ($"Parameters__{key.Key}-secret-manager-key", key.Value["secret_key"])));
        environmentVariables.AddRange(cockpitEndpoints.Select(endpoint => ($"Cockpit__{endpoint.Key}OtlpEndpoint", endpoint.Value)));
        environmentVariables.Add(("InternalTls__CaPem", internalCaPem));
        environmentVariables.Add(("Hostnames__App", appHostname));
        environmentVariables.Add(("Hostnames__BackOffice", backOfficeHostname));
        environmentVariables.Add(("Email__Sender", $"noreply@{domainName}"));
        // The back-office signs staff in from the environment's committed staff identity file
        // (cloud-infrastructure/staff-identity/<environment>.yaml), which ships with it.
        environmentVariables.Add(("StaffIdentity__Environment", terraformEnvironment));

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

            PrintHeader("Checking the edge certificates");
            await ReissueFailedEdgeCertificatesAsync(terraformEnvironment, projectId, credentials, autoApprove);
        }

        AnsiConsole.MarkupLine($"[green]Deployment of {aspireEnvironment} succeeded.[/] Run [cyan]dotnet run --project developer-cli -- edge-health -e {aspireEnvironment}[/] for a read-only health check of the edge and the public endpoints.");
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
    private static async Task ApplyEnvironmentChangesAsync(string terraformEnvironment, string projectId, bool autoApprove, string declinedMessage, string additionalPlanArguments = "")
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
            $"{varFiles} -var environment={terraformEnvironment} -var project_id={projectId} {additionalPlanArguments}".TrimEnd(),
            autoApprove,
            $"{declinedMessage} Run the deploy again to apply them, or pass --skip-plan-check to deploy without them."
        );
    }

    /// <summary>
    ///     Requests a new managed certificate for every edge pipeline whose issuance failed. Scaleway
    ///     never retries a failed issuance (found live: tls_managed_internal after the CNAME pointed at
    ///     an old project; the stage stays "active" with no certificate), and recreating the TLS stage
    ///     is the only way to request a new one. A stage qualifies only when it has no certificate at
    ///     all and its hostname's CNAME already points at the pipeline — otherwise the new request
    ///     fails the same way. A stage that serves a certificate is never recreated, even when its
    ///     renewal fails: that would drop the working certificate before it expires, so edge-health's
    ///     expiry threshold flags that case for a person instead.
    /// </summary>
    private static async Task ReissueFailedEdgeCertificatesAsync(string terraformEnvironment, string projectId, ScalewayCredentials credentials, bool autoApprove)
    {
        using var edgeHttpClient = new HttpClient();
        edgeHttpClient.DefaultRequestHeaders.Add("X-Auth-Token", credentials.SecretKey);
        edgeHttpClient.Timeout = TimeSpan.FromSeconds(30);

        var failedTlsStages = new List<(string PipelineId, string TlsStageId, string Hostname)>();
        var pipelines = await GetEdgeArrayAsync(edgeHttpClient, $"pipelines?page_size=100&project_id={projectId}", "pipelines");
        foreach (var pipeline in pipelines)
        {
            var pipelineId = pipeline["id"]!.GetValue<string>();
            var issuanceFailed = pipeline["errors"]?.AsArray().Any(error => error?["code"]?.GetValue<string>() == "tls_managed_internal") == true;
            if (!issuanceFailed) continue;

            var tlsStage = (await GetEdgeArrayAsync(edgeHttpClient, $"pipelines/{pipelineId}/tls-stages", "stages")).FirstOrDefault();
            var hasCertificate = tlsStage?["secrets"]?.AsArray().Count > 0;
            if (tlsStage?["managed_certificate"]?.GetValue<bool>() != true || hasCertificate) continue;

            var customDnsStage = (await GetEdgeArrayAsync(edgeHttpClient, $"pipelines/{pipelineId}/dns-stages", "stages"))
                .FirstOrDefault(stage => stage["type"]?.GetValue<string>() == "custom");
            var hostname = customDnsStage?["fqdns"]?.AsArray().FirstOrDefault()?.GetValue<string>();
            var pipelineEndpoint = customDnsStage?["default_fqdn"]?.GetValue<string>().TrimEnd('.');
            if (hostname is null || pipelineEndpoint is null) continue;

            var cname = (await ProcessHelper.ExecuteQuietlyAsync($"dig +short CNAME {hostname}")).StdOut.Trim().TrimEnd('.');
            if (!string.Equals(cname, pipelineEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine($"[yellow]The certificate for {hostname} did not issue, and its CNAME does not point at the pipeline ({pipelineEndpoint}) yet.[/] Fix the record (dns-check lists it) and deploy again; the deploy then requests a new certificate.");
                continue;
            }

            failedTlsStages.Add((pipelineId, tlsStage["id"]!.GetValue<string>(), hostname));
        }

        if (failedTlsStages.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No edge certificate needs a new request.[/]");
            return;
        }

        // The Terraform address of each failed stage comes from the state (edge stage ids carry no
        // region prefix today; the bare uuid is compared in case they ever do).
        var stateJson = await TerraformHelper.RunSensitiveAsync(ScalewayEnvironment.EnvironmentModuleFolder, "show -json", "read the Terraform state");
        using var state = JsonDocument.Parse(stateJson);
        var tlsStageAddresses = state.RootElement.GetProperty("values").GetProperty("root_module").GetProperty("resources").EnumerateArray()
            .Where(resource => resource.GetProperty("type").GetString() == "scaleway_edge_services_tls_stage")
            .ToDictionary(resource => resource.GetProperty("values").GetProperty("id").GetString()!.Split('/').Last(), resource => resource.GetProperty("address").GetString()!);

        var replaceArguments = new List<string>();
        foreach (var (_, tlsStageId, hostname) in failedTlsStages)
        {
            if (!tlsStageAddresses.TryGetValue(tlsStageId.Split('/').Last(), out var address))
            {
                AnsiConsole.MarkupLine($"[red]The TLS stage of {hostname} ({tlsStageId}) is not in the Terraform state.[/] Recreate it by hand in the Scaleway console.");
                Environment.Exit(1);
            }

            AnsiConsole.MarkupLine($"[yellow]The certificate for {hostname} did not issue[/] (tls_managed_internal) although its CNAME points at the pipeline; recreating its TLS stage requests a new one.");
            // The address carries quotes (gateway["back-office"]); escaped for the argument parser.
            replaceArguments.Add($"\"-replace={address.Replace("\"", "\\\"")}\"");
        }

        await ApplyEnvironmentChangesAsync(terraformEnvironment, projectId, autoApprove, "The rollout succeeded, but the failed edge certificates are not requested again.", string.Join(' ', replaceArguments));
        await WaitForEdgeCertificatesAsync(edgeHttpClient, failedTlsStages);
    }

    private static async Task WaitForEdgeCertificatesAsync(HttpClient edgeHttpClient, List<(string PipelineId, string TlsStageId, string Hostname)> requestedTlsStages)
    {
        var pending = requestedTlsStages.ToList();
        var stopwatch = Stopwatch.StartNew();
        AnsiConsole.MarkupLine($"Waiting up to {EdgeCertificateIssuanceTimeout.TotalMinutes:F0} minutes for the new certificate(s)...");
        while (pending.Count > 0 && stopwatch.Elapsed < EdgeCertificateIssuanceTimeout)
        {
            await Task.Delay(EdgeCertificatePollInterval);
            foreach (var requested in pending.ToList())
            {
                var tlsStage = (await GetEdgeArrayAsync(edgeHttpClient, $"pipelines/{requested.PipelineId}/tls-stages", "stages")).FirstOrDefault();
                if (tlsStage?["secrets"]?.AsArray().Count > 0)
                {
                    AnsiConsole.MarkupLine($"[green]The certificate for {requested.Hostname} is issued.[/]");
                    pending.Remove(requested);
                }
            }
        }

        if (pending.Count == 0) return;

        AnsiConsole.MarkupLine($"[red]The rollout succeeded, but the certificate for {string.Join(", ", pending.Select(requested => requested.Hostname))} is still not issued after {EdgeCertificateIssuanceTimeout.TotalMinutes:F0} minutes.[/] Check the pipeline's errors in the Scaleway console; edge-health shows the current state.");
        Environment.Exit(1);
    }

    private static async Task<List<JsonNode>> GetEdgeArrayAsync(HttpClient edgeHttpClient, string path, string propertyName)
    {
        using var response = await edgeHttpClient.GetAsync($"{EdgeServicesApiUrl}/{path}");
        response.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return node?[propertyName]?.AsArray().OfType<JsonNode>().ToList() ?? [];
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
    // Kapsule ships Cilium with its Gateway API controller off and without the Gateway API and Envoy
    // CRDs, and the release's Gateway, HTTPRoutes, and BackendTLSPolicies need all of them. Every step
    // is idempotent, so the deploy converges a fresh cluster and re-converges one after a Kapsule
    // upgrade (which may revert cilium-config). Cilium is restarted only when something changed.
    private static void PrepareClusterForGateway(string kubeconfigPath, string repositoryRoot)
    {
        PrintHeader("Preparing the cluster for the Gateway API");

        var kubectl = $"kubectl --kubeconfig {kubeconfigPath}";
        string[] customResourceDefinitions =
        [
            $"https://github.com/kubernetes-sigs/gateway-api/releases/download/{GatewayApiVersion}/standard-install.yaml",
            $"https://raw.githubusercontent.com/cilium/cilium/{CiliumVersion}/pkg/k8s/apis/cilium.io/client/crds/v2/ciliumenvoyconfigs.yaml",
            $"https://raw.githubusercontent.com/cilium/cilium/{CiliumVersion}/pkg/k8s/apis/cilium.io/client/crds/v2/ciliumclusterwideenvoyconfigs.yaml"
        ];
        var changed = customResourceDefinitions.Aggregate(false, (current, manifest) => KubectlApply(kubectl, $"apply -f {manifest}", "the CRDs from " + manifest) || current);

        var configuration = ProcessHelper.ExecuteQuietly($"{kubectl} get configmap cilium-config -n kube-system -o jsonpath={{.data}}");
        ExitOnFailure(configuration, "Reading cilium-config");
        var gatewaySettings = JsonSerializer.Deserialize<Dictionary<string, string>>(configuration.StdOut)!;
        string[] requiredSettings = ["enable-gateway-api", "enable-gateway-api-secrets-sync", "enable-envoy-config"];
        if (requiredSettings.Any(setting => gatewaySettings.GetValueOrDefault(setting) != "true"))
        {
            // A patch file, not -p: the process runs without a shell, so a quoted JSON argument
            // would not survive argument parsing.
            var patchFile = Path.Combine(Path.GetTempPath(), $"pp-cilium-config-patch-{Guid.NewGuid():N}.json");
            File.WriteAllText(patchFile, JsonSerializer.Serialize(new { data = requiredSettings.ToDictionary(setting => setting, _ => "true") }));
            var patchResult = ProcessHelper.ExecuteQuietly($"{kubectl} patch configmap cilium-config -n kube-system --type merge --patch-file {patchFile}");
            File.Delete(patchFile);
            ExitOnFailure(patchResult, "Enabling Cilium's Gateway API controller");
            changed = true;
        }

        var gatewayApiManifest = Path.Combine(repositoryRoot, "cloud-infrastructure", "scaleway", "cluster-ops", "gateway-api.yaml");
        changed = KubectlApply(kubectl, $"apply -f {gatewayApiManifest}", "the Cilium GatewayClass and RBAC") || changed;

        if (changed)
        {
            AnsiConsole.MarkupLine("  Restarting Cilium so it picks up the Gateway API configuration...");
            ExitOnFailure(ProcessHelper.ExecuteQuietly($"{kubectl} rollout restart deployment/cilium-operator daemonset/cilium -n kube-system"), "Restarting Cilium");
            ExitOnFailure(ProcessHelper.ExecuteQuietly($"{kubectl} rollout status daemonset/cilium -n kube-system --timeout=10m"), "Waiting for Cilium");
            ExitOnFailure(ProcessHelper.ExecuteQuietly($"{kubectl} rollout status deployment/cilium-operator -n kube-system --timeout=10m"), "Waiting for the Cilium operator");
        }

        AnsiConsole.MarkupLine($"  [green]✓[/] Gateway API {GatewayApiVersion} and Cilium's Gateway controller {(changed ? "set up" : "already in place")}");
    }

    /// <summary>Applies a manifest and reports whether it created or changed anything.</summary>
    private static bool KubectlApply(string kubectl, string arguments, string what)
    {
        var result = ProcessHelper.ExecuteQuietly($"{kubectl} {arguments}");
        ExitOnFailure(result, $"Applying {what}");
        return result.StdOut.Split('\n').Any(line => line.EndsWith(" created") || line.EndsWith(" configured"));
    }

    private static void ExitOnFailure(ProcessResult result, string operation)
    {
        if (result.Success) return;

        AnsiConsole.MarkupLine($"[red]{Markup.Escape(operation)} failed:[/] " + Markup.Escape(result.GetErrorSummary("kubectl")));
        Environment.Exit(result.ExitCode == 0 ? 1 : result.ExitCode);
    }

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

    // The staff list is the environment's staff identity file in git (the open-source project ships it
    // empty). An environment whose list has nobody in it could never be signed in to — no staff member
    // has a password, so there is no break-glass account — so the deploy asks for the first admin and
    // writes them into the list, which the operator commits. Further staff are added to the same file.
    private static void EnsureFirstAdmin(string repositoryRoot, string terraformEnvironment)
    {
        var staffPath = Path.Combine(repositoryRoot, "cloud-infrastructure", "staff-identity", $"{terraformEnvironment}.yaml");
        if (!File.Exists(staffPath))
        {
            File.WriteAllText(staffPath, string.Format(EmptyStaffIdentityTemplate, terraformEnvironment));
        }

        var staffIdentity = File.ReadAllText(staffPath);
        if (Regex.IsMatch(staffIdentity, "^  - id:", RegexOptions.Multiline))
        {
            return;
        }

        var relativePath = Markup.Escape(Path.GetRelativePath(repositoryRoot, staffPath));
        AnsiConsole.MarkupLine($"  [yellow]The {terraformEnvironment} staff list ({relativePath}) has nobody in it.[/] Add the first admin; they sign in with an emailed one-time code.");
        if (Console.IsInputRedirected || !Regex.IsMatch(staffIdentity, "^staff: \\[\\]$", RegexOptions.Multiline))
        {
            AnsiConsole.MarkupLine($"[red]The first admin cannot be added here.[/] Add their entry to {relativePath} by hand (see the README beside it), or run the deploy interactively with the empty list the file starts from.");
            Environment.Exit(1);
        }

        var email = AnsiConsole.Prompt(new TextPrompt<string>("  First admin's email:")
            .Validate(value => Regex.IsMatch(value, "^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$") ? ValidationResult.Success() : ValidationResult.Error("Enter an email address."))
        ).Trim().ToLowerInvariant();
        var name = AnsiConsole.Prompt(new TextPrompt<string>("  First admin's full name:")).Trim();

        // The id is the OIDC subject other tools key accounts on (never reused); derived from the name.
        var id = string.Join('.', Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9 ]", "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (id.Length == 0) id = Regex.Replace(email.Split('@')[0], "[^a-z0-9._-]", "");

        // JSON strings are valid YAML double-quoted scalars, so a name with a colon or quote stays one value.
        var adminEntry = $"""
                          staff:
                            - id: {JsonSerializer.Serialize(id)}
                              email: {JsonSerializer.Serialize(email)}
                              name: {JsonSerializer.Serialize(name)}
                              groups: [staff, admins]
                          """;
        // A match evaluator inserts the entry literally: a replacement string would treat a '$' in the name as a group.
        File.WriteAllText(staffPath, Regex.Replace(staffIdentity, "^staff: \\[\\]$", _ => adminEntry, RegexOptions.Multiline));

        AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(email)} added as the first admin (id {Markup.Escape(id)}). Commit {relativePath} in your fork so the next deploy keeps them.");
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

    // The migration Jobs are pre-upgrade hooks: they run under the policies already in the cluster,
    // before the release applies its own. The migrator policy therefore has to select the label
    // every migration Job's pods carry (workload-role: migrator) BEFORE the rollout, or a migrator
    // new in this release gets the namespace's default-deny and times out reaching Secret Manager
    // (found live: back-office-migrator's first deploy). The selector set here is the one the chart
    // sets, so Helm finds the live policy already converged; once a cluster has it, this is a no-op.
    private static void AdmitMigrationJobsUnderTheLivePolicy(string kubeconfigPath, string repositoryRoot)
    {
        const string policyName = "migrators-east-west";
        const string migratorSelector = """{"key":"k8s:workload-role","operator":"In","values":["migrator"]}""";

        var policiesPath = Path.Combine(repositoryRoot, "cloud-infrastructure", "scaleway", "cluster-ops", "network-policies.yaml");
        if (!File.ReadAllText(policiesPath).Contains("""- { key: "k8s:workload-role", operator: In, values: [migrator] }"""))
        {
            AnsiConsole.MarkupLine($"[red]The {policyName} policy in network-policies.yaml no longer selects workload-role: migrator.[/] Update the deploy's pre-rollout selector to match it.");
            Environment.Exit(1);
        }

        var kubectl = $"kubectl --kubeconfig {kubeconfigPath} --namespace default";
        var live = ProcessHelper.ExecuteQuietly($"{kubectl} get ciliumnetworkpolicy {policyName} --ignore-not-found -o jsonpath={{.spec.endpointSelector}}");
        if (!live.Success)
        {
            AnsiConsole.MarkupLine($"[red]Reading the {policyName} network policy failed:[/] " + Markup.Escape(live.GetErrorSummary("kubectl")));
            Environment.Exit(live.ExitCode);
        }

        // Absent on a first install (nothing in the namespace is denied yet), or already converged.
        if (live.StdOut.Trim().Length == 0 || live.StdOut.Contains("k8s:workload-role"))
        {
            return;
        }

        var patch = """{"spec":{"endpointSelector":{"matchExpressions":[{"key":"k8s:io.kubernetes.pod.namespace","operator":"In","values":["default"]},"""
                    + migratorSelector + "]}}}";
        // One quoted argument with escaped quotes: the process argument parser strips bare ones.
        var result = ProcessHelper.ExecuteQuietly($"{kubectl} patch ciliumnetworkpolicy {policyName} --type=merge -p \"{patch.Replace("\"", "\\\"")}\"");
        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]Admitting the migration Jobs under the {policyName} policy failed:[/] " + Markup.Escape(result.GetErrorSummary("kubectl")));
            Environment.Exit(1);
        }

        AnsiConsole.MarkupLine($"  [green]✓[/] {policyName} now admits every migration Job by its workload-role label");
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
