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

# Nightly backup to a Nextcloud folder over WebDAV; off while the three values are missing.
NEXTCLOUD_WEBDAV_URL="${NEXTCLOUD_WEBDAV_URL:-}" # e.g. https://cloud.example.de/remote.php/dav/files/<user>/Backups/identity
NEXTCLOUD_USERNAME="${NEXTCLOUD_USERNAME:-}"
NEXTCLOUD_APP_PASSWORD="${NEXTCLOUD_APP_PASSWORD:-}" # app password (Nextcloud: Settings > Security > Devices & sessions)
BACKUP_PASSPHRASE="${BACKUP_PASSPHRASE:-}"       # optional: encrypt the archives with this passphrase
BACKUP_AT="${BACKUP_AT:-03:00}"                  # start time of the nightly backup (local time of the container)

log() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
die() { printf '\033[1;31mError: %s\033[0m\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "Run as root on the Proxmox host."
command -v pct >/dev/null || die "pct not found. This script must run on a Proxmox VE host."

if [[ -n $NEXTCLOUD_WEBDAV_URL ]]; then
    [[ $NEXTCLOUD_WEBDAV_URL =~ ^https?://[^/]+/ && $NEXTCLOUD_WEBDAV_URL != *://*://* ]] ||
        die "NEXTCLOUD_WEBDAV_URL must be a WebDAV folder like https://cloud.example.de/remote.php/dav/files/<user>/Backups/identity."
fi

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
apt-get install -y -q ca-certificates curl git jq openssl sqlite3 zip unzip dotnet-sdk-10.0

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

log "Setting up the Nextcloud backup"
systemd-analyze calendar "*-*-* $BACKUP_AT" >/dev/null || { echo "BACKUP_AT=$BACKUP_AT is not a time like 03:00." >&2; exit 1; }
cat >/usr/local/bin/identity-backup <<'EOF'
#!/usr/bin/env bash
# Backs up the identity server to a Nextcloud folder over WebDAV, like fil-o-mat: a copy of the SQLite
# database through SQLite's online backup (consistent while the server writes) plus the keys and settings
# from /etc/identity, packed as a ZIP. Each run uploads two archives: identity-mon.zip ... identity-sun.zip
# (the last seven days) and identity-2026-10.zip (the last state of each month). Nothing is deleted there.
# Access in /etc/identity/backup.env: NEXTCLOUD_WEBDAV_URL, NEXTCLOUD_USERNAME, NEXTCLOUD_APP_PASSWORD,
# optionally BACKUP_PASSPHRASE (archives are then encrypted with AES-256 and named *.zip.enc).
set -euo pipefail
CONF_DIR=/etc/identity

# Read as plain KEY=value lines, so passwords with $ or quotes need no escaping.
setting() { if [[ -f $2 ]]; then sed -n "s/^$1=//p" "$2" | tail -n1; fi; }
url="${NEXTCLOUD_WEBDAV_URL:-$(setting NEXTCLOUD_WEBDAV_URL "$CONF_DIR/backup.env")}"
user="${NEXTCLOUD_USERNAME:-$(setting NEXTCLOUD_USERNAME "$CONF_DIR/backup.env")}"
password="${NEXTCLOUD_APP_PASSWORD:-$(setting NEXTCLOUD_APP_PASSWORD "$CONF_DIR/backup.env")}"
passphrase="${BACKUP_PASSPHRASE:-$(setting BACKUP_PASSPHRASE "$CONF_DIR/backup.env")}"
if [[ -z $url || -z $user || -z $password ]]; then
    echo "Nextcloud backup is off: NEXTCLOUD_WEBDAV_URL, NEXTCLOUD_USERNAME and NEXTCLOUD_APP_PASSWORD are missing in $CONF_DIR/backup.env."
    exit 0
fi
url="${url%/}"
[[ $url =~ ^https?://[^/]+/ && $url != *://*://* ]] ||
    { echo "NEXTCLOUD_WEBDAV_URL=$url is no WebDAV folder like https://cloud.example.de/remote.php/dav/files/<user>/Backups/identity." >&2; exit 1; }

provider="$(setting Database__Provider "$CONF_DIR/identity.env")"
[[ ${provider:-Sqlite} == Sqlite ]] || { echo "Only SQLite is backed up, the server uses $provider." >&2; exit 1; }
db="$(setting ConnectionStrings__Identity "$CONF_DIR/identity.env" | sed -n 's/.*Data Source=\([^;]*\).*/\1/p')"
db="${db:-/var/lib/identity/identity.db}"
[[ -f $db ]] || { echo "Database $db not found." >&2; exit 1; }

exec 9>/run/identity-backup.lock
flock -n 9 || { echo "Another backup is running."; exit 0; }

scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
chown identity:identity "$scratch"
# As the service user, so SQLite never leaves root-owned -wal or -shm files next to the database.
runuser -u identity -- sqlite3 "$db" ".timeout 30000" ".backup '$scratch/identity.db'"
check="$(sqlite3 "$scratch/identity.db" "PRAGMA integrity_check")"
[[ $check == ok ]] || { echo "The copy of the database is damaged: $check" >&2; exit 1; }

# The keys belong to the database: the encryption certificate protects the cookie keys stored in it.
# source.env (git token) and backup.env (this access) stay out.
mkdir -p "$scratch/etc/identity"
cp -a "$CONF_DIR/identity.env" "$CONF_DIR/keys" "$scratch/etc/identity/"
(cd "$scratch" && zip -q -r -9 identity.zip identity.db etc)
archive="$scratch/identity.zip" extension=zip
if [[ -n $passphrase ]]; then
    BACKUP_PASSPHRASE="$passphrase" openssl enc -aes-256-cbc -pbkdf2 -iter 600000 -salt \
        -in "$archive" -out "$archive.enc" -pass env:BACKUP_PASSPHRASE
    archive="$archive.enc" extension=zip.enc
fi

# Credentials go to curl on stdin, not on the command line where ps would show them.
webdav() {
    local method=$1 target=$2
    shift 2
    printf 'user = "%s"\n' "$(printf '%s:%s' "$user" "$password" | sed 's/[\\"]/\\&/g')" |
        curl -sS -K - -o /dev/null -w '%{http_code}' --connect-timeout 30 --max-time 300 -X "$method" "$@" "$target"
}

status="$(webdav MKCOL "$url/")" || { echo "Nextcloud does not respond ($url)." >&2; exit 1; }
case $status in
    201 | 405) ;; # 405: the folder exists
    401) echo "Nextcloud rejects the user or the app password (401)." >&2; exit 1 ;;
    409) echo "The folder above the backup folder is missing in Nextcloud (409): ${url%/*}" >&2; exit 1 ;;
    *) echo "Cannot create the backup folder $url (HTTP $status)." >&2; exit 1 ;;
