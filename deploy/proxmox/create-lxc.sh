#!/usr/bin/env bash
# Creates a Proxmox LXC container that runs the identity server, reachable over Tailscale with HTTPS.
#
# Run on the Proxmox host as root:
#   TS_AUTHKEY=tskey-auth-... ADMIN_EMAIL=you@example.com bash create-lxc.sh
#
# Inside the container: Ubuntu 24.04, .NET 10, Tailscale, the server as a systemd service on
# 127.0.0.1:8080 with its SQLite database in /var/lib/identity, and "tailscale serve" in front of it
# (https://<hostname>.<tailnet>.ts.net).
# All settings below can be overridden with environment variables.
set -euo pipefail

CTID="${CTID:-}"                              # empty: next free ID
CT_HOSTNAME="${CT_HOSTNAME:-identity}"        # also the Tailscale machine name
STORAGE="${STORAGE:-local-lvm}"               # storage for the root disk
TEMPLATE_STORAGE="${TEMPLATE_STORAGE:-local}" # storage holding container templates
BRIDGE="${BRIDGE:-vmbr0}"
IP="${IP:-dhcp}"                              # or e.g. 192.168.1.50/24
GATEWAY="${GATEWAY:-}"                        # needed with a static IP
CORES="${CORES:-2}"
MEMORY="${MEMORY:-2048}"                      # MiB; the build needs about 1.5 GiB
DISK="${DISK:-10}"                            # GiB
SSH_PUBLIC_KEY_FILE="${SSH_PUBLIC_KEY_FILE:-}" # optional, for root login via SSH

TS_AUTHKEY="${TS_AUTHKEY:-}"                  # auth key (tskey-auth-...); empty: log in through the link Tailscale prints
TS_FUNNEL="${TS_FUNNEL:-0}"                   # 1: also reachable from the internet (Tailscale Funnel)

ADMIN_EMAIL="${ADMIN_EMAIL:-admin@example.com}"
REPO_URL="${REPO_URL:-https://github.com/Marcel-B/Identity.git}"
REPO_BRANCH="${REPO_BRANCH:-main}"
GIT_TOKEN="${GIT_TOKEN:-}"                    # only for a private repository
AUTO_UPDATE="${AUTO_UPDATE:-1}"               # 1: deploy new commits on REPO_BRANCH automatically
UPDATE_INTERVAL="${UPDATE_INTERVAL:-15min}"   # how often to check (systemd time span)

log() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
die() { printf '\033[1;31mError: %s\033[0m\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "Run as root on the Proxmox host."
command -v pct >/dev/null || die "pct not found. This script must run on a Proxmox VE host."

case $TS_AUTHKEY in
    "" | tskey-auth-*) ;;
    tskey-api-*) die "TS_AUTHKEY is an API access token. Create an auth key instead: Tailscale admin console > Settings > Keys > Generate auth key (tskey-auth-...)." ;;
    *) die "TS_AUTHKEY must be an auth key starting with tskey-auth-." ;;
esac

CTID="${CTID:-$(pvesh get /cluster/nextid)}"
RESUME=0
if pct status "$CTID" &>/dev/null; then
    # Running the script again with the same CTID finishes a setup that stopped half way.
    pct config "$CTID" | grep -qx "hostname: $CT_HOSTNAME" ||
        die "Container $CTID already exists and is not named $CT_HOSTNAME."
    RESUME=1
fi

if [[ $RESUME == 1 ]]; then
    log "Continuing the setup of container $CTID ($CT_HOSTNAME)"
else
    log "Fetching the Ubuntu 24.04 template"
    pveam update >/dev/null
    TEMPLATE="$(pveam available --section system | awk '/ubuntu-24.04-standard/ {print $2}' | sort -V | tail -n1)"
    [[ -n $TEMPLATE ]] || die "No ubuntu-24.04-standard template available."
    if ! pveam list "$TEMPLATE_STORAGE" | grep -q "$TEMPLATE"; then
        pveam download "$TEMPLATE_STORAGE" "$TEMPLATE"
    fi

    log "Creating container $CTID ($CT_HOSTNAME)"
    NET="name=eth0,bridge=$BRIDGE,ip=$IP"
    [[ -n $GATEWAY ]] && NET+=",gw=$GATEWAY"
    CREATE_ARGS=(
        --hostname "$CT_HOSTNAME"
        --cores "$CORES" --memory "$MEMORY" --swap 512
        --rootfs "$STORAGE:$DISK"
        --net0 "$NET"
        --ostype ubuntu --unprivileged 1 --features nesting=1
        --onboot 1 --timezone host
    )
    [[ -n $SSH_PUBLIC_KEY_FILE ]] && CREATE_ARGS+=(--ssh-public-keys "$SSH_PUBLIC_KEY_FILE")
    pct create "$CTID" "$TEMPLATE_STORAGE:vztmpl/$TEMPLATE" "${CREATE_ARGS[@]}"

    # Tailscale needs the TUN device, which unprivileged containers do not get by default.
    cat >>"/etc/pve/lxc/$CTID.conf" <<'EOF'
