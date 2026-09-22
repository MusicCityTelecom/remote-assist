#!/usr/bin/env bash
set -Eeuo pipefail
[[ $EUID -eq 0 ]] || { echo "Run as root." >&2; exit 1; }
SRC="${1:-}"
[[ -n "$SRC" && -f "$SRC" ]] || { echo "Usage: $0 /path/to/RemoteAssist.exe" >&2; exit 1; }
DST=/opt/remote-assist/downloads/RemoteAssist.exe
ENV_FILE=/etc/remote-assist-app/app.env
install -d -m 0755 "$(dirname "$DST")"
install -m 0644 "$SRC" "$DST.new"
mv -f "$DST.new" "$DST"
sha256sum "$DST"
PUBLIC_URL="${PUBLIC_BASE_URL:-}"
if [[ -z "$PUBLIC_URL" && -r "$ENV_FILE" ]]; then
  PUBLIC_URL="$(sed -n 's/^PUBLIC_BASE_URL=//p' "$ENV_FILE" | tail -n 1)"
fi
if [[ -n "$PUBLIC_URL" ]]; then
  echo "Published: ${PUBLIC_URL%/}/download/windows"
else
  echo "Published to: $DST"
fi