esac

day="$(LC_ALL=C date +%a)"
bytes="$(stat -c %s "$archive")"
for name in "identity-${day,,}.$extension" "identity-$(date +%Y-%m).$extension"; do
    status="$(webdav PUT "$url/$name" -H 'Content-Type: application/octet-stream' --data-binary "@$archive")" ||
        { echo "Nextcloud does not respond ($url)." >&2; exit 1; }
    case $status in
        201 | 204) echo "Uploaded $name ($bytes bytes)" ;;
        401) echo "Nextcloud rejects the user or the app password (401)." >&2; exit 1 ;;
        423) echo "Nextcloud keeps $name locked (423)." >&2; exit 1 ;;
        507) echo "Nextcloud is full (507)." >&2; exit 1 ;;
        *) echo "Cannot upload $name (HTTP $status)." >&2; exit 1 ;;
    esac
done
EOF
chmod 755 /usr/local/bin/identity-backup

cat >/usr/local/bin/identity-restore <<'EOF'
#!/usr/bin/env bash
# Restores a backup made by identity-backup.
#   identity-restore --list                   list the archives in the Nextcloud folder
#   identity-restore identity-mon.zip         download that archive from Nextcloud and restore the database
#   identity-restore /root/identity-mon.zip   restore from a local file (.zip or .zip.enc)
# Options:
#   --with-config  also restore the certificates and identity.env from the archive (for a new container);
#                  Server__Issuer of this container is kept
#   --yes          do not ask before replacing the database
# The current database (and with --with-config the current settings) is kept next to it first; if the
# server does not come up healthy with the restored state, that copy is put back.
set -euo pipefail
CONF_DIR=/etc/identity

