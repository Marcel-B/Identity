#!/usr/bin/env bash
# Switches monitoring on in an identity container made by create-lxc.sh before monitoring existed:
# /metrics on port 9464 (tailnet only, never through Funnel), log lines to Loki, and the backup stamp
# that identity_backup_last_success_timestamp_seconds reads. Runs again without harm.
#
# Run on the Proxmox host as root, after the monitoring change is on main:
#   bash monitoring-einrichten.sh
#   CTID=105 LOKI_URL=http://monitoring:3100 bash monitoring-einrichten.sh
set -euo pipefail

CTID="${CTID:-}"                             # empty: the container named identity
LOKI_URL="${LOKI_URL:-http://monitoring:3100}" # empty: no log shipping

die() { printf '\033[1;31mError: %s\033[0m\n' "$*" >&2; exit 1; }
[[ $EUID -eq 0 ]] || die "Run as root on the Proxmox host."
command -v pct >/dev/null || die "pct not found. This script must run on a Proxmox VE host."

if [[ -z $CTID ]]; then
    CTID="$(pct list | awk '$NF == "identity" { print $1 }' | head -n1)"
    [[ -n $CTID ]] || die "No container named identity; pass CTID=<id>."
fi

pct exec "$CTID" -- env LOKI_URL="$LOKI_URL" bash -s <<'INNER'
set -euo pipefail
env_file=/etc/identity/identity.env
[[ -f $env_file ]] || { echo "$env_file is missing; is this the identity container?" >&2; exit 1; }

echo "==> Settings in $env_file"
if ! grep -q '^ASPNETCORE_URLS=.*:9464' "$env_file"; then
    sed -i 's#^ASPNETCORE_URLS=\(.*\)$#ASPNETCORE_URLS=\1;http://127.0.0.1:9464#' "$env_file"
fi
sed -i '/^#\?Monitoring__Loki__Url=/d' "$env_file"
if [[ -n $LOKI_URL ]]; then
    echo "Monitoring__Loki__Url=$LOKI_URL" >>"$env_file"
fi
grep -E '^(ASPNETCORE_URLS|Monitoring__)' "$env_file"

echo "==> Backup stamp"
backup=/usr/local/bin/identity-backup
if [[ -f $backup ]] && ! grep -q backup-last-success "$backup"; then
    # The script stops with an error on every failure, so its last line only runs after a full upload.
    printf '%s\n' '# Read by the server for identity_backup_last_success_timestamp_seconds.' \
        'date +%s >/var/lib/identity/backup-last-success' >>"$backup"
fi
grep -c backup-last-success "$backup" >/dev/null && echo "identity-backup writes /var/lib/identity/backup-last-success"

echo "==> Port 9464 in the tailnet"
tailscale serve --bg --http=9464 http://127.0.0.1:9464
tailscale serve status

echo "==> Deploying main"
identity-update

echo "==> Checking"
curl -fsS http://127.0.0.1:9464/metrics | grep -m1 '^identity_users' || { echo "/metrics on 9464 does not answer." >&2; exit 1; }
status="$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8080/metrics)"
[[ $status == 404 ]] || { echo "/metrics answers on 8080 too (HTTP $status)." >&2; exit 1; }
echo "8080 refuses /metrics (404), as it should."
if [[ -n $LOKI_URL ]]; then
    curl -fsS -o /dev/null "$LOKI_URL/ready" && echo "Loki answers at $LOKI_URL." || echo "Loki does not answer at $LOKI_URL yet." >&2
fi

dns="$(tailscale status --json | python3 -c 'import json,sys; print(json.load(sys.stdin)["Self"]["DNSName"].rstrip("."))')"
echo
echo "Done. On the Proxmox host, register the target with the monitoring stack:"
echo "  curl -fsSL https://codeload.github.com/Marcel-B/Tonwerk-AI/tar.gz/main | tar -xz --strip-components=2 -C /root Tonwerk-AI-main/deploy/monitoring"
echo "  APP_TARGETS=\"identity=$dns:9464\" /root/monitoring/proxmox-lxc.sh"
echo "The backup value appears after the next successful backup (tonight, or now: identity-backup)."
INNER
