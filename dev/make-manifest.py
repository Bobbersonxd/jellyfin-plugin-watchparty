#!/usr/bin/env python3
"""Print a Jellyfin plugin repository manifest for one release zip.

Usage: make-manifest.py ZIP VERSION SOURCE_URL [EXISTING_MANIFEST]
With EXISTING_MANIFEST, the new version is prepended to that manifest's version list.
"""
import datetime
import hashlib
import json
import sys

import yaml_lite

zip_path, version, source_url = sys.argv[1:4]
meta = yaml_lite.load("build.yaml")
entry = {
    "version": version,
    "changelog": meta.get("changelog", ""),
    "targetAbi": meta["targetAbi"],
    "sourceUrl": source_url,
    "checksum": hashlib.md5(open(zip_path, "rb").read()).hexdigest(),
    "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}
plugin = {
    "guid": meta["guid"],
    "name": meta["name"],
    "overview": meta["overview"],
    "description": meta["description"],
    "owner": meta["owner"],
    "category": meta["category"],
    "versions": [],
}
if len(sys.argv) > 4:
    try:
        existing = json.load(open(sys.argv[4]))
        plugin["versions"] = [v for v in existing[0]["versions"] if v["version"] != version]
    except (FileNotFoundError, IndexError, KeyError, json.JSONDecodeError):
        pass
plugin["versions"].insert(0, entry)
print(json.dumps([plugin], indent=2))
