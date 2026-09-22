#!/usr/bin/env bash
set -Eeuo pipefail
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
umask 077

DOMAIN="${SUPPORT_DOMAIN:-}"
APP_ROOT=/opt/remote-assist
BIN_DIR="$APP_ROOT/bin"
DOWNLOAD_DIR="$APP_ROOT/downloads"
ENV_DIR=/etc/remote-assist-app
ENV_FILE="$ENV_DIR/app.env"
SERVICE=/etc/systemd/system/remote-assist.service
VHOST=/etc/apache2/sites-available/remote-assist.conf
ACME=/var/www/remote-assist/acme
REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
NEW_CREDS=0

fail(){ echo "ERROR: $*" >&2; exit 1; }
[[ -n "$DOMAIN" ]] || fail "Set SUPPORT_DOMAIN to the public DNS name for this Remote Assist server."
[[ $EUID -eq 0 ]] || fail "Run as root."
. /etc/os-release
[[ ${ID:-} == ubuntu ]] || fail "Ubuntu is required."
case "${VERSION_ID:-}" in 24.04|26.04) ;; *) fail "Supported Ubuntu releases: 24.04 and 26.04.";; esac
for cmd in apache2ctl systemctl mysql openssl curl; do command -v "$cmd" >/dev/null || fail "Missing required command: $cmd"; done
if ! command -v git >/dev/null || ! command -v go >/dev/null; then
  echo "Installing Git/Go build prerequisites from Ubuntu repositories..."
  apt-get update
  apt-get install -y --no-install-recommends git golang-go ca-certificates
fi
GO_VERSION_RAW="$(go env GOVERSION 2>/dev/null || true)"
python3 - "$GO_VERSION_RAW" <<'PY_GO_VERSION'
import re, sys
m = re.fullmatch(r'go(\d+)\.(\d+)(?:\.\d+)?', sys.argv[1])
if not m or (int(m.group(1)), int(m.group(2))) < (1, 23):
    raise SystemExit(f"Go 1.23+ is required; found {sys.argv[1] or 'unknown'}")
PY_GO_VERSION
systemctl is-active --quiet apache2 || fail "Apache must already be running."
systemctl is-active --quiet mysql || systemctl is-active --quiet mariadb || fail "MySQL/MariaDB must already be running."

# This deployment uses HTTPS/WSS only. It deliberately does not change UFW or the existing RustDesk services.
echo "Repository: $REPO_ROOT"
echo "Domain:     $DOMAIN"
echo "Building server..."
mkdir -p "$BIN_DIR" "$DOWNLOAD_DIR" "$ENV_DIR" "$ACME/.well-known/acme-challenge"
chmod 0755 "$APP_ROOT" "$BIN_DIR" "$DOWNLOAD_DIR" "$ACME" "$ACME/.well-known" "$ACME/.well-known/acme-challenge"
cd "$REPO_ROOT"
go mod download
go test ./...
go vet ./...
go build -trimpath -ldflags='-s -w' -o "$BIN_DIR/remote-assist-server.new" ./cmd/server
chmod 0755 "$BIN_DIR/remote-assist-server.new"

RELEASE_REPO="${REMOTE_ASSIST_RELEASE_REPO:-}"
if [[ -z "$RELEASE_REPO" ]]; then
  ORIGIN_URL="$(git -C "$REPO_ROOT" config --get remote.origin.url 2>/dev/null || true)"
  RELEASE_REPO="$(printf '%s' "$ORIGIN_URL" | sed -E 's#^https://github.com/##; s#^git@github.com:##; s#\.git$##')"
fi
if [[ ! -s "$DOWNLOAD_DIR/RemoteAssist.exe" ]]; then
  echo "Downloading the current CI-built Windows support agent..."
  AGENT_URL="${AGENT_URL:-${RELEASE_REPO:+https://github.com/$RELEASE_REPO/releases/download/preview-latest/Remote-Assist.exe}}"
  TMP_AGENT="$(mktemp "$DOWNLOAD_DIR/.RemoteAssist.exe.XXXXXX")"
  if curl --fail --location --retry 3 --connect-timeout 15 --max-time 300 "$AGENT_URL" -o "$TMP_AGENT"; then
    python3 - "$TMP_AGENT" <<'PY_AGENT'
import pathlib, sys
p = pathlib.Path(sys.argv[1])
data = p.read_bytes()
if len(data) < 100_000 or not data.startswith(b'MZ'):
    raise SystemExit('Downloaded Windows agent is not a plausible PE executable')
