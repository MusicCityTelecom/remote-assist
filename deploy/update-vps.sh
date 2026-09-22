#!/usr/bin/env bash
set -Eeuo pipefail
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
[[ $EUID -eq 0 ]] || { echo "Run as root." >&2; exit 1; }

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
APP_ROOT=/opt/remote-assist
BIN="$APP_ROOT/bin/remote-assist-server"
NEW_BIN="$APP_ROOT/bin/remote-assist-server.new"
DOWNLOAD="$APP_ROOT/downloads/RemoteAssist.exe"
TECH_PORTABLE="$APP_ROOT/downloads/Remote-Assist-Technician-Portable.zip"
TECH_INSTALLER="$APP_ROOT/downloads/Remote-Assist-Technician-Setup.exe"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
BACKUP="/root/remote-assist-update-$STAMP"

mkdir -m 0700 "$BACKUP"
RELEASE_REPO="${REMOTE_ASSIST_RELEASE_REPO:-}"
if [[ -z "$RELEASE_REPO" ]]; then
  ORIGIN_URL="$(git -C "$REPO_ROOT" config --get remote.origin.url 2>/dev/null || true)"
  RELEASE_REPO="$(printf '%s' "$ORIGIN_URL" | sed -E 's#^https://github.com/##; s#^git@github.com:##; s#\.git$##')"
fi
cd "$REPO_ROOT"

git rev-parse HEAD > "$BACKUP/previous-repo-commit.txt"
git pull --ff-only
git rev-parse HEAD > "$BACKUP/target-repo-commit.txt"

echo "Validating target source..."
go mod download
go test ./...
go vet ./...
if command -v node >/dev/null 2>&1; then
  node --check internal/webui/web/app.js
fi
go build -trimpath -ldflags='-s -w' -o "$NEW_BIN" ./cmd/server
chmod 0755 "$NEW_BIN"

if [[ -x "$BIN" ]]; then
  cp -a "$BIN" "$BACKUP/remote-assist-server.previous"
fi

if command -v mysqldump >/dev/null 2>&1; then
  echo "Creating pre-update MySQL snapshot..."
  mysqldump --protocol=socket --single-transaction --quick --skip-lock-tables     --default-character-set=utf8mb4 remote_assist > "$BACKUP/remote_assist.sql"
  chmod 0600 "$BACKUP/remote_assist.sql"
  sha256sum "$BACKUP/remote_assist.sql" > "$BACKUP/remote_assist.sql.sha256"
else
  echo "WARNING: mysqldump is unavailable; no automatic database snapshot was created." >&2
fi

rollback_server() {
  echo "New server failed its local health check. Rolling back the previous binary..." >&2
  if [[ -x "$BACKUP/remote-assist-server.previous" ]]; then
    install -m 0755 "$BACKUP/remote-assist-server.previous" "$BIN"
    systemctl restart remote-assist.service
    for _ in {1..20}; do
      if curl -fsS http://127.0.0.1:8787/api/health >/dev/null; then
        echo "Rollback succeeded. Previous server is healthy." >&2
        return 0
      fi
      sleep 1
    done
    echo "CRITICAL: rollback binary also failed health check." >&2
  else
    echo "CRITICAL: no previous server binary was available for rollback." >&2
  fi
  return 1
}

mv -f "$NEW_BIN" "$BIN"
if ! systemctl restart remote-assist.service; then
  rollback_server || true
  exit 1
fi

healthy=0
for _ in {1..30}; do
  if curl -fsS http://127.0.0.1:8787/api/health >/dev/null; then
    healthy=1
    break
  fi
  sleep 1
done
if [[ $healthy -ne 1 ]]; then
  journalctl -u remote-assist.service -n 100 --no-pager >&2 || true
  rollback_server || true
  exit 1
fi

download_release_asset() {
  local label="$1"
  local url="$2"
  local destination="$3"
  local kind="$4"

  local dir tmp
  dir="$(dirname "$destination")"
  tmp="$(mktemp "$dir/.download.XXXXXX")"

  if ! curl --fail --location --retry 3 --connect-timeout 15 --max-time 300 "$url" -o "$tmp"; then
    rm -f "$tmp"
    echo "WARNING: Could not refresh $label; keeping the currently published copy." >&2
    return 0
  fi

  python3 - "$tmp" "$kind" "$label" <<'PY_ASSET'
import hashlib
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
    if len(data) < 10_000 or not data.startswith(b"PK"):
        raise SystemExit(f"{label} is not a plausible ZIP archive")
    if not zipfile.is_zipfile(path):
        raise SystemExit(f"{label} ZIP validation failed")
else:
    raise SystemExit(f"unknown asset validation kind: {kind}")

print(f"{label} SHA-256:", hashlib.sha256(data).hexdigest())
PY_ASSET

  chmod 0644 "$tmp"
  mv -f "$tmp" "$destination"
}

echo "New server is healthy. Refreshing Windows release artifacts..."

AGENT_URL="${AGENT_URL:-${RELEASE_REPO:+https://github.com/$RELEASE_REPO/releases/download/preview-latest/Remote-Assist.exe}}"
TECH_PORTABLE_URL="${TECH_PORTABLE_URL:-${RELEASE_REPO:+https://github.com/$RELEASE_REPO/releases/download/preview-latest/Remote-Assist-Technician-Portable.zip}}"
TECH_INSTALLER_URL="${TECH_INSTALLER_URL:-${RELEASE_REPO:+https://github.com/$RELEASE_REPO/releases/download/preview-latest/Remote-Assist-Technician-Setup.exe}}"

download_release_asset "Windows customer agent" "$AGENT_URL" "$DOWNLOAD" pe
download_release_asset "Technician portable" "$TECH_PORTABLE_URL" "$TECH_PORTABLE" zip
download_release_asset "Technician installer" "$TECH_INSTALLER_URL" "$TECH_INSTALLER" pe

apache2ctl configtest
if [[ -n "${PUBLIC_BASE_URL:-}" ]]; then curl -fsS "${PUBLIC_BASE_URL%/}/api/health"; else echo "PUBLIC_BASE_URL not set; skipped public HTTPS health check."; fi
echo
echo "Update complete."
echo "Backup/rollback evidence: $BACKUP"
echo "Deployed commit: $(git rev-parse HEAD)"
