#!/usr/bin/env bash
# Builds a release zip + Jellyfin repository manifest in .devserver/repo and serves it on :8765,
# so a test server can install Watch Party through Dashboard -> Plugins -> Repositories -> Catalog.
# Inside the podman test container the repository URL is http://host.containers.internal:8765/manifest.json
set -euo pipefail
cd "$(dirname "$0")/.."
version=1.0.0.0
base_url="${REPO_BASE_URL:-http://host.containers.internal:8765}"
out=.devserver/repo
rm -rf "$out" && mkdir -p "$out"
dotnet build src/Jellyfin.Plugin.WatchParty -c Release >/dev/null
zip -j -q "$out/watchparty_${version}.zip" src/Jellyfin.Plugin.WatchParty/bin/Release/net9.0/Jellyfin.Plugin.WatchParty.dll
python3 dev/make-manifest.py "$out/watchparty_${version}.zip" "$version" "$base_url/watchparty_${version}.zip" > "$out/manifest.json"
echo "Repository: $base_url/manifest.json"
[[ "${1:-}" == "--no-serve" ]] && exit 0
exec python3 -m http.server 8765 --directory "$out"
