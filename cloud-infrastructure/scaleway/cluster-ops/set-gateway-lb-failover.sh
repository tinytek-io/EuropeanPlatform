#!/bin/zsh
# Wires the Load Balancer's failover host (customized error page) for the Cilium gateway
# service: the Scaleway LB serves a static bucket website when ALL backends fail their
# health check (cluster unreachable). Idempotent; derives all IDs live, so it survives
# node replacement and service/LB recreation.
#
# Why not Terraform: the LB backend is created and reconciled by the Kapsule
# cloud-controller-manager (pool = node IPs, health checks, IDs derived from the service
# UID). A second owner in Terraform would fight the controller and reference dead IDs
# after every recreation.
#
# Usage (from this directory):
#   KUBECONFIG=... ./set-gateway-lb-failover.sh
# Optional overrides:
#   SERVICE_NAME (default: cilium-gateway-public-gateway)
#   SERVICE_PORT (default: 80)
#   FAILOVER_HOST (default: pp-stage-maintenance.s3-website.fr-par.scw.cloud)

set -euo pipefail

SERVICE_NAME="${SERVICE_NAME:-cilium-gateway-public-gateway}"
SERVICE_PORT="${SERVICE_PORT:-80}"
FAILOVER_HOST="${FAILOVER_HOST:-pp-stage-maintenance.s3-website.fr-par.scw.cloud}"

if [[ -z "${SCW_SECRET_KEY:-}" ]]; then
  echo "SCW_SECRET_KEY is required (the scw CLI profile key works)." >&2
  exit 1
fi

# 1. The failover feature requires an HTTP-typed backend; the CCM creates TCP by default.
echo "Annotating service ${SERVICE_NAME} (protocol-http on port ${SERVICE_PORT})..."
kubectl annotate svc "${SERVICE_NAME}" \
  "service.beta.kubernetes.io/scw-loadbalancer-protocol-http=${SERVICE_PORT}" --overwrite >/dev/null

# 2. Locate the Load Balancer the CCM created for the service.
LB_REF=$(kubectl get svc "${SERVICE_NAME}" -o jsonpath='{.metadata.annotations.service\.beta\.kubernetes\.io/scw-loadbalancer-id}')
if [[ -z "${LB_REF}" ]]; then
  echo "Service has no scw-loadbalancer-id annotation yet; retry in a moment." >&2
  exit 1
fi
LB_ZONE="${LB_REF%%/*}"
LB_ID="${LB_REF##*/}"
echo "Load Balancer: ${LB_REF}"

# 3. Find the backend for the service port (the frontend name embeds the service UID + port).
FRONTENDS=$(kubectl get svc "${SERVICE_NAME}" -o jsonpath='{.metadata.uid}')
BACKEND_ID=$(curl -s -H "X-Auth-Token: ${SCW_SECRET_KEY}" \
  "https://api.scaleway.com/lb/v1/zones/${LB_ZONE}/lbs/${LB_ID}/frontends" |
  python3 -c "import json,sys; d=json.load(sys.stdin); print(d['frontends'][0]['backend']['id'])")
echo "Backend: ${BACKEND_ID}"

# 4. Merge the failover host into the current backend definition. The API validates the
#    request, not the stored state, so forward_protocol must be present (and http).
REGION="fr-par"
curl -s -H "X-Auth-Token: ${SCW_SECRET_KEY}" \
  "https://api.scaleway.com/lb/v1/regions/${REGION}/backends/${BACKEND_ID}" |
  python3 -c "
import json, sys
d = json.load(sys.stdin)
if d.get('failover_host') == '${FAILOVER_HOST}':
    print('failover_host already set; nothing to do.')
    sys.exit(0)
if d.get('forward_protocol') != 'http':
    print('backend forward_protocol is', d.get('forward_protocol'),
          '- the CCM annotation from step 1 should flip it; re-run in a moment.', file=sys.stderr)
    sys.exit(1)
body = {
    'name': d['name'],
    'forward_protocol': d['forward_protocol'],
    'forward_port': d['forward_port'],
    'forward_port_algorithm': d['forward_port_algorithm'],
    'sticky_sessions': d['sticky_sessions'],
    'sticky_sessions_cookie_name': d.get('sticky_sessions_cookie_name', ''),
    'health_check': d['health_check'],
    'pool': d['pool'],
    'failover_host': '${FAILOVER_HOST}',
}
print(json.dumps(body))
" > /tmp/pp-failover-merge.json
if [[ "$(cat /tmp/pp-failover-merge.json)" != *'{'* ]]; then
  cat /tmp/pp-failover-merge.json
  exit 0
fi

RESPONSE=$(curl -s -X PUT -H "X-Auth-Token: ${SCW_SECRET_KEY}" -H "Content-Type: application/json" \
  -d @/tmp/pp-failover-merge.json \
  "https://api.scaleway.com/lb/v1/regions/${REGION}/backends/${BACKEND_ID}")
rm -f /tmp/pp-failover-merge.json
echo "${RESPONSE}" | python3 -c "
import json, sys
d = json.load(sys.stdin)
if d.get('message'):
    print('API error:', d.get('message'), d.get('details'), file=sys.stderr)
    sys.exit(1)
print('failover_host set to:', d.get('failover_host'))
"
