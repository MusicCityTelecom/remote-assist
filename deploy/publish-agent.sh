#!/usr/bin/env bash
set -Eeuo pipefail
[[ $EUID -eq 0 ]] || { echo "Run as root." >&2; exit 1; }
SRC="${1:-}"
[[ -n "$SRC" && -f "$SRC" ]] || { echo "Usage: $0 /path/to/RemoteAssist.exe" >&2; exit 1; }
DST=/opt/remote-assist/downloads/RemoteAssist.exe
install -d -m 0755 "$(dirname "$DST")"
install -m 0644 "$SRC" "$DST.new"
mv -f "$DST.new" "$DST"
sha256sum "$DST"
echo "Published: https://support.example.com/download/windows"
