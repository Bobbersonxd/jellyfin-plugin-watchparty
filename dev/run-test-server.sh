#!/usr/bin/env bash
# Throwaway Jellyfin 10.11 test server on http://localhost:8097 (rootless podman).
# --sideload copies the Release build straight into the plugins folder (dev shortcut only).
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p .devserver/config .devserver/media
if [[ "${1:-}" == "--sideload" ]]; then
  dotnet build src/Jellyfin.Plugin.WatchParty -c Release >/dev/null
  dest=.devserver/config/plugins/WatchParty_1.0.0.0
  mkdir -p "$dest"
  cp src/Jellyfin.Plugin.WatchParty/bin/Release/net9.0/Jellyfin.Plugin.WatchParty.dll "$dest/"
fi
podman rm -f wp-test >/dev/null 2>&1 || true
podman run -d --name wp-test -p 8097:8096 \
  -v ./.devserver/config:/config:Z -v ./.devserver/media:/media:Z \
  docker.io/jellyfin/jellyfin:10.11.11 >/dev/null
echo "wp-test starting on http://localhost:8097"
