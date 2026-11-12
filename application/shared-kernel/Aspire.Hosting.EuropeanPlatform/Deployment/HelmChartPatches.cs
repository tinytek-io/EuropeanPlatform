using System.Text.RegularExpressions;
using Aspire.Hosting.EuropeanPlatform.Profiles;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.EuropeanPlatform.Deployment;

/// <summary>
///     Patches the Helm chart the Kubernetes publisher generates, as the final action of <c>publish-kubernetes</c>: by
///     then the chart exists, and every step that consumes it (<c>helm-deploy-kubernetes</c> in a deploy) runs after
///     final actions complete. Each patch fails the publish when the generated layout no longer matches what it
///     expects, so a publisher change surfaces before Helm rather than as a broken rollout.
/// </summary>
/// <param name="builder">The AppHost builder, in publish mode.</param>
/// <param name="internalTlsWorkloads">The workloads that serve traffic in the cluster over internal TLS on 8443.</param>
/// <param name="publicRouteWorkloads">The workloads the public Gateway's routes dial, which Envoy must reach over TLS.</param>
/// <param name="scalewayRegion">The region the network policies admit Scaleway's regional endpoints in.</param>
/// <param name="workloadSizing">Each workload's sizing from the environment type's profile, by resource name.</param>
internal sealed class HelmChartPatches(
    IDistributedApplicationBuilder builder,
    string[] internalTlsWorkloads,
    string[] publicRouteWorkloads,
    string scalewayRegion,
    IReadOnlyDictionary<string, WorkloadSizing> workloadSizing
)
{
    // Generated per self-contained system; the RESOURCE/VALUES_KEY/MEMORY_* tokens are replaced per migrator.
    // The image/config/secret are the migrator's own — a console app that applies schema + data
    // migrations and exits (it has no run mode to select; it is not a worker). Helm's own {{ ... }}
    // braces stay literal (raw string, no interpolation).
    private const string JobTemplate = """
                                       ---
                                       apiVersion: "batch/v1"
                                       kind: "Job"
                                       metadata:
                                         name: "{{ .Chart.Name }}-RESOURCE-job"
                                         labels:
                                           app.kubernetes.io/name: "{{ .Chart.Name }}"
                                           app.kubernetes.io/component: "RESOURCE"
                                           app.kubernetes.io/instance: "{{ .Release.Name }}"
                                         annotations:
                                           # Runs BEFORE any pod of the release rolls out, upgrade or fresh
                                           # install: the upgrade blocks until migrations complete, and a
                                           # failed migration aborts the deploy with the old version still
                                           # serving. The config map and secret below are hook resources
                                           # themselves (applied at weight -2, before this Job at -1) because
                                           # regular release resources are applied after hooks.
                                           "helm.sh/hook": "pre-upgrade,pre-install"
                                           # The previous Job is deleted just before this one is created (default policy).
                                           "helm.sh/hook-delete-policy": "before-hook-creation"
                                           "helm.sh/hook-weight": "-1"
                                       spec:
                                         backoffLimit: 2
                                         # A hung migration fails the Job (and the upgrade, with the old version
                                         # still serving) after 15 minutes instead of blocking until Helm's own timeout.
                                         activeDeadlineSeconds: 900
                                         template:
                                           metadata:
                                             labels:
                                               app.kubernetes.io/name: "{{ .Chart.Name }}"
                                               app.kubernetes.io/component: "RESOURCE"
                                               app.kubernetes.io/instance: "{{ .Release.Name }}"
                                               # The migrator network policy selects this label, not the
                                               # component names: the Job runs before the release's policies
                                               # are applied, so a migrator new in a release is admitted only by
                                               # a policy already in the cluster.
                                               workload-role: "migrator"
                                           spec:
                                             restartPolicy: Never
                                             imagePullSecrets:
                                               - name: "scaleway-registry-pull"
                                             containers:
                                               - name: "RESOURCE"
                                                 image: "{{ .Values.parameters.VALUES_KEY.VALUES_KEY_image }}"
                                                 envFrom:
                                                   - configMapRef:
                                                       name: "RESOURCE-config"
                                                   - secretRef:
                                                       name: "RESOURCE-secrets"
                                                 imagePullPolicy: "IfNotPresent"
                                                 # A migration loads the EF Core model and runs once: the limit leaves room for a
                                                 # large data migration, the low request keeps it schedulable beside a running release.
                                                 resources:
                                                   requests:
                                                     memory: "MEMORY_REQUEST"
                                                   limits:
                                                     memory: "MEMORY_LIMIT"
                                       """;

    // Hook annotations for a migrator's config map and secret (inserted into their metadata): hook
    // resources are applied before the regular release resources and before lower-weight hooks, so
    // the migration Job (weight -1) can reference them even when they are new in this release.
    // before-hook-creation recreates them for every hook run so their values always match the
    // release being deployed.
    private const string HookResourceAnnotations = """
                                                     annotations:
                                                       "helm.sh/hook": "pre-upgrade,pre-install"
                                                       "helm.sh/hook-delete-policy": "before-hook-creation"
                                                       "helm.sh/hook-weight": "-2"
                                                   """;

    public async Task PatchGeneratedChartAsync(PipelineStepContext context)
    {
        var templatesFolder = Path.Combine(builder.AppHostDirectory, "aspire-output", "templates");
        if (!Directory.Exists(templatesFolder))
        {
            throw new InvalidOperationException($"Chart templates folder not found at '{templatesFolder}'.");
        }

        await PatchRolloutStrategyAsync(templatesFolder);
        await AddMigrationJobsAsync(templatesFolder);
        PatchGatewayRoutesForwardedProto(templatesFolder);
        PatchGatewayLoadBalancer(templatesFolder);
        AddInternalTlsManifests(templatesFolder);
        AddNetworkPolicyManifests(templatesFolder);
        PatchWorkloadsForInternalTls(templatesFolder);
        PatchWorkloadReplicas(templatesFolder);
        PatchWorkloadImagePullSecrets(templatesFolder);
    }

    // The east-west network policies (cloud-infrastructure/scaleway/cluster-ops/network-policies.yaml)
    // ship in the chart, so every deploy converges them and a policy removed from the file is removed
    // from the cluster.
    private void AddNetworkPolicyManifests(string templatesFolder)
    {
        var policiesPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "cloud-infrastructure", "scaleway", "cluster-ops", "network-policies.yaml"));
        var policies = NetworkPolicyManifest.Render(policiesPath, RequireDeploySetting("Database:EndpointIp"), RequireDeploySetting("Database:EndpointPort"), scalewayRegion);

        var networkPoliciesFolder = Path.Combine(templatesFolder, "network-policies");
        Directory.CreateDirectory(networkPoliciesFolder);
        File.WriteAllText(Path.Combine(networkPoliciesFolder, "network-policies.yaml"), policies);

        var policyCount = Regex.Matches(policies, "^kind: CiliumNetworkPolicy$", RegexOptions.Multiline).Count;
        Console.WriteLine($"Network policy patch: {policyCount} Cilium network policies added.");
    }

    private string RequireDeploySetting(string key)
    {
        var value = builder.Configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Publish mode requires {key} (the deploy passes it from the environment module's outputs).")
            : value;
    }

    // The internal TLS trust chain, written as chart manifests because the Aspire publisher has no
    // typed support for BackendTLSPolicy: Terraform owns the internal CA and the per-workload leaf
    // certificates (see cloud-infrastructure/scaleway/environment), the leaf key material reaches the
    // workloads through Secret Manager, and the CA — public material — reaches this publish pipeline
    // as the InternalTls:CaPem configuration value. The chart needs it in two places: the ConfigMap
    // Envoy validates against, and (referenced by name) the BackendTLSPolicies.
    private void AddInternalTlsManifests(string templatesFolder)
    {
        var caPem = builder.Configuration["InternalTls:CaPem"];
        if (string.IsNullOrWhiteSpace(caPem))
        {
            throw new InvalidOperationException("InternalTls:CaPem is not set — the deploy command passes the Terraform-managed internal CA certificate. Without it Envoy cannot validate the workloads' TLS.");
        }

        var internalTlsFolder = Path.Combine(templatesFolder, "internal-tls");
        Directory.CreateDirectory(internalTlsFolder);

        var caConfigMap = $"""
                           ---
                           apiVersion: "v1"
                           kind: "ConfigMap"
                           metadata:
                             name: "internal-root-ca-bundle"
                           data:
                             # Cilium's BackendTLSPolicy validation reads the CA from the literal
                             # `ca.crt` key, whatever the policy references.
                             ca.crt: |
                               {caPem.Replace("\n", "\n    ")}
                           """;
        File.WriteAllText(Path.Combine(internalTlsFolder, "internal-ca.yaml"), caConfigMap);

        const string backendTlsPolicyTemplate = """
                                                ---
                                                apiVersion: "gateway.networking.k8s.io/v1"
                                                kind: "BackendTLSPolicy"
                                                metadata:
                                                  name: "WORKLOAD-service-tls"
                                                spec:
                                                  targetRefs:
                                                    - group: ""
                                                      kind: "Service"
                                                      name: "WORKLOAD-service"
                                                  validation:
                                                    hostname: "WORKLOAD-service.default.svc.cluster.local"
                                                    caCertificateRefs:
                                                      - group: ""
                                                        kind: "ConfigMap"
                                                        name: "internal-root-ca-bundle"
                                                """;
        // Envoy needs a policy for each workload a public route dials; everything behind those (the APIs YARP routes
        // to) is dialed by YARP, which trusts the internal CA itself.
        var policies = string.Join("\n", publicRouteWorkloads.Select(workload => backendTlsPolicyTemplate.Replace("WORKLOAD", workload)));
        File.WriteAllText(Path.Combine(internalTlsFolder, "backend-tls-policies.yaml"), policies);

        Console.WriteLine($"Internal TLS patch: CA, {internalTlsWorkloads.Length} leaf certificates, trust bundle, and backend TLS policies added.");
    }

    // The publisher generates probes and port declarations from the publish-mode endpoint, but the
    // generated deployment still carries the plain-HTTP contract (scheme HTTP, port 8080) — this patch
    // completes the HTTPS-only conversion per workload.
    private void PatchWorkloadsForInternalTls(string templatesFolder)
    {
        foreach (var workload in internalTlsWorkloads)
        {
            var deploymentPath = Path.Combine(templatesFolder, workload, "deployment.yaml");
            var yaml = File.ReadAllText(deploymentPath);

            // Probes and the declared port follow the endpoint: HTTPS on 8443.
            yaml = yaml.Replace("scheme: \"HTTP\"", "scheme: \"HTTPS\"");
            yaml = yaml.Replace("port: 8080", "port: 8443");
            yaml = yaml.Replace("- name: \"http\"\n              protocol: \"TCP\"\n              containerPort: 8080",
                "- name: \"https\"\n              protocol: \"TCP\"\n              containerPort: 8443"
            );

            // Fails the publish before helm-deploy-kubernetes runs when the generated layout no longer
            // matches the replacements above (the workload would fail its probes or serve on 8080).
            if (yaml.Contains("scheme: \"HTTP\"") || !yaml.Contains("containerPort: 8443"))
            {
                throw new InvalidOperationException($"Internal TLS patch did not convert {workload} to HTTPS on 8443 — the generated chart layout changed, update the patch.");
            }

            File.WriteAllText(deploymentPath, yaml);
            Console.WriteLine($"Internal TLS patch: {workload} serves HTTPS only on 8443 (probes and ports; the certificate comes from Secret Manager).");
        }
    }

    // Redundancy at the workload tier: each long-running workload runs the replicas its environment type's profile
    // gives its role and class, instead of the generated single pod, so where the profile asks for two a pod loss or a
    // node drain never drops the serving path.
    private void PatchWorkloadReplicas(string templatesFolder)
    {
        var replicated = workloadSizing.Where(workload => workload.Value.Replicas is not null).ToArray();
        foreach (var (workload, sizing) in replicated)
        {
            var deploymentPath = Path.Combine(templatesFolder, workload, "deployment.yaml");
            var yaml = File.ReadAllText(deploymentPath);
            if (!yaml.Contains("  replicas: 1"))
            {
                throw new InvalidOperationException($"Expected 'replicas: 1' in {workload}'s deployment — the generated chart layout changed, update the replicas patch.");
            }

            File.WriteAllText(deploymentPath, yaml.Replace("  replicas: 1", $"  replicas: {sizing.Replicas}"));
        }

        Console.WriteLine($"Replicas patch: {string.Join(", ", replicated.Select(workload => $"{workload.Key} {workload.Value.Replicas}"))}.");
    }

    // The registry namespace is private (zero trust: no anonymous pulls), so every pod that pulls a
    // workload image authenticates through the cluster's dockerconfigjson Secret — the deploy command
    // applies it (idempotently) from the bootstrap module's read-only registry-pull identity. Kapsule
    // nodes have no native registry authentication, and the Secret must exist before the first pod of
    // a release pulls; the name here matches the Secret the deploy command creates.
    private void PatchWorkloadImagePullSecrets(string templatesFolder)
    {
        const string podSpecAnchor = "    spec:\n      containers:";
        const string imagePullSecrets = "    spec:\n      imagePullSecrets:\n        - name: \"scaleway-registry-pull\"\n      containers:";

        var deployments = Directory.GetFiles(templatesFolder, "deployment.yaml", SearchOption.AllDirectories);
        if (deployments.Length == 0)
        {
            throw new InvalidOperationException("No deployment.yaml found in the generated chart — the chart layout changed, update the image-pull-secret patch.");
        }

        foreach (var deployment in deployments)
        {
            var yaml = File.ReadAllText(deployment);
            if (yaml.Contains("imagePullSecrets:"))
            {
                continue;
            }

            if (!yaml.Contains(podSpecAnchor))
            {
                throw new InvalidOperationException($"No pod-spec anchor found in {deployment} — the generated chart layout changed, update the image-pull-secret patch.");
            }

            File.WriteAllText(deployment, yaml.Replace(podSpecAnchor, imagePullSecrets));
        }

        Console.WriteLine($"Image-pull-secret patch: {deployments.Length} deployments pull through scaleway-registry-pull.");
    }

    // The gateway Service Cilium creates for the Gateway must run on the Terraform-owned Load Balancer
    // (stable flexible IP, edge-only ACLs, and the edge's backend stages), not on a new one the Kapsule
    // CCM would create for it. Cilium copies spec.infrastructure.annotations onto that Service, so the
    // adoption holds from the Service's first moment, and externally-managed keeps the CCM from ever
    // deleting the LB.
    private void PatchGatewayLoadBalancer(string templatesFolder)
    {
        var gatewayPath = Path.Combine(templatesFolder, "public-gateway", "public-gateway.yaml");
        var yaml = File.ReadAllText(gatewayPath);
        const string gatewayClassLine = "  gatewayClassName: \"cilium\"\n";
        if (!yaml.Contains(gatewayClassLine) || yaml.Contains("  infrastructure:"))
        {
            throw new InvalidOperationException($"'{gatewayPath}' has no gatewayClassName line or already has an infrastructure block — the generated chart layout changed, update the gateway load balancer patch.");
        }

        var infrastructure = $"""
                                infrastructure:
                                  annotations:
                                    service.beta.kubernetes.io/scw-loadbalancer-id: "{RequireDeploySetting("Gateway:LoadBalancerId")}"
                                    service.beta.kubernetes.io/scw-loadbalancer-externally-managed: "true"

                              """;
        File.WriteAllText(gatewayPath, yaml.Replace(gatewayClassLine, gatewayClassLine + infrastructure));
    }

    // The public requests' TLS terminates twice: at the Scaleway edge (its certificate) and again at
    // the Gateway (the cert-manager certificate — the edge origin connects with TLS through the
    // TCP-passthrough LB, see the edge backend stage). Cilium's Envoy does not set X-Forwarded-Proto
    // when forwarding, so nothing tells the APIs that the request arrived encrypted — and everything
    // from Envoy inward (YARP, the APIs) is plain HTTP on the pod network. The APIs' ForwardedHeaders
    // middleware trusts the pod network and honors that header, and antiforgery's SecurePolicy.Always
    // refuses requests that do not present as SSL (found live: crash-loop on SecurePolicy.Always).
    // The rewrite is honest: every request reaching the Gateway terminated TLS at its https listener,
    // and the Gateway is not reachable from inside the cluster.
    private void PatchGatewayRoutesForwardedProto(string templatesFolder)
    {
        var gatewayRoutesFolder = Path.Combine(templatesFolder, "public-gateway");
        var routeFiles = Directory.GetFiles(gatewayRoutesFolder, "*-route.yaml");
        if (routeFiles.Length == 0)
        {
            throw new InvalidOperationException("No HTTPRoute templates found under public-gateway — the generated chart layout changed, update the forwarded-proto patch.");
        }

        foreach (var routeFile in routeFiles)
        {
            var yaml = File.ReadAllText(routeFile);
            if (yaml.Contains("requestHeaderModifier"))
            {
                continue;
            }

            var backendRefsIndex = yaml.IndexOf("      backendRefs:", StringComparison.Ordinal);
            if (backendRefsIndex < 0)
            {
                throw new InvalidOperationException($"No backendRefs block found in {Path.GetFileName(routeFile)} — the generated chart layout changed, update the forwarded-proto patch.");
            }

            var forwardedProtoFilter = """
                                             filters:
                                               - type: "RequestHeaderModifier"
                                                 requestHeaderModifier:
                                                   set:
                                                     - name: "X-Forwarded-Proto"
                                                       value: "https"
                                       """;
            yaml = yaml.Insert(backendRefsIndex, forwardedProtoFilter + Environment.NewLine);
            File.WriteAllText(routeFile, yaml);
            Console.WriteLine($"Forwarded-proto patch: {Path.GetFileName(routeFile)} restores X-Forwarded-Proto: https.");
        }
    }

    // The publisher rolls Deployments out with maxUnavailable: 1. With single-replica workloads the last ready pod then
    // terminates before its replacement passes readiness, which surfaced as edge 504 windows on every deploy. The patch
    // pins maxUnavailable: 0; maxSurge stays 1.
    private async Task PatchRolloutStrategyAsync(string templatesFolder)
    {
        var deployments = Directory.GetFiles(templatesFolder, "deployment.yaml", SearchOption.AllDirectories);
        if (deployments.Length == 0)
        {
            throw new InvalidOperationException("No deployment.yaml found in the generated chart — the chart layout changed, update the rollout patch.");
        }

        var patched = 0;
        foreach (var deployment in deployments)
        {
            var yaml = await File.ReadAllTextAsync(deployment);
            if (!yaml.Contains("maxUnavailable: 1"))
            {
                continue;
            }

            await File.WriteAllTextAsync(deployment, yaml.Replace("maxUnavailable: 1", "maxUnavailable: 0"));
            patched++;
        }

        // Fails the publish before helm-deploy-kubernetes runs: a deployment that still rolls out with
        // unavailable pods would cause 504 windows on every deploy.
        var unpatched = deployments.Where(deployment => !File.ReadAllText(deployment).Contains("maxUnavailable: 0")).ToArray();
        if (unpatched.Length > 0)
        {
            throw new InvalidOperationException($"Rollout patch did not reach {string.Join(", ", unpatched.Select(Path.GetDirectoryName))} — the generated chart layout changed, update the rollout patch.");
        }

        Console.WriteLine($"Rollout patch: {patched} of {deployments.Length} deployments pinned to maxUnavailable: 0.");
    }

    private async Task AddMigrationJobsAsync(string templatesFolder)
    {
        // One migration Job per migrator workload: the publisher generates a Deployment for every
        // project resource, but a migrator runs to completion and exits — as a Deployment it would
        // crash-loop forever. The patch deletes the generated Deployment and replaces it with a
        // pre-upgrade/pre-install hook Job, and promotes the migrator's config map and secret to
        // hook resources so they exist before the Job's pod starts.
        var migratorWorkloads = Directory.GetDirectories(templatesFolder, "*-migrator");
        if (migratorWorkloads.Length == 0)
        {
            throw new InvalidOperationException("No *-migrator workload found in the generated chart — the chart layout changed, update the migration patch.");
        }

        foreach (var migratorFolder in migratorWorkloads)
        {
            var migrator = Path.GetFileName(migratorFolder); // account-migrator
            File.Delete(Path.Combine(migratorFolder, "deployment.yaml"));

            await PromoteToHookResourceAsync(Path.Combine(migratorFolder, "config.yaml"), migrator);
            await PromoteToHookResourceAsync(Path.Combine(migratorFolder, "secrets.yaml"), migrator);

            if (!workloadSizing.TryGetValue(migrator, out var sizing))
            {
                throw new InvalidOperationException($"The generated chart has a {migrator} the platform did not add, so it has no sizing for its Job.");
            }

            var job = JobTemplate
                .Replace("RESOURCE", migrator)
                .Replace("VALUES_KEY", migrator.Replace('-', '_'))
                .Replace("MEMORY_REQUEST", sizing.MemoryRequest)
                .Replace("MEMORY_LIMIT", sizing.MemoryLimit);

            await File.WriteAllTextAsync(Path.Combine(migratorFolder, "job.yaml"), job);
            Console.WriteLine($"Migration patch: {migrator} Job added (pre-upgrade/pre-install hook).");
        }
    }

    // The publisher emits every workload's config map and secret with a single metadata labels block;
    // the hook annotations are inserted right before it (same indentation).
    private async Task PromoteToHookResourceAsync(string yamlFile, string migrator)
    {
        var yaml = await File.ReadAllTextAsync(yamlFile);
        var labelsIndex = yaml.IndexOf("  labels:", StringComparison.Ordinal);
        if (labelsIndex < 0)
        {
            throw new InvalidOperationException($"No metadata labels block found in {Path.GetFileName(yamlFile)} for {migrator} — the generated chart layout changed, update the migration patch.");
        }

        yaml = yaml.Insert(labelsIndex, HookResourceAnnotations + Environment.NewLine);
        await File.WriteAllTextAsync(yamlFile, yaml);
    }
}