lxc.cgroup2.devices.allow: c 10:200 rwm
lxc.mount.entry: /dev/net/tun dev/net/tun none bind,create=file
EOF
fi

pct status "$CTID" | grep -q running || pct start "$CTID"

log "Waiting for the network"
for _ in $(seq 1 60); do
    pct exec "$CTID" -- getent hosts archive.ubuntu.com &>/dev/null && break
    sleep 2
done
pct exec "$CTID" -- getent hosts archive.ubuntu.com &>/dev/null || die "Container $CTID has no network."

PROVISION="$(mktemp)"
trap 'rm -f "$PROVISION"' EXIT
cat >"$PROVISION" <<'PROVISION_EOF'
#!/usr/bin/env bash
# Runs inside the container. Safe to run again: existing keys, settings and the database are kept.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

log() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
secret() { openssl rand -base64 32 | tr -d '/+=' | cut -c1-32; }

APP_DIR=/opt/identity/app
CONF_DIR=/etc/identity

log "Installing packages"
apt-get update -q
apt-get install -y -q software-properties-common
add-apt-repository -y universe >/dev/null
apt-get update -q
apt-get install -y -q ca-certificates curl git jq openssl sqlite3 dotnet-sdk-10.0

log "Installing Tailscale"
if ! command -v tailscale >/dev/null; then
    curl -fsSL https://tailscale.com/install.sh | sh
fi
systemctl enable --now tailscaled
if ! tailscale status &>/dev/null; then
    if [[ -z $TS_AUTHKEY ]] || ! tailscale up --authkey "$TS_AUTHKEY" --hostname "$CT_HOSTNAME"; then
        [[ -n $TS_AUTHKEY ]] && echo "Tailscale rejected the auth key (expired, used up or revoked?). Falling back to the login link."
        echo "Open the link below to add this container to your tailnet:"
        tailscale up --hostname "$CT_HOSTNAME"
    fi
fi
TS_NAME="$(tailscale status --json | jq -r '.Self.DNSName' | sed 's/\.$//')"
[[ -n $TS_NAME && $TS_NAME != null ]] || { echo "Could not read the Tailscale name." >&2; exit 1; }
ISSUER="https://$TS_NAME/"

log "Creating the service user"
id identity &>/dev/null || useradd --system --home /var/lib/identity --shell /usr/sbin/nologin identity
install -d -m 750 -o root -g identity "$CONF_DIR" "$CONF_DIR/keys"

log "Creating token certificates"
if [[ ! -f $CONF_DIR/keys/password ]]; then
    secret >"$CONF_DIR/keys/password"
fi
KEY_PASSWORD="$(cat "$CONF_DIR/keys/password")"
for kind in signing encryption; do
    [[ -f $CONF_DIR/keys/$kind.pfx ]] && continue
    usage=$([[ $kind == signing ]] && echo digitalSignature || echo keyEncipherment)
    openssl req -x509 -newkey rsa:3072 -nodes -days 1095 -subj "/CN=Identity $kind" \
        -addext "keyUsage=critical,$usage" \
        -keyout "$CONF_DIR/keys/$kind.key" -out "$CONF_DIR/keys/$kind.crt" 2>/dev/null
    openssl pkcs12 -export -in "$CONF_DIR/keys/$kind.crt" -inkey "$CONF_DIR/keys/$kind.key" \
        -out "$CONF_DIR/keys/$kind.pfx" -passout "pass:$KEY_PASSWORD"
    rm "$CONF_DIR/keys/$kind.key"
