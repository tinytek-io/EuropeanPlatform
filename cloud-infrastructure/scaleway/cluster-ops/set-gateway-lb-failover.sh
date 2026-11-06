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
# LIMITATION: the failover host is an HTTP-backend feature, so this only covers the backend
# behind the plain-HTTP frontend (SERVICE_PORT, :80 by default). Public traffic does NOT take
# that path: the Edge Services pipelines reach the cluster through the TCP passthrough frontends
# (the CCM's 443 frontend and the Terraform-owned back-office 8443 frontend), whose shared Envoy
# TCP backend stays tcp-typed because Envoy terminates TLS — it cannot carry a failover host. When
# the cluster is down, edge visitors get the edge's own origin error, not this maintenance page;
# only direct HTTP hits on the LB's :80 frontend are redirected.
#
# Usage (from this directory, with the environment module's state readable):
#   KUBECONFIG=... SCW_SECRET_KEY=... ./set-gateway-lb-failover.sh
# Defaults derive from `terraform -chdir=../environment output` (region,
# maintenance_website_endpoint); optional overrides:
#   SERVICE_NAME (default: cilium-gateway-public-gateway)
#   SERVICE_PORT (default: 80)
#   REGION (default: the `region` output)
#   FAILOVER_HOST (default: the `maintenance_website_endpoint` output)

set -euo pipefail

SCRIPT_DIR="${0:A:h}"
ENVIRONMENT_DIR="${SCRIPT_DIR}/../environment"

SERVICE_NAME="${SERVICE_NAME:-cilium-gateway-public-gateway}"
SERVICE_PORT="${SERVICE_PORT:-80}"
REGION="${REGION:-$(terraform -chdir="${ENVIRONMENT_DIR}" output -raw region)}"
FAILOVER_HOST="${FAILOVER_HOST:-$(terraform -chdir="${ENVIRONMENT_DIR}" output -raw maintenance_website_endpoint)}"

if [[ -z "${SCW_SECRET_KEY:-}" ]]; then
  echo "SCW_SECRET_KEY is required (the scw CLI profile key works)." >&2
  exit 1
fi

MERGE_FILE=$(mktemp)
trap 'rm -f "${MERGE_FILE}"' EXIT

echo "Region: ${REGION}; failover host: ${FAILOVER_HOST}"

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

# 3. Find the backend behind the frontend listening on the service port. Selecting by port, not
#    list position: the LB also carries the 443 frontend and the Terraform-owned 8443 frontend.
BACKEND_ID=$(curl -s -H "X-Auth-Token: ${SCW_SECRET_KEY}" \
  "https://api.scaleway.com/lb/v1/zones/${LB_ZONE}/lbs/${LB_ID}/frontends" |
  SERVICE_PORT="${SERVICE_PORT}" python3 -c "
import json, os, sys
port = int(os.environ['SERVICE_PORT'])
matches = [f for f in json.load(sys.stdin).get('frontends', []) if f.get('inbound_port') == port]
if len(matches) != 1:
    print(f'expected exactly one frontend on port {port}, found {len(matches)}', file=sys.stderr)
    sys.exit(1)
print(matches[0]['backend']['id'])
")
echo "Backend: ${BACKEND_ID}"

# 4. Merge the failover host into the current backend definition. The API validates the
#    request, not the stored state, so forward_protocol must be present (and http).
curl -s -H "X-Auth-Token: ${SCW_SECRET_KEY}" \
  "https://api.scaleway.com/lb/v1/regions/${REGION}/backends/${BACKEND_ID}" |
  FAILOVER_HOST="${FAILOVER_HOST}" python3 -c "
import json, os, sys
failover_host = os.environ['FAILOVER_HOST']
d = json.load(sys.stdin)
if d.get('failover_host') == failover_host:
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
    'failover_host': failover_host,
}
print(json.dumps(body))
" > "${MERGE_FILE}"
if [[ "$(cat "${MERGE_FILE}")" != *'{'* ]]; then
  cat "${MERGE_FILE}"
  exit 0
fi

RESPONSE=$(curl -s -X PUT -H "X-Auth-Token: ${SCW_SECRET_KEY}" -H "Content-Type: application/json" \
  -d @"${MERGE_FILE}" \
  "https://api.scaleway.com/lb/v1/regions/${REGION}/backends/${BACKEND_ID}")
echo "${RESPONSE}" | python3 -c "
import json, sys
d = json.load(sys.stdin)
if d.get('message'):
    print('API error:', d.get('message'), d.get('details'), file=sys.stderr)
    sys.exit(1)
print('failover_host set to:', d.get('failover_host'))
"
