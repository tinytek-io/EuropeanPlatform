using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Hosting.Pipelines;

namespace AppHost.Deployment;

/// <summary>
///     What the cluster needs before the release's Helm rollout, as pipeline steps that <c>helm-deploy-kubernetes</c>
///     waits for. Each step is idempotent: it converges a fresh cluster and is a no-op on one that is already right,
///     so every deploy runs all of them. They act on the cluster in the kubeconfig the deploy command passes.
/// </summary>
internal static class ClusterPreparationSteps
{
    // Aspire's Kubernetes integration names its steps after the environment ("kubernetes" in Program.cs).
    private const string HelmDeployStep = "helm-deploy-kubernetes";

    // The Gateway API CRDs and the Cilium version whose Envoy CRDs the cluster gets; bump the Cilium one with
    // Kapsule's Cilium version.
    private const string GatewayApiVersion = "v1.6.1";
    private const string CiliumVersion = "v1.20.2";

    private const string RegistryPullSecretName = "scaleway-registry-pull";
    private const string MigratorsPolicyName = "migrators-east-west";

    // A Challenge younger than this may belong to an Order cert-manager is still populating.
    private static readonly TimeSpan StaleChallengeMinimumAge = TimeSpan.FromMinutes(10);

    /// <param name="builder">The AppHost builder, in publish mode with the Kubernetes environment added.</param>
    /// <param name="registryHost">The registry the cluster pulls workload images from.</param>
    /// <param name="registryPullSecretKey">
    ///     The read-only registry key the pull secret carries. Only the deploy passes it, so it is required when the
    ///     step runs rather than when the AppHost starts: publishing and listing the steps work without it.
    /// </param>
    public static void AddClusterPreparationSteps(this IDistributedApplicationBuilder builder, string registryHost, string? registryPullSecretKey)
    {
        var clusterOperationsFolder = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "cloud-infrastructure", "scaleway", "cluster-ops"));
        var networkPoliciesPath = Path.Combine(clusterOperationsFolder, "network-policies.yaml");

        builder.Pipeline.AddStep("prepare-gateway-api-kubernetes", context => PrepareGatewayApiAsync(context, clusterOperationsFolder), requiredBy: HelmDeployStep);
        builder.Pipeline.AddStep("apply-registry-pull-secret-kubernetes", context => ApplyRegistryPullSecretAsync(context, registryHost, registryPullSecretKey), requiredBy: HelmDeployStep);
        builder.Pipeline.AddStep("adopt-network-policies-kubernetes", context => AdoptHandAppliedNetworkPoliciesAsync(context, networkPoliciesPath), requiredBy: HelmDeployStep);
        builder.Pipeline.AddStep("admit-migration-jobs-kubernetes", context => AdmitMigrationJobsUnderTheLivePolicyAsync(context, networkPoliciesPath), requiredBy: HelmDeployStep);
        builder.Pipeline.AddStep("clear-stale-certificate-challenges-kubernetes", ClearStaleCertificateChallengesAsync, requiredBy: HelmDeployStep);
    }

    // Kapsule ships Cilium with its Gateway API controller off and without the Gateway API and Envoy CRDs, and the
    // release's Gateway, HTTPRoutes and BackendTLSPolicies need all of them; it also pins Cilium's MTU below the
    // Private Network's (see the mtu setting). A Kapsule upgrade may revert cilium-config, so every deploy
    // re-converges it. Cilium is restarted only when something changed.
    private static async Task PrepareGatewayApiAsync(PipelineStepContext context, string clusterOperationsFolder)
    {
        var cancellationToken = context.CancellationToken;
        var task = await context.ReportingStep.CreateTaskAsync($"Gateway API {GatewayApiVersion} and Cilium's Gateway controller", cancellationToken);

        string[] customResourceDefinitions =
        [
            $"https://github.com/kubernetes-sigs/gateway-api/releases/download/{GatewayApiVersion}/standard-install.yaml",
            $"https://raw.githubusercontent.com/cilium/cilium/{CiliumVersion}/pkg/k8s/apis/cilium.io/client/crds/v2/ciliumenvoyconfigs.yaml",
            $"https://raw.githubusercontent.com/cilium/cilium/{CiliumVersion}/pkg/k8s/apis/cilium.io/client/crds/v2/ciliumclusterwideenvoyconfigs.yaml"
        ];
        var changed = false;
        foreach (var manifest in customResourceDefinitions)
        {
            changed |= Kubectl.AppliedChanges(await Kubectl.RequireAsync($"Applying the CRDs from {manifest}", ["apply", "-f", manifest], cancellationToken));
        }

        var configuration = await Kubectl.RequireAsync("Reading cilium-config", ["get", "configmap", "cilium-config", "-n", "kube-system", "-o", "jsonpath={.data}"], cancellationToken);
        var currentSettings = JsonSerializer.Deserialize<Dictionary<string, string>>(configuration)!;
        var requiredSettings = new Dictionary<string, string>
        {
            ["enable-gateway-api"] = "true",
            ["enable-gateway-api-secrets-sync"] = "true",
            ["enable-envoy-config"] = "true",
            // Cilium treats `mtu` as the underlying network's MTU and subtracts the VXLAN overhead itself for pod
            // routes. Kapsule pins it to 1450, which sizes cilium_host/cilium_net at 1450 while the Private Network
            // carries 1500. Envoy receives the Load Balancer's traffic through those devices, so full 1500-byte
            // packets were dropped silently and requests just over one packet hung until the edge's 60-second
            // timeout. 1500 is the Private Network's real MTU, and tunnel routes become 1450.
            ["mtu"] = "1500"
        };
        if (requiredSettings.Any(setting => currentSettings.GetValueOrDefault(setting.Key) != setting.Value))
        {
            var patch = JsonSerializer.Serialize(new { data = requiredSettings });
            await Kubectl.RequireAsync("Configuring Cilium's Gateway API controller and MTU", ["patch", "configmap", "cilium-config", "-n", "kube-system", "--type", "merge", "-p", patch], cancellationToken);
            changed = true;
        }

        var gatewayApiManifest = Path.Combine(clusterOperationsFolder, "gateway-api.yaml");
        changed |= Kubectl.AppliedChanges(await Kubectl.RequireAsync("Applying the Cilium GatewayClass and RBAC", ["apply", "-f", gatewayApiManifest], cancellationToken));

        if (changed)
        {
            await Kubectl.RequireAsync("Restarting Cilium", ["rollout", "restart", "deployment/cilium-operator", "daemonset/cilium", "-n", "kube-system"], cancellationToken);
            await Kubectl.RequireAsync("Waiting for Cilium", ["rollout", "status", "daemonset/cilium", "-n", "kube-system", "--timeout=10m"], cancellationToken);
            await Kubectl.RequireAsync("Waiting for the Cilium operator", ["rollout", "status", "deployment/cilium-operator", "-n", "kube-system", "--timeout=10m"], cancellationToken);
        }

        await task.SucceedAsync(changed ? "Set up, and Cilium restarted to pick it up" : "Already in place", cancellationToken);
    }

    // The registry namespace is private, and Kapsule nodes have no native registry authentication: the generated
    // chart's imagePullSecrets reference this dockerconfigjson Secret, built from the bootstrap module's read-only
    // pull key. Applied before the release's first pod pulls, so every deploy converges it to the current key. The
    // manifest goes to kubectl on standard input, so the key never touches the disk.
    private static async Task ApplyRegistryPullSecretAsync(PipelineStepContext context, string registryHost, string? registryPullSecretKey)
    {
        if (string.IsNullOrEmpty(registryPullSecretKey))
        {
            throw new InvalidOperationException("Deploying requires Registry:PullSecretKey (the deploy passes the bootstrap module's registry_pull_secret_key).");
        }

        var cancellationToken = context.CancellationToken;
        var task = await context.ReportingStep.CreateTaskAsync($"Registry pull secret {RegistryPullSecretName}", cancellationToken);

        // Scaleway registry authentication: the username is a fixed placeholder, the secret key authenticates.
        var dockerConfigJson = JsonSerializer.Serialize(new { auths = new Dictionary<string, object> { [registryHost] = new { username = "nologin", password = registryPullSecretKey } } });
        var secretManifest = $"""
                              apiVersion: v1
                              kind: Secret
                              metadata:
                                name: {RegistryPullSecretName}
                                namespace: default
                              type: kubernetes.io/dockerconfigjson
                              data:
                                .dockerconfigjson: {Convert.ToBase64String(Encoding.UTF8.GetBytes(dockerConfigJson))}
                              """;
        await Kubectl.RequireAsync("Applying the registry pull secret (without it no pod can pull its image)", ["apply", "-f", "-"], cancellationToken, secretManifest);

        await task.SucceedAsync("Applied", cancellationToken);
    }

    // The network policies ship in the chart, but clusters set up before that carry them applied by hand, and Helm
    // refuses to take over a resource it did not create. Each hand-applied policy is labelled as owned by the release
    // (named after the chart's internal CA ConfigMap, which every release carries), so the upgrade adopts it in
    // place. Deleting them instead would leave the namespace allow-all until Helm recreates them.
    private static async Task AdoptHandAppliedNetworkPoliciesAsync(PipelineStepContext context, string networkPoliciesPath)
    {
        var cancellationToken = context.CancellationToken;
        var task = await context.ReportingStep.CreateTaskAsync("Hand-applied network policies", cancellationToken);

        var policyNames = Regex.Matches(await File.ReadAllTextAsync(networkPoliciesPath, cancellationToken), "^kind: CiliumNetworkPolicy\nmetadata:\n  name: (\\S+)$", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToHashSet();

        var existing = await Kubectl.RequireAsync("Listing the cluster's network policies",
            ["get", "ciliumnetworkpolicies", "-n", "default", "--no-headers", "-o", "custom-columns=NAME:.metadata.name,MANAGED_BY:.metadata.labels.app\\.kubernetes\\.io/managed-by"], cancellationToken
        );
        var handApplied = existing.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(columns => columns[1] != "Helm" && policyNames.Contains(columns[0]))
            .Select(columns => columns[0])
            .ToArray();
        if (handApplied.Length == 0)
        {
            await task.SucceedAsync("None to adopt", cancellationToken);
            return;
        }

        var release = await Kubectl.RunAsync(
            ["get", "configmap", "internal-root-ca-bundle", "-n", "default", "--no-headers", "-o", "custom-columns=NAME:.metadata.annotations.meta\\.helm\\.sh/release-name,NAMESPACE:.metadata.annotations.meta\\.helm\\.sh/release-namespace"],
            null, cancellationToken
        );
        var releaseColumns = release.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (release.ExitCode != 0 || releaseColumns.Length != 2 || releaseColumns.Contains("<none>"))
        {
            throw new InvalidOperationException(
                "The cluster has hand-applied network policies but no release to adopt them into (the internal-root-ca-bundle ConfigMap carries no Helm release name). " +
                "Delete them (kubectl delete cnp --all -n default) and deploy again; the chart recreates them."
            );
        }

        foreach (var policyName in handApplied)
        {
            await Kubectl.RequireAsync($"Labelling the network policy {policyName}", ["label", "ciliumnetworkpolicy", policyName, "-n", "default", "app.kubernetes.io/managed-by=Helm", "--overwrite"], cancellationToken);
            await Kubectl.RequireAsync($"Annotating the network policy {policyName}",
                ["annotate", "ciliumnetworkpolicy", policyName, "-n", "default", $"meta.helm.sh/release-name={releaseColumns[0]}", $"meta.helm.sh/release-namespace={releaseColumns[1]}", "--overwrite"], cancellationToken
            );
        }

        await task.SucceedAsync($"{handApplied.Length} adopted into the release", cancellationToken);
    }

    // The migration Jobs are pre-upgrade hooks: they run under the policies already in the cluster, before the release
    // applies its own. The migrator policy therefore has to select the label every migration Job's pods carry
    // (workload-role: migrator) BEFORE the rollout, or a migrator new in this release gets the namespace's
    // default-deny and times out reaching Secret Manager (found live: back-office-migrator's first deploy). The
    // selector set here is the one the chart sets, so Helm finds the live policy already converged.
    private static async Task AdmitMigrationJobsUnderTheLivePolicyAsync(PipelineStepContext context, string networkPoliciesPath)
    {
        var cancellationToken = context.CancellationToken;
        var task = await context.ReportingStep.CreateTaskAsync($"Migration Jobs under {MigratorsPolicyName}", cancellationToken);

        if (!(await File.ReadAllTextAsync(networkPoliciesPath, cancellationToken)).Contains("""- { key: "k8s:workload-role", operator: In, values: [migrator] }"""))
        {
            throw new InvalidOperationException($"The {MigratorsPolicyName} policy in network-policies.yaml no longer selects workload-role: migrator. Update the selector this step sets to match it.");
        }

        var live = await Kubectl.RequireAsync($"Reading the {MigratorsPolicyName} network policy",
            ["get", "ciliumnetworkpolicy", MigratorsPolicyName, "-n", "default", "--ignore-not-found", "-o", "jsonpath={.spec.endpointSelector}"], cancellationToken
        );

        // Absent on a first install (nothing in the namespace is denied yet), or already converged.
        if (live.Trim().Length == 0 || live.Contains("k8s:workload-role"))
        {
            await task.SucceedAsync("Already admitted", cancellationToken);
            return;
        }

        var patch = JsonSerializer.Serialize(new
            {
                spec = new
                {
                    endpointSelector = new
                    {
                        matchExpressions = new object[]
                        {
                            new { key = "k8s:io.kubernetes.pod.namespace", @operator = "In", values = new[] { "default" } },
                            new { key = "k8s:workload-role", @operator = "In", values = new[] { "migrator" } }
                        }
                    }
                }
            }
        );
        await Kubectl.RequireAsync($"Admitting the migration Jobs under the {MigratorsPolicyName} policy", ["patch", "ciliumnetworkpolicy", MigratorsPolicyName, "-n", "default", "--type=merge", "-p", patch], cancellationToken);

        await task.SucceedAsync("Now admits every migration Job by its workload-role label", cancellationToken);
    }

    // cert-manager owns a Challenge by its issuer, not by its Order, so a Challenge outlives the Order it was created
    // for (a Certificate whose hostnames changed gets a new Order; found live: a removed hostname's challenge kept
    // retrying its self-check, with a solver pod and HTTPRoute, long after the certificate was issued). A Challenge
    // whose ACME URL no live Order lists belongs to nothing; deleting it lets cert-manager's finalizer remove its
    // solver. The age floor keeps the check clear of an Order still being populated.
    private static async Task ClearStaleCertificateChallengesAsync(PipelineStepContext context)
    {
        var cancellationToken = context.CancellationToken;
        var task = await context.ReportingStep.CreateTaskAsync("Stale certificate challenges", cancellationToken);

        var orders = await Kubectl.RunAsync(["get", "orders.acme.cert-manager.io", "--all-namespaces", "-o", "json"], null, cancellationToken);

        // A cluster without cert-manager (yet) has neither resource type, so nothing can be stale.
        if (orders.ExitCode != 0 && orders.StandardError.Contains("the server doesn't have a resource type"))
        {
            await task.SucceedAsync("cert-manager is not installed yet", cancellationToken);
            return;
        }

        if (orders.ExitCode != 0)
        {
            throw new InvalidOperationException($"Reading the cert-manager Orders failed: {orders.StandardError.Trim()}");
        }

        var challenges = await Kubectl.RequireAsync("Reading the cert-manager Challenges", ["get", "challenges.acme.cert-manager.io", "--all-namespaces", "-o", "json"], cancellationToken);

        var listedChallengeUrls = JsonNode.Parse(orders.StandardOutput)!["items"]!.AsArray()
            .SelectMany(order => order?["status"]?["authorizations"]?.AsArray() ?? [])
            .SelectMany(authorization => authorization?["challenges"]?.AsArray() ?? [])
            .Select(challenge => challenge?["url"]?.GetValue<string>())
            .OfType<string>()
            .ToHashSet();

        var staleBefore = DateTimeOffset.UtcNow - StaleChallengeMinimumAge;
        var staleChallenges = JsonNode.Parse(challenges)!["items"]!.AsArray()
            .OfType<JsonNode>()
            .Where(challenge => !listedChallengeUrls.Contains(challenge["spec"]?["url"]?.GetValue<string>() ?? ""))
            .Where(challenge => DateTimeOffset.Parse(challenge["metadata"]!["creationTimestamp"]!.GetValue<string>()) < staleBefore)
            .Select(challenge => (Namespace: challenge["metadata"]!["namespace"]!.GetValue<string>(), Name: challenge["metadata"]!["name"]!.GetValue<string>(), Hostname: challenge["spec"]?["dnsName"]?.GetValue<string>()))
            .ToArray();

        foreach (var (challengeNamespace, name, _) in staleChallenges)
        {
            // --wait=false: cert-manager's finalizer removes the solver first, then releases the Challenge.
            await Kubectl.RequireAsync($"Deleting the stale challenge {name}", ["delete", "challenges.acme.cert-manager.io", name, "--namespace", challengeNamespace, "--wait=false"], cancellationToken);
        }

        await task.SucceedAsync(
            staleChallenges.Length == 0 ? "None" : $"Deleted {staleChallenges.Length} whose order no longer exists: {string.Join(", ", staleChallenges.Select(challenge => challenge.Hostname ?? challenge.Name))}",
            cancellationToken
        );
    }
}