done
chown -R root:identity "$CONF_DIR/keys"
chmod 640 "$CONF_DIR"/keys/*

log "Writing configuration"
ADMIN_PASSWORD=""
if [[ ! -f $CONF_DIR/identity.env ]]; then
    ADMIN_PASSWORD="$(secret)"
    cat >"$CONF_DIR/identity.env" <<EOF
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:8080
Database__Provider=Sqlite
ConnectionStrings__Identity=Data Source=/var/lib/identity/identity.db

# Public address. Kestrel only listens on loopback; HTTPS comes from "tailscale serve".
Server__Issuer=$ISSUER
Server__RequireHttps=false
Server__UseForwardedHeaders=true

Keys__SigningCertificates__0__Path=$CONF_DIR/keys/signing.pfx
Keys__SigningCertificates__0__Password=$KEY_PASSWORD
Keys__EncryptionCertificates__0__Path=$CONF_DIR/keys/encryption.pfx
Keys__EncryptionCertificates__0__Password=$KEY_PASSWORD

# First administrator, only created while nobody has the admin role.
Seed__Admin__Email=$ADMIN_EMAIL
Seed__Admin__Password=$ADMIN_PASSWORD

# Mail for confirmations and password resets. Without a host, mails only go to the log
# (journalctl -u identity), and new accounts cannot confirm their address.
#Email__Host=smtp.example.com
#Email__Port=587
#Email__UserName=
#Email__Password=
#Email__From=identity@example.com

# Sign-in with external accounts (redirect URI: ${ISSUER}callback/login/github etc.)
#ExternalProviders__GitHub__ClientId=
#ExternalProviders__GitHub__ClientSecret=
EOF
    chmod 640 "$CONF_DIR/identity.env"
    chown root:identity "$CONF_DIR/identity.env"
fi

log "Installing the update command"
cat >/usr/local/bin/identity-update <<'EOF'
#!/usr/bin/env bash
# Pulls the latest code, builds it and restarts the server. If the new version does not come up
# healthy, the previous one is restored.
#   identity-update               deploy the current state of the branch
#   identity-update --if-changed  only when the branch has a commit that is not deployed yet (used by the timer)
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
token="${GIT_TOKEN:-}"
source /etc/identity/source.env
GIT_TOKEN="${token:-${GIT_TOKEN:-}}"
SRC_DIR=/opt/identity/src APP_DIR=/opt/identity/app
DEPLOYED=/opt/identity/deployed-commit FAILED=/opt/identity/failed-commit

exec 9>/run/identity-update.lock
flock -n 9 || { echo "Another update is running."; exit 0; }

url="$REPO_URL"
if [[ -n ${GIT_TOKEN:-} ]]; then
    url="${REPO_URL/https:\/\//https://x-access-token:$GIT_TOKEN@}"
fi

if [[ ${1:-} == --if-changed ]]; then
    latest="$(git ls-remote "$url" "refs/heads/$REPO_BRANCH" | cut -f1)"
    [[ -n $latest ]] || { echo "Branch $REPO_BRANCH not found." >&2; exit 1; }
    [[ $latest == "$(cat "$DEPLOYED" 2>/dev/null)" ]] && exit 0
    # A commit that failed once is not built again on every run; a newer commit or a manual run retries.
    [[ $latest == "$(cat "$FAILED" 2>/dev/null)" ]] && exit 0
fi

if [[ -d $SRC_DIR/.git ]]; then
    git -C "$SRC_DIR" fetch --depth 1 "$url" "$REPO_BRANCH"
    git -C "$SRC_DIR" reset --hard FETCH_HEAD
else
    git clone --depth 1 --branch "$REPO_BRANCH" "$url" "$SRC_DIR"
    git -C "$SRC_DIR" remote set-url origin "$REPO_URL"
fi
commit="$(git -C "$SRC_DIR" rev-parse HEAD)"
echo "Building ${commit:0:7}"

rm -rf "$APP_DIR.new"
if ! dotnet publish "$SRC_DIR/src/Identity.Server/Identity.Server.csproj" -c Release -o "$APP_DIR.new"; then
    echo "$commit" >"$FAILED"
    echo "Build of ${commit:0:7} failed, the running version stays." >&2
    exit 1
fi
rm -rf "$APP_DIR.old"
if [[ -d $APP_DIR ]]; then mv "$APP_DIR" "$APP_DIR.old"; fi
mv "$APP_DIR.new" "$APP_DIR"

healthy() {
    for _ in $(seq 1 30); do
        curl -fsS -o /dev/null http://127.0.0.1:8080/health && return 0
        sleep 2
    done
    return 1
}

if systemctl is-enabled identity &>/dev/null; then
    systemctl restart identity
    if ! healthy; then
        echo "${commit:0:7} did not become healthy, restoring the previous version." >&2
        echo "$commit" >"$FAILED"
        if [[ -d $APP_DIR.old ]]; then
            mv "$APP_DIR" "$APP_DIR.new"
            mv "$APP_DIR.old" "$APP_DIR"
            systemctl restart identity
        fi
        exit 1
    fi
fi
echo "$commit" >"$DEPLOYED"
rm -f "$FAILED"
echo "Deployed ${commit:0:7}"
EOF
chmod 755 /usr/local/bin/identity-update
# Root only: also holds the token of a private repository, so the timer can fetch.
if [[ -z $GIT_TOKEN && -f $CONF_DIR/source.env ]]; then
    GIT_TOKEN="$(sed -n 's/^GIT_TOKEN=//p' "$CONF_DIR/source.env")"
fi
install -m 600 /dev/null "$CONF_DIR/source.env"
cat >"$CONF_DIR/source.env" <<EOF
REPO_URL=$REPO_URL
REPO_BRANCH=$REPO_BRANCH
GIT_TOKEN=$GIT_TOKEN
EOF

log "Building the server"
/usr/local/bin/identity-update

log "Creating the systemd service"
cat >/etc/systemd/system/identity.service <<EOF
[Unit]
Description=Identity server (OpenID Connect)
After=network-online.target
Wants=network-online.target

[Service]
User=identity
Group=identity
WorkingDirectory=$APP_DIR
EnvironmentFile=$CONF_DIR/identity.env
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart=/usr/bin/dotnet $APP_DIR/Identity.Server.dll
Restart=always
RestartSec=5
# /var/lib/identity, owned by the service user; holds the SQLite database.
StateDirectory=identity
StateDirectoryMode=0750
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now identity

for _ in $(seq 1 60); do
    curl -fs http://127.0.0.1:8080/health >/dev/null && break
    sleep 2
done
curl -fs http://127.0.0.1:8080/health >/dev/null || { journalctl -u identity -n 50 --no-pager; exit 1; }

log "Setting up automatic updates"
if [[ $AUTO_UPDATE == 1 ]]; then
    cat >/etc/systemd/system/identity-update.service <<'EOF'
[Unit]
Description=Deploy a new version of the identity server from git
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=/usr/local/bin/identity-update --if-changed
EOF
    cat >/etc/systemd/system/identity-update.timer <<EOF
[Unit]
Description=Check for a new version of the identity server

[Timer]
OnBootSec=5min
OnUnitActiveSec=$UPDATE_INTERVAL
RandomizedDelaySec=1min

[Install]
WantedBy=timers.target
EOF
    systemctl daemon-reload
    systemctl enable --now identity-update.timer
else
    systemctl disable --now identity-update.timer 2>/dev/null || true
fi

log "Publishing over Tailscale"
if [[ $TS_FUNNEL == 1 ]]; then
    tailscale funnel --bg http://127.0.0.1:8080
else
    tailscale serve --bg http://127.0.0.1:8080
fi

log "Done"
echo "Issuer:    $ISSUER"
echo "Discovery: ${ISSUER}.well-known/openid-configuration"
if [[ -n $ADMIN_PASSWORD ]]; then
    echo "Admin:     $ADMIN_EMAIL"
    echo "Password:  $ADMIN_PASSWORD   (shown only now, please change it after the first sign-in)"
fi
echo "Settings:  $CONF_DIR/identity.env, then: systemctl restart identity"
if [[ $AUTO_UPDATE == 1 ]]; then
    echo "Updates:   new commits on $REPO_BRANCH are deployed every $UPDATE_INTERVAL (journalctl -u identity-update)"
fi
echo "Update now: identity-update"
echo "Backup:    sqlite3 /var/lib/identity/identity.db \".backup /root/identity-backup.db\""
PROVISION_EOF

pct push "$CTID" "$PROVISION" /root/provision-identity.sh --perms 700

log "Provisioning the container"
pct exec "$CTID" -- env \
    CT_HOSTNAME="$CT_HOSTNAME" TS_AUTHKEY="$TS_AUTHKEY" TS_FUNNEL="$TS_FUNNEL" \
    ADMIN_EMAIL="$ADMIN_EMAIL" REPO_URL="$REPO_URL" REPO_BRANCH="$REPO_BRANCH" GIT_TOKEN="$GIT_TOKEN" \
    AUTO_UPDATE="$AUTO_UPDATE" UPDATE_INTERVAL="$UPDATE_INTERVAL" \
    bash /root/provision-identity.sh ||
    die "Setup of container $CTID stopped. Fix the cause and run the script again with CTID=$CTID to continue."

log "Container $CTID is ready"
