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

## 5b. Capture the CCM-created LB frontend/backend ids (after the first deploy)

The CCM owns the frontends and backends it creates on the adopted LB; Terraform owns only the
LB shell and its flexible IP. The edge configuration needs two of those CCM-created ids as
INPUTS — the app pipeline's backend stage targets the CCM's 443 frontend, and the TF-created
back-office 8443 frontend targets the CCM's Envoy TCP backend — so capture them once per
cluster, after the first deploy has created the annotated gateway Service:

```bash
LB_REF=$(terraform -chdir=../environment output -raw gateway_lb_ref)   # zone/lb-id
LB_ZONE=${LB_REF%%/*}
LB_ID=${LB_REF##*/}                                                   # the scw CLI wants the bare id

scw lb frontend list "lb-id=${LB_ID}" "zone=${LB_ZONE}" -o json \
  | jq -r '.[] | select(.inbound_port == 443) | .id'                  # -> gateway_lb_frontend_id
scw lb backend list "lb-id=${LB_ID}" "zone=${LB_ZONE}" -o json \
  | jq -r --arg zone "${LB_ZONE}" \
      '.[] | select(.forward_protocol == "tcp") | "\($zone)/\(.id)"'  # -> gateway_lb_backend_id
```

Export both as `TF_VAR_gateway_lb_frontend_id` and `TF_VAR_gateway_lb_backend_id` (they are
cluster-specific and never committed) and apply — the edge backend stages reference them.

The CCM names its resources `<gateway-service-uid>_tcp_<port>`; anything named
`<prefix>-<environment>-*` is Terraform-owned. If the 443 frontend or the TCP backend is
missing after a deploy, the §5 adoption annotations did not take — re-run §5, redeploy, and
capture again.

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
kubectl run pull-test --rm -it --image=rg.<region>.scw.cloud/<namespace>/<image>:<tag> --restart=Never -- command -v true
```

## 7. East-west network policies (default-deny + derived allows)

`network-policies.yaml` enforces zero-trust east-west: a namespace-wide baseline
(`default-namespace-default-deny`) puts every pod in `default` into default-deny in and out —
only kubelet probes and DNS are admitted — so a pod without its own policy (a debug pod, a new
system) is isolated rather than allow-all. The per-workload policies add exactly the traffic the
AppHost wiring declares on top (Cilium allows are additive; see the file's header for the
per-workload map). Envoy reaches the serving workloads as the reserved `ingress` entity, on 8443
only. The RDB and region placeholders are resolved from Terraform outputs first:

```bash
RDB_IP=$(terraform -chdir=../environment output -raw database_endpoint_ip)
RDB_PORT=$(terraform -chdir=../environment output -raw database_endpoint_port)
REGION=$(terraform -chdir=../environment output -raw region)
sed -e "s/<rdb-ip>/$RDB_IP/g" -e "s/<rdb-port>/$RDB_PORT/g" -e "s/<region>/$REGION/g" \
  network-policies.yaml | kubectl apply -f -
```

Verify (in this order — the site must stay green throughout):

```bash
kubectl get cnp -n default                                    # all policies present
curl -sI "https://$(terraform -chdir=../environment output -json edge_hostnames | jq -r .app)" | head -1   # public path unchanged
# Envoy -> workload traffic must show as the `ingress` identity and be FORWARDED, not DROPPED
# (the file's "verify on first apply" caveat; fallback is the kube-system selector on 8443).
kubectl -n kube-system exec ds/cilium -- hubble observe --identity ingress --last 20
kubectl logs -n default deploy/account-api --tail=20          # no secret-manager/DB errors
kubectl rollout status deploy/account-api -n default
# Prove the deny actually denies: a pod that is NOT in an allow path cannot reach an API.
kubectl run deny-test --rm -it --image=busybox --restart=Never -- wget -qO- --timeout=5 https://account-api-service:8443 || echo "denied (correct)"
# Prove the namespace baseline: a pod no policy names has no egress beyond DNS.
kubectl run egress-test --rm -it --image=busybox --restart=Never -- wget -qO- --timeout=5 https://api.scaleway.com || echo "denied (correct)"
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

## Known gap: operator access to the database

The managed PostgreSQL instance sits on the private network only (no public endpoint), and the
network policies allow database egress from the application workloads and migrators alone. There
is therefore no documented operator path to the database (break-glass queries, manual restores).
When one is needed, add it deliberately: a short-lived `kubectl run` pod labelled for a dedicated
policy that allows only the RDB endpoint, or a bastion on the private network — never by widening
an application workload's policy. The admin credential stays in Terraform state
(`database_admin_user` / `database_admin_password` outputs).