usage() { sed -n '2,12s/^# \{0,1\}//p' "$0"; exit "${1:-0}"; }
die() { echo "$*" >&2; exit 1; }

with_config=0 yes=0 list=0 source=""
for arg in "$@"; do
    case $arg in
        --with-config) with_config=1 ;;
        --yes) yes=1 ;;
        --list) list=1 ;;
        -h | --help) usage ;;
        -*) usage 1 ;;
        *) [[ -z $source ]] || usage 1; source=$arg ;;
    esac
done
[[ $list == 1 || -n $source ]] || usage 1

setting() { if [[ -f $2 ]]; then sed -n "s/^$1=//p" "$2" | tail -n1; fi; }
url="${NEXTCLOUD_WEBDAV_URL:-$(setting NEXTCLOUD_WEBDAV_URL "$CONF_DIR/backup.env")}"
user="${NEXTCLOUD_USERNAME:-$(setting NEXTCLOUD_USERNAME "$CONF_DIR/backup.env")}"
password="${NEXTCLOUD_APP_PASSWORD:-$(setting NEXTCLOUD_APP_PASSWORD "$CONF_DIR/backup.env")}"
passphrase="${BACKUP_PASSPHRASE:-$(setting BACKUP_PASSPHRASE "$CONF_DIR/backup.env")}"
url="${url%/}"

