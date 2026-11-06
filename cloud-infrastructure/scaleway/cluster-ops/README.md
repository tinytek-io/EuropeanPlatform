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