PY_AGENT
    chmod 0644 "$TMP_AGENT"
    mv -f "$TMP_AGENT" "$DOWNLOAD_DIR/RemoteAssist.exe"
  else
    rm -f "$TMP_AGENT"
    echo "WARNING: Windows agent preview release is not available yet. Server installation will continue." >&2
    echo "         After CI publishes it, rerun deploy/update-vps.sh or deploy/publish-agent.sh." >&2
  fi
fi

download_optional_release_asset() {
  local label="$1"
  local url="$2"
  local destination="$3"
  local kind="$4"

  if [[ -s "$destination" ]]; then
    return 0
  fi

  local tmp
  tmp="$(mktemp "$DOWNLOAD_DIR/.download.XXXXXX")"

  if ! curl --fail --location --retry 3 --connect-timeout 15 --max-time 300 "$url" -o "$tmp"; then
    rm -f "$tmp"
    echo "WARNING: $label release asset is not available yet." >&2
    return 0
  fi

  python3 - "$tmp" "$kind" "$label" <<'PY_EXTRA_ASSET'
import pathlib
import sys
import zipfile

path = pathlib.Path(sys.argv[1])
kind = sys.argv[2]
label = sys.argv[3]
data = path.read_bytes()

if kind == "pe":
    if len(data) < 100_000 or not data.startswith(b"MZ"):
        raise SystemExit(f"{label} is not a plausible PE executable")
elif kind == "zip":
    if len(data) < 10_000 or not data.startswith(b"PK") or not zipfile.is_zipfile(path):
        raise SystemExit(f"{label} is not a plausible ZIP archive")
else:
    raise SystemExit(f"unknown validation kind: {kind}")
PY_EXTRA_ASSET

  chmod 0644 "$tmp"
  mv -f "$tmp" "$destination"
}

TECH_PORTABLE_URL="${TECH_PORTABLE_URL:-${RELEASE_REPO:+https://github.com/$RELEASE_REPO/releases/download/preview-latest/Remote-Assist-Technician-Portable.zip}}"
TECH_INSTALLER_URL="${TECH_INSTALLER_URL:-${RELEASE_REPO:+https://github.com/$RELEASE_REPO/releases/download/preview-latest/Remote-Assist-Technician-Setup.exe}}"

download_optional_release_asset   "Technician portable"   "$TECH_PORTABLE_URL"   "$DOWNLOAD_DIR/Remote-Assist-Technician-Portable.zip"   zip

download_optional_release_asset   "Technician installer"   "$TECH_INSTALLER_URL"   "$DOWNLOAD_DIR/Remote-Assist-Technician-Setup.exe"   pe

if ! getent passwd remote-assist >/dev/null; then
  useradd --system --user-group --home-dir "$APP_ROOT" --no-create-home --shell /usr/sbin/nologin remote-assist
fi

if [[ ! -f "$ENV_FILE" ]]; then
  NEW_CREDS=1
  DBPASS="$(openssl rand -hex 24)"
  TECHPASS="$(openssl rand -base64 24 | tr -d '\n')"
  COOKIE_SECRET="$(openssl rand -hex 32)"
  CODE_SECRET="$(openssl rand -hex 32)"
  mysql --protocol=socket <<SQL
CREATE DATABASE IF NOT EXISTS remote_assist CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS 'remote_assist'@'127.0.0.1' IDENTIFIED BY '$DBPASS';
ALTER USER 'remote_assist'@'127.0.0.1' IDENTIFIED BY '$DBPASS';
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, REFERENCES ON remote_assist.* TO 'remote_assist'@'127.0.0.1';
FLUSH PRIVILEGES;
SQL
  cat > "$ENV_FILE" <<ENV
LISTEN_ADDR=127.0.0.1:8787
PUBLIC_BASE_URL=https://$DOMAIN
MYSQL_DSN=remote_assist:$DBPASS@tcp(127.0.0.1:3306)/remote_assist?parseTime=true&loc=UTC&charset=utf8mb4
TECH_USERNAME=admin
TECH_PASSWORD=$TECHPASS
COOKIE_SECRET=$COOKIE_SECRET
CODE_SECRET=$CODE_SECRET
SESSION_TTL_MINUTES=15
LIVE_SESSION_TTL_MINUTES=480
AGENT_DOWNLOAD_PATH=$DOWNLOAD_DIR/RemoteAssist.exe
TECHNICIAN_PORTABLE_PATH=$DOWNLOAD_DIR/Remote-Assist-Technician-Portable.zip
TECHNICIAN_INSTALLER_PATH=$DOWNLOAD_DIR/Remote-Assist-Technician-Setup.exe
TRUST_PROXY=true
ENV
  chmod 0600 "$ENV_FILE"
else
  echo "Preserving existing $ENV_FILE and server identity/secrets."
fi

mv -f "$BIN_DIR/remote-assist-server.new" "$BIN_DIR/remote-assist-server"
chown -R root:root "$APP_ROOT"

