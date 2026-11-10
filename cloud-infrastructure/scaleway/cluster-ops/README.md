# Cluster-ops (per-cluster, once)

Steps that live OUTSIDE the Terraform modules and the Aspire Helm release. They bootstrap the
Kapsule cluster so it can terminate the application's Gateway API traffic. Run these once per
cluster (staging today, and again for production/review clusters):

## 1–3. Gateway API CRDs, Cilium Envoy CRDs, Cilium's Gateway controller — NO LONGER MANUAL

Kapsule ships Cilium with its Gateway API controller off (`enable-gateway-api`,
`enable-gateway-api-secrets-sync`, `enable-envoy-config`), without the operator RBAC for
`gateway.networking.k8s.io`, and without the Gateway API and Cilium Envoy CRDs. The deploy
converges all of it before every rollout, in the AppHost's `prepare-gateway-api-kubernetes` pipeline
step (`application/AppHost/Deployment/ClusterPreparationSteps.cs`): it applies the Gateway API
standard CRDs and the Cilium Envoy CRDs (versions pinned in the step), sets the `cilium-config` keys
(the Gateway API controller and the MTU), applies `gateway-api.yaml` (GatewayClass and RBAC), and
restarts Cilium only when something changed. A Kapsule Cilium upgrade that reverts `cilium-config` is undone by the next
deploy. Bump the pinned Cilium version with Kapsule's.

## 4. cert-manager — NO LONGER MANUAL

cert-manager (upstream v1.20.2, CRDs + Gateway API shim enabled) and the production Let's Encrypt
ClusterIssuer are owned by the Aspire chart (`application/AppHost/Program.cs`: `AddCertManager` +
`AddIssuer("letsencrypt").WithLetsEncryptProduction`). The deploy installs/updates them; do NOT
apply cert-manager or an issuer here — the chart's Helm step fails if a manual install already
owns the CRDs (found live: "CustomResourceDefinition ... exists and cannot be imported into the
current release").

## 5. Gateway LB adoption — NO LONGER MANUAL

The gateway Load Balancer is Terraform-owned (`../environment/main.tf`: `scaleway_lb.gateway` +
`scaleway_lb_ip.gateway` — the flexible IP survives LB recreation). The AppHost writes the adoption
annotations into the Gateway's `spec.infrastructure.annotations`, which Cilium copies onto the
`cilium-gateway-public-gateway` Service it creates, so the Service runs on that LB from its first
moment and the CCM never creates one of its own: `scw-loadbalancer-id` (the deploy passes the
`gateway_lb_ref` output), `scw-loadbalancer-externally-managed=true` (the CCM never deletes the LB),
and `scw-loadbalancer-protocol-http=80`. The CCM still owns the frontends and backends on the
adopted LB — by design (the pool contains node IPs). `set-gateway-lb-failover.sh` (the maintenance
page for direct :80 hits on the LB IP) is still a manual, optional step.

The LB name must NOT start with the Kapsule cluster id (the CCM refuses such LBs in
externally-managed mode).

## 5b. The CCM-created LB frontend and backend (no capture needed)

The CCM owns the frontends and backends it creates on the adopted LB; Terraform owns only the
LB shell and its flexible IP. The edge configuration builds on two of the CCM's resources — the
app pipeline's backend stage targets the CCM's 443 frontend, and the Terraform-owned 8443/8444
frontends forward to the CCM's Envoy TCP backend — and the environment module reads both from the
LB (`data.scaleway_lb_frontends.gateway`: the frontend on port 443, and its backend). Nothing is
captured or exported.

They exist only once a deploy has created the annotated gateway Service, so an environment's very
first plan cannot resolve them. The CCM names its resources `<gateway-service-uid>_tcp_<port>`;
anything named `<prefix>-<environment>-*` is Terraform-owned. If the plan cannot find the 443
frontend after a deploy, the §5 adoption annotations did not take — check the Service's annotations (`kubectl get svc cilium-gateway-public-gateway -o yaml`) and redeploy.

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
deploy applies the `scaleway-registry-pull` dockerconfigjson Secret (from the bootstrap module's
read-only registry-pull identity) on every deploy, in the AppHost's
`apply-registry-pull-secret-kubernetes` pipeline step, and the generated chart's workloads
reference it via `imagePullSecrets`. Nothing to do by hand as long as deploys run; after the
first deploy with the secret, verify pulls authenticate:

```bash
kubectl get secret scaleway-registry-pull -n default
kubectl run pull-test --rm -it --image=rg.<region>.scw.cloud/<namespace>/<image>:<tag> --restart=Never -- command -v true
```

## 7. East-west network policies (default-deny + derived allows) — NO LONGER MANUAL

`network-policies.yaml` enforces zero-trust east-west: a namespace-wide baseline
(`default-namespace-default-deny`) puts every pod in `default` into default-deny in and out —
only kubelet probes and DNS are admitted — so a pod without its own policy (a debug pod, a new
system) is isolated rather than allow-all. The per-workload policies add exactly the traffic the
AppHost wiring declares on top (Cilium allows are additive; see the file's header for the
per-workload map). Envoy reaches the serving workloads as the reserved `ingress` entity, on 8443
only.

The policies ship in the Aspire chart: the AppHost resolves the file's placeholders (database
endpoint, region) from the values the deploy passes from the Terraform
outputs, so `deploy` converges them and a policy removed from the file is removed from the
cluster. Do not `kubectl apply` the file by hand. Policies applied by hand before the chart owned
them are adopted into the release by the deploy (labelled and annotated in place, never deleted,
so the namespace is never allow-all during the switch).

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

Rollback: remove the policy from the file and deploy. In an emergency, `kubectl delete cnp --all
-n default` restores Cilium's allow-all default until the next deploy recreates them.

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
