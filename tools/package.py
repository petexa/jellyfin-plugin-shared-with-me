#!/usr/bin/env python3
"""Package the plugin for release: dist/SharedWithMe_<version>.zip, manifest.json and build.yaml.

The version comes from the csproj <Version>, the GUID from Plugin.PluginGuid in Plugin.cs; neither is typed here.
The zip is reproducible: the DLL and meta.json only, in a fixed order, stored (not compressed: no dependence on the host's zlib), with fixed timestamps and permissions, so two
runs over the same DLL give byte-identical zips. Every date written is the time of the last commit that changed the
csproj (where the version is set), never the clock, so committing manifest.json does not change it.
Build the DLL first (tools/build.sh build). Nothing is uploaded or published.

Usage: tools/package.py [--zip-only] [--source-url URL] [--owner NAME]
  --zip-only     write only the zip (tools/deploy-spike.sh uses this; manifest.json and build.yaml are left alone)
  --source-url   the zip's download URL in manifest.json (default: the REPLACE_AT_RELEASE placeholder)
  --owner        the owner shown in the catalogue (default: the placeholder)
"""
import argparse
import hashlib
import json
import re
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
CSPROJ = REPO / "Jellyfin.Plugin.SharedWithMe" / "Jellyfin.Plugin.SharedWithMe.csproj"
PLUGIN_CS = REPO / "Jellyfin.Plugin.SharedWithMe" / "Plugin.cs"
DLL = REPO / "artifacts" / "Jellyfin.Plugin.SharedWithMe.dll"
DIST = REPO / "dist"
PLACEHOLDER = "REPLACE_AT_RELEASE"
OWNER = PLACEHOLDER  # set from --owner in main()

NAME = "Shared With Me"
TARGET_ABI = "12.4.0.0"
CATEGORY = "General"
OVERVIEW = "Share one title with one other profile, such as a film above a child's age limit."
DESCRIPTION = ("A user who can see a film or series can share it with one other profile that cannot. The title appears "
               "for that profile in a library of its own. To keep it there, the plugin changes every profile's library "
               "access list and blocked tags: read the README before installing. Sharing works from the web page the "
               "plugin adds, and from TV apps through private playlists.")
CHANGELOG = "First release."


def version():
    m = re.search(r"<Version>([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)</Version>", CSPROJ.read_text())
    if not m:
        sys.exit(f"no four-part <Version> in {CSPROJ}")
    return m.group(1)


def guid():
    m = re.search(r'public const string PluginGuid = "([0-9a-f-]{36})";', PLUGIN_CS.read_text())
    if not m:
        sys.exit(f"no PluginGuid constant in {PLUGIN_CS}")
    return m.group(1)


def commit_time():
    out = subprocess.run(["git", "-C", str(REPO), "log", "-1", "--format=%ct", "--", str(CSPROJ.relative_to(REPO))],
                         capture_output=True, text=True, check=True)
    if not out.stdout.strip():
        sys.exit(f"{CSPROJ} has no commit yet")
    return datetime.fromtimestamp(int(out.stdout.strip()), tz=timezone.utc)


def meta(ver, when):
    return {
        "category": CATEGORY,
        "changelog": CHANGELOG,
        "description": DESCRIPTION,
        "guid": guid(),
        "name": NAME,
        "overview": OVERVIEW,
        "owner": OWNER,
        "targetAbi": TARGET_ABI,
        "timestamp": when.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "version": ver,
    }


def write_zip(ver, when):
    if not DLL.is_file():
        sys.exit(f"missing {DLL}: run tools/build.sh build first")
    DIST.mkdir(exist_ok=True)
    out = DIST / f"SharedWithMe_{ver}.zip"
    entries = [
        ("Jellyfin.Plugin.SharedWithMe.dll", DLL.read_bytes()),
        ("meta.json", (json.dumps(meta(ver, when), indent=2) + "\n").encode()),
    ]
    stamp = when.timetuple()[:6]
    with zipfile.ZipFile(out, "w") as z:
        for name, data in entries:
            info = zipfile.ZipInfo(name, date_time=stamp)
            info.compress_type = zipfile.ZIP_STORED  # stored, so the bytes do not depend on the host's zlib
            info.create_system = 3  # unix, so the permissions below are read
            info.external_attr = 0o100644 << 16
            z.writestr(info, data, compress_type=zipfile.ZIP_STORED)
    return out


def write_manifest(ver, when, checksum, source_url):
    entry = {
        "guid": guid(),
        "name": NAME,
        "description": DESCRIPTION,
        "overview": OVERVIEW,
        "owner": OWNER,
        "category": CATEGORY,
        "versions": [{
            "version": ver,
            "changelog": CHANGELOG,
            "targetAbi": TARGET_ABI,
            "sourceUrl": source_url,
            "checksum": checksum,
            "timestamp": when.strftime("%Y-%m-%dT%H:%M:%SZ"),
        }],
    }
    (REPO / "manifest.json").write_text(json.dumps([entry], indent=4) + "\n")


def write_build_yaml(ver):
    (REPO / "build.yaml").write_text(
        "# Written by tools/package.py from the csproj <Version> and Plugin.PluginGuid; do not edit by hand.\n"
        f'name: "{NAME}"\n'
        f'guid: "{guid()}"\n'
        f'version: "{ver}"\n'
        f'targetAbi: "{TARGET_ABI}"\n'
        'framework: "net10.0"\n'
        f'owner: "{OWNER}"\n'
        f'overview: "{OVERVIEW}"\n'
        "description: >\n"
        f"  {DESCRIPTION}\n"
        f'category: "{CATEGORY}"\n'
        "artifacts:\n"
        '  - "Jellyfin.Plugin.SharedWithMe.dll"\n'
        "changelog: >\n"
        f"  {CHANGELOG}\n")


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--zip-only", action="store_true")
    ap.add_argument("--source-url", default=PLACEHOLDER)
    ap.add_argument("--owner", default=PLACEHOLDER, help="the owner shown in the plugin catalogue (default: the placeholder)")
    args = ap.parse_args()
    global OWNER
    OWNER = args.owner
    ver, when = version(), commit_time()
    out = write_zip(ver, when)
    checksum = hashlib.md5(out.read_bytes()).hexdigest()
    if not args.zip_only:
        write_manifest(ver, when, checksum, args.source_url)
        write_build_yaml(ver)
    print(f"{out.relative_to(REPO)} md5 {checksum}")


if __name__ == "__main__":
    main()