# Credentials go to curl on stdin, not on the command line where ps would show them.
need_access() {
    [[ -n $url && -n $user && -n $password ]] ||
        die "No Nextcloud access in $CONF_DIR/backup.env; pass the path of a local archive instead."
    [[ $url =~ ^https?://[^/]+/ && $url != *://*://* ]] ||
        die "NEXTCLOUD_WEBDAV_URL=$url is no WebDAV folder like https://cloud.example.de/remote.php/dav/files/<user>/Backups/identity."
}
webdav() {
    printf 'user = "%s"\n' "$(printf '%s:%s' "$user" "$password" | sed 's/[\\"]/\\&/g')" |
        curl -sS -K - --connect-timeout 30 --max-time 300 "$@"
}

if [[ $list == 1 ]]; then
    need_access
    webdav -f -X PROPFIND -H 'Depth: 1' "$url/" |
        grep -o '<d:href>[^<]*</d:href>' | sed 's#.*/\([^/<]*\)</d:href>#\1#' |
        grep -E '^identity-.*\.zip(\.enc)?$' | sort || die "No archives found in $url."
    exit 0
fi

provider="$(setting Database__Provider "$CONF_DIR/identity.env")"
[[ ${provider:-Sqlite} == Sqlite ]] || die "Only SQLite can be restored, the server uses $provider."
db="$(setting ConnectionStrings__Identity "$CONF_DIR/identity.env" | sed -n 's/.*Data Source=\([^;]*\).*/\1/p')"
db="${db:-/var/lib/identity/identity.db}"

scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
archive="$scratch/$(basename "$source")"
if [[ -f $source ]]; then
    cp "$source" "$archive"
else
    [[ $source != */* ]] || die "$source not found."
    need_access
    echo "Downloading $source"
    webdav -f -o "$archive" "$url/$source" || die "Cannot download $source from $url (identity-restore --list shows the archives)."
fi

if [[ $archive == *.enc ]]; then
    if [[ -z $passphrase ]]; then
        read -rsp "Passphrase: " passphrase
        echo
    fi
    BACKUP_PASSPHRASE="$passphrase" openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 \
        -in "$archive" -out "${archive%.enc}" -pass env:BACKUP_PASSPHRASE 2>/dev/null ||
        die "Cannot decrypt $source: wrong passphrase?"
    archive="${archive%.enc}"
fi
unzip -q "$archive" -d "$scratch/restore" || die "$source is not a readable ZIP."
restore="$scratch/restore"
[[ -f $restore/identity.db ]] || die "$source contains no identity.db."
check="$(sqlite3 "$restore/identity.db" "PRAGMA integrity_check")"
[[ $check == ok ]] || die "The database in $source is damaged: $check"
if [[ $with_config == 1 ]]; then
    [[ -f $restore/etc/identity/identity.env && -d $restore/etc/identity/keys ]] ||
        die "$source contains no certificates and settings."
fi

echo "Users in the backup: $(sqlite3 "$restore/identity.db" "SELECT count(*) FROM AspNetUsers" 2>/dev/null || echo '?')"
if [[ $yes != 1 ]]; then
    what="the database $db"
    [[ $with_config == 1 ]] && what+=" and the certificates and settings in $CONF_DIR"
    read -rp "Replace $what with $(basename "$source")? [y/N] " answer
    [[ $answer == [yYjJ]* ]] || die "Cancelled."
fi

# No backup or update may run in between: both use these locks.
exec 8>/run/identity-backup.lock 9>/run/identity-update.lock
{ flock -w 600 8 && flock -w 600 9; } || die "A backup or update is still running."

stamp="$(date +%Y%m%d-%H%M%S)"
previous="$db.before-restore-$stamp"
previous_config="/root/identity-config-before-restore-$stamp"
healthy() {
    for _ in $(seq 1 30); do
        curl -fs -o /dev/null http://127.0.0.1:8080/health && return 0
        sleep 2
    done
    return 1
}

systemctl stop identity
if [[ -f $db ]]; then
    # As the service user, so SQLite leaves no root-owned files next to the database.
    runuser -u identity -- sqlite3 "$db" ".backup '$previous'"
    chmod 640 "$previous"
    echo "Current database kept as $previous"
fi
install -o identity -g identity -m 640 "$restore/identity.db" "$db"
rm -f "$db-wal" "$db-shm"

if [[ $with_config == 1 ]]; then
    mkdir -p "$previous_config"
    cp -a "$CONF_DIR/identity.env" "$CONF_DIR/keys" "$previous_config/"
    echo "Current settings kept in $previous_config"
    issuer="$(setting Server__Issuer "$CONF_DIR/identity.env")"
    old_issuer="$(setting Server__Issuer "$restore/etc/identity/identity.env")"
    rm -rf "$CONF_DIR/keys"
    cp -a "$restore/etc/identity/keys" "$restore/etc/identity/identity.env" "$CONF_DIR/"
    if [[ -n $issuer ]]; then
        sed -i "s#^Server__Issuer=.*#Server__Issuer=$issuer#" "$CONF_DIR/identity.env"
        [[ $issuer == "$old_issuer" ]] ||
            echo "Kept Server__Issuer=$issuer (the backup had $old_issuer); clients may need the new address."
    fi
    chown -R root:identity "$CONF_DIR/keys" "$CONF_DIR/identity.env"
    chmod 640 "$CONF_DIR/identity.env" "$CONF_DIR"/keys/*
fi

systemctl start identity
if healthy; then
    echo "Restored $(basename "$source")."
    exit 0
fi

echo "The server does not come up with the restored state, putting the previous one back." >&2
journalctl -u identity -n 20 --no-pager >&2 || true
systemctl stop identity
if [[ -f $previous ]]; then
    install -o identity -g identity -m 640 "$previous" "$db"
    rm -f "$db-wal" "$db-shm"
fi
if [[ $with_config == 1 ]]; then
    rm -rf "$CONF_DIR/keys"
    cp -a "$previous_config/keys" "$previous_config/identity.env" "$CONF_DIR/"
fi
systemctl start identity
exit 1
EOF
chmod 755 /usr/local/bin/identity-restore

# Root only: holds the Nextcloud app password. Values given to this script replace the file,
# otherwise an existing one is kept.
if [[ -n $NEXTCLOUD_WEBDAV_URL || -n $NEXTCLOUD_USERNAME || -n $NEXTCLOUD_APP_PASSWORD || ! -f $CONF_DIR/backup.env ]]; then
    install -m 600 /dev/null "$CONF_DIR/backup.env"
    cat >"$CONF_DIR/backup.env" <<EOF
# Nightly backup to Nextcloud (identity-backup). The folder is created, the one above it must exist.
NEXTCLOUD_WEBDAV_URL=$NEXTCLOUD_WEBDAV_URL
NEXTCLOUD_USERNAME=$NEXTCLOUD_USERNAME
NEXTCLOUD_APP_PASSWORD=$NEXTCLOUD_APP_PASSWORD
# Optional: encrypt the archives (keep the passphrase outside this container, e.g. in a password manager).
BACKUP_PASSPHRASE=$BACKUP_PASSPHRASE
EOF
elif [[ -n $BACKUP_PASSPHRASE ]]; then
    sed -i '/^BACKUP_PASSPHRASE=/d' "$CONF_DIR/backup.env"
    echo "BACKUP_PASSPHRASE=$BACKUP_PASSPHRASE" >>"$CONF_DIR/backup.env"
fi

cat >/etc/systemd/system/identity-backup.service <<'EOF'
[Unit]
Description=Back up the identity server to Nextcloud
After=network-online.target
Wants=network-online.target
# A failed run is retried every 30 minutes, at most three times.
StartLimitIntervalSec=6h
StartLimitBurst=4

[Service]
Type=oneshot
ExecStart=/usr/local/bin/identity-backup
Restart=on-failure
RestartSec=30min
PrivateTmp=true
EOF
cat >/etc/systemd/system/identity-backup.timer <<EOF
[Unit]
Description=Nightly backup of the identity server to Nextcloud

[Timer]
OnCalendar=*-*-* $BACKUP_AT
# Catches up on a run missed while the container was off.
Persistent=true
RandomizedDelaySec=10min

[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
systemctl enable --now identity-backup.timer

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
echo "Restore:   identity-restore --list, then identity-restore <archive>"
if [[ -n $(sed -n 's/^NEXTCLOUD_APP_PASSWORD=//p' "$CONF_DIR/backup.env") ]]; then
    echo "Backup:    every night at $BACKUP_AT to Nextcloud (journalctl -u identity-backup), now: identity-backup"
else
    echo "Backup:    off, add the Nextcloud access to $CONF_DIR/backup.env, then test with: identity-backup"
fi
PROVISION_EOF

pct push "$CTID" "$PROVISION" /root/provision-identity.sh --perms 700

log "Provisioning the container"
pct exec "$CTID" -- env \
    CT_HOSTNAME="$CT_HOSTNAME" TS_AUTHKEY="$TS_AUTHKEY" TS_FUNNEL="$TS_FUNNEL" \
    ADMIN_EMAIL="$ADMIN_EMAIL" REPO_URL="$REPO_URL" REPO_BRANCH="$REPO_BRANCH" GIT_TOKEN="$GIT_TOKEN" \
    AUTO_UPDATE="$AUTO_UPDATE" UPDATE_INTERVAL="$UPDATE_INTERVAL" \
    NEXTCLOUD_WEBDAV_URL="$NEXTCLOUD_WEBDAV_URL" NEXTCLOUD_USERNAME="$NEXTCLOUD_USERNAME" \
    NEXTCLOUD_APP_PASSWORD="$NEXTCLOUD_APP_PASSWORD" BACKUP_PASSPHRASE="$BACKUP_PASSPHRASE" BACKUP_AT="$BACKUP_AT" \
    bash /root/provision-identity.sh ||
    die "Setup of container $CTID stopped. Fix the cause and run the script again with CTID=$CTID to continue."

log "Container $CTID is ready"
