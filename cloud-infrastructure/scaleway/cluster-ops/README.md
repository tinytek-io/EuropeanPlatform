# Cluster-ops (per-cluster, once)

Steps that live OUTSIDE the Terraform modules and the Aspire Helm release. They bootstrap the
Kapsule cluster so it can terminate the application's Gateway API traffic. Run these once per
cluster (staging today, and again for production/review clusters):

## 1. Gateway API CRDs

```bash
kubectl apply -f https://github.com/kubernetes-sigs/gateway-api/releases/download/v1.6.1/standard-install.yaml
```

## 2. Cilium Envoy CRDs (Kapsule does not install these)

Cilium 1.20's Gateway controller needs the Envoy config CRDs; the cilium-agent blocks startup of
the CEC watcher until they exist.

```bash
kubectl apply -f https://raw.githubusercontent.com/cilium/cilium/v1.20.2/pkg/k8s/apis/cilium.io/client/crds/v2/ciliumenvoyconfigs.yaml
kubectl apply -f https://raw.githubusercontent.com/cilium/cilium/v1.20.2/pkg/k8s/apis/cilium.io/client/crds/v2/ciliumclusterwideenvoyconfigs.yaml
```

## 3. Enable Cilium's Gateway API controller

Kapsule ships Cilium with `enable-gateway-api` and `enable-envoy-config` off, and without the
operator RBAC for `gateway.networking.k8s.io`:

```bash
kubectl patch cm cilium-config -n kube-system --type merge \
  -p '{"data":{"enable-gateway-api":"true","enable-gateway-api-secrets-sync":"true","enable-envoy-config":"true"}}'
kubectl apply -f gateway-api.yaml
kubectl rollout restart deployment/cilium-operator ds/cilium -n kube-system
```

Caveat: `cilium-config` and the Cilium deployments are Kapsule-managed. A Kapsule Cilium upgrade
may revert them — re-run this step (and re-check Gateway status) after any cluster version bump.

## 4. cert-manager — NO LONGER MANUAL

cert-manager (upstream v1.20.2, CRDs + Gateway API shim enabled) and the production Let's Encrypt
ClusterIssuer are owned by the Aspire chart (`application/AppHost/Program.cs`: `AddCertManager` +
`AddIssuer("letsencrypt").WithLetsEncryptProduction`). The deploy installs/updates them; do NOT
apply cert-manager or an issuer here — the chart's Helm step fails if a manual install already
owns the CRDs (found live: "CustomResourceDefinition ... exists and cannot be imported into the
current release").

## 5. Gateway LB adoption (TF-owned shell, CCM-managed backend)

The gateway Load Balancer is Terraform-owned (`../environment/main.tf`:
`scaleway_lb.gateway` + `scaleway_lb_ip.gateway` — the flexible IP survives LB recreation).
Adopt it into the Cilium gateway Service after the first deploy (the CCM then creates the
frontend/backend on it and never deletes the LB):

```bash
LB_REF=$(terraform output -raw gateway_lb_ref)   # zone/lb-id
kubectl annotate svc cilium-gateway-public-gateway \
  "service.beta.kubernetes.io/scw-loadbalancer-id=${LB_REF}" \
  'service.beta.kubernetes.io/scw-loadbalancer-externally-managed=true' \
  --overwrite
kubectl annotate svc cilium-gateway-public-gateway \
  'service.beta.kubernetes.io/scw-loadbalancer-protocol-http=80' --overwrite
```

The LB name must NOT start with the Kapsule cluster id (the CCM refuses such LBs in
externally-managed mode). The CCM still owns the frontend/backend on the adopted LB — that is
by design (the pool contains node IPs) — so `set-gateway-lb-failover.sh` still applies.

## 6. Verify

```bash
kubectl get gatewayclass cilium
kubectl get clusterissuer letsencrypt            # owned by the Aspire chart
kubectl get deploy -n cert-manager-chart        # chart-installed cert-manager
kubectl get gateway public-gateway   # ADDRESS + Programmed=True once the LB is up
kubectl get svc cilium-gateway-public-gateway -o jsonpath='{.status.loadBalancer.ingress[0].ip}'
```

## 6b. cert-manager manual-install cleanup (one-time)

If a cert-manager was applied by hand BEFORE the chart owned it (staging's history), every
deploy's `helm-install-cert-manager-chart` step fails with "CustomResourceDefinition ... exists
and cannot be imported into the current release" — the app rollout still succeeds, but the
pipeline is noisy and the chart's cert-manager never becomes the live one. Recovery (restores
pipeline-intended state, so it is an allowed hand-fix):

```bash
helm list -A | grep -i cert-manager           # a hand-installed release shows here
kubectl get deploy -A | grep cert-manager     # note the namespace the hand install used
helm uninstall <release> -n <namespace>       # if helm-owned
# ...or, if applied as raw manifests: kubectl delete -f <the manifests used>
kubectl delete issuer <manual-issuer> -n default 2>/dev/null
```

The next deploy re-installs cert-manager + the production issuer from the chart and the step
goes green. Verify with the §6 checks plus one deploy.

## 6c. Registry pull secret (per environment, once — then deploy-owned)

The registry namespace is private; Kapsule nodes have no native registry authentication. The
deploy command applies the `scaleway-registry-pull` dockerconfigjson Secret (from the bootstrap
module's read-only registry-pull identity) on every deploy, and the generated chart's workloads
reference it via `imagePullSecrets`. Nothing to do by hand as long as deploys run; after the
first deploy with the secret, verify pulls authenticate:

```bash
kubectl get secret scaleway-registry-pull -n default
kubectl run pull-test --rm -it --image=rg.fr-par.scw.cloud/<namespace>/<image>:<tag> --restart=Never -- command -v true
```

## 7. East-west network policies (default-deny + derived allows)

`network-policies.yaml` enforces zero-trust east-west: default-deny in and out for every
workload in `default`, with only the traffic the AppHost wiring declares (see the file's header
for the per-workload map). The RDB placeholders are resolved from Terraform outputs first:

```bash
cd ../environment
RDB_IP=$(terraform output -raw database_endpoint_ip)
RDB_PORT=$(terraform output -raw database_endpoint_port)
cd ../cluster-ops
sed -e "s/<rdb-ip>/$RDB_IP/" -e "s/<rdb-port>/$RDB_PORT/" network-policies.yaml | kubectl apply -f -
```

Verify (in this order — the site must stay green throughout):

```bash
kubectl get cnp -n default                                    # all policies present
curl -sI https://app.staging.europeanplatform.net | head -1   # public path unchanged
kubectl logs -n default deploy/account-api --tail=20          # no secret-manager/DB errors
kubectl rollout status deploy/account-api -n default
# Prove the deny actually denies: a pod that is NOT in an allow path cannot reach an API.
kubectl run deny-test --rm -it --image=busybox --restart=Never -- wget -qO- --timeout=5 https://account-api-service:8443 || echo "denied (correct)"
```

The `toFQDNs` rules depend on Cilium's DNS proxy. If Cockpit/Secret Manager lookups fail under
policy (Kapsule's managed Cilium config is the unknown), the interim fallback is `toEntities:
[world]` + the same port scoping (weaker: any IP on those ports) until the DNS proxy is proven.

Rollback restores the pre-policy state exactly (Cilium without policies is allow-all):

```bash
kubectl delete -f network-policies.yaml
```

## Known gap: the Kubernetes API server

The pinned Terraform provider (scaleway ~> 2.86) has no apiserver access-restriction attributes,
so the cluster's API server is publicly reachable with no allow-list. Mitigation: kubeconfig is
the only credential; revisit on a provider upgrade.