cat > "$SERVICE" <<UNIT
[Unit]
Description=Remote Assist attended remote support server
After=network-online.target mysql.service mariadb.service
Wants=network-online.target

[Service]
Type=simple
User=remote-assist
Group=remote-assist
EnvironmentFile=$ENV_FILE
ExecStart=$BIN_DIR/remote-assist-server
Restart=on-failure
RestartSec=3
NoNewPrivileges=true
PrivateTmp=true
PrivateDevices=true
ProtectSystem=strict
ProtectHome=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
LockPersonality=true
RestrictNamespaces=true
CapabilityBoundingSet=
AmbientCapabilities=
ReadOnlyPaths=$APP_ROOT

[Install]
WantedBy=multi-user.target
UNIT
chmod 0644 "$SERVICE"
systemctl daemon-reload
systemctl enable --now remote-assist.service

for i in {1..30}; do
  if curl -fsS http://127.0.0.1:8787/api/health >/dev/null; then break; fi
  sleep 1
done
curl -fsS http://127.0.0.1:8787/api/health >/dev/null || { journalctl -u remote-assist.service -n 80 --no-pager; fail "Support service failed health check."; }

CERT_ROOT=""
for name in "remote-assist-$DOMAIN" "$DOMAIN"; do
  if [[ -s "/etc/letsencrypt/live/$name/fullchain.pem" && -s "/etc/letsencrypt/live/$name/privkey.pem" ]]; then CERT_ROOT="/etc/letsencrypt/live/$name"; break; fi
done
[[ -n "$CERT_ROOT" ]] || fail "No existing Let's Encrypt certificate found for $DOMAIN. Obtain one first, then rerun."

a2enmod proxy proxy_http proxy_wstunnel headers rewrite ssl >/dev/null
if [[ -f "$VHOST" ]]; then
  if ! grep -Eq 'remote-assist-root-vultr-v1|Remote Assist attended remote support' "$VHOST"; then
    fail "$VHOST exists but is not recognized as an Remote Assist-managed vhost. Refusing to overwrite it."
  fi
  cp -a "$VHOST" "/root/remote-assist-vhost-$(date +%Y%m%d-%H%M%S).conf"
fi

cat > "$VHOST" <<APACHE
# Remote Assist attended remote support - managed by deploy/install-vps.sh
<VirtualHost *:80>
    ServerName $DOMAIN
    DocumentRoot "$ACME"
    <Directory "$ACME">
        Options -Indexes -ExecCGI
        AllowOverride None
        Require all granted
    </Directory>
    RewriteEngine On
    RewriteCond %{REQUEST_URI} !^/\.well-known/acme-challenge/
    RewriteRule ^ https://$DOMAIN%{REQUEST_URI} [R=301,L,NE]
</VirtualHost>

<VirtualHost *:443>
    ServerName $DOMAIN
    SSLEngine On
    SSLCertificateFile $CERT_ROOT/fullchain.pem
    SSLCertificateKeyFile $CERT_ROOT/privkey.pem
    SSLProtocol -all +TLSv1.2 +TLSv1.3

    ProxyRequests Off
    ProxyPreserveHost On
    ProxyTimeout 120
    RequestHeader set X-Forwarded-Proto "https"

    ProxyPass        /ws/ ws://127.0.0.1:8787/ws/ retry=0 timeout=120
    ProxyPassReverse /ws/ ws://127.0.0.1:8787/ws/
    ProxyPass        / http://127.0.0.1:8787/ retry=0 timeout=120
    ProxyPassReverse / http://127.0.0.1:8787/

    ErrorLog \${APACHE_LOG_DIR}/remote-assist-error.log
    CustomLog \${APACHE_LOG_DIR}/remote-assist-access.log combined
</VirtualHost>
APACHE
chmod 0644 "$VHOST"
a2ensite remote-assist.conf >/dev/null
apache2ctl configtest
systemctl reload apache2

curl -fsS "https://$DOMAIN/api/health" >/dev/null || fail "Public HTTPS health check failed after Apache reload."

echo
echo "Remote Assist server installed successfully."
echo "Console: https://$DOMAIN"
echo "Service: remote-assist.service"
echo "Config:  $ENV_FILE"
echo "Agent:   $DOWNLOAD_DIR/RemoteAssist.exe"
echo "RustDesk services and firewall rules were not changed."
if (( NEW_CREDS )); then
  echo
  echo "SAVE THESE TECHNICIAN CREDENTIALS NOW:"
  echo "  Username: admin"
  echo "  Password: $TECHPASS"
  echo "They are also stored root-only in $ENV_FILE. Change TECH_PASSWORD there and restart the service when ready."
fi
