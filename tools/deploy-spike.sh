#!/usr/bin/env bash
# Install the packaged plugin into a DISPOSABLE test Jellyfin (a container named jf-share-spike whose config folder is
# /opt/jf-share-spike/config) the way a manual install does: unzip dist/SharedWithMe_<version>.zip into
# plugins/SharedWithMe_<version>/, restart the container and wait until the server answers.
# Never point this at a server you care about.
#
# SWM_JF_URL  the test server's address (default http://localhost:8097)
# SWM_RUN     command that runs a bash script read from stdin on the Docker host (default: bash -s, this machine;
#             for another machine, for example: SWM_RUN='ssh me@docker-host bash -s')
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\([0-9.]*\)</Version>.*:\1:p' "$REPO/Jellyfin.Plugin.SharedWithMe/Jellyfin.Plugin.SharedWithMe.csproj" | head -1)"
[ -n "$VERSION" ] || { echo "no <Version> in the csproj" >&2; exit 1; }
ZIP="$REPO/dist/SharedWithMe_${VERSION}.zip"
URL="${SWM_JF_URL:-http://localhost:8097}/System/Info/Public"
RUNNER="${SWM_RUN:-bash -s}"
DEST="/opt/jf-share-spike/config/plugins/SharedWithMe_${VERSION}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

python3 "$REPO/tools/package.py" --zip-only
python3 -c 'import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$ZIP" "$TMP/unzipped"
[ -f "$TMP/unzipped/Jellyfin.Plugin.SharedWithMe.dll" ] && [ -f "$TMP/unzipped/meta.json" ] || { echo "the zip lacks the DLL or meta.json" >&2; exit 1; }

# The files travel inside the script (base64), so the runner needs nothing but a shell.
{
  echo 'set -euo pipefail'
  echo 'for d in /opt/jf-share-spike/config/plugins/SharedWithMe_* "/opt/jf-share-spike/config/plugins/Shared With Me_"*; do [ -e "$d" ] && rm -rf "$d"; done'
  echo "mkdir -p '$DEST'"
  echo "base64 -d > '$DEST/Jellyfin.Plugin.SharedWithMe.dll' <<'B64'"
  base64 "$TMP/unzipped/Jellyfin.Plugin.SharedWithMe.dll"
  echo 'B64'
  echo "base64 -d > '$DEST/meta.json' <<'B64'"
  base64 "$TMP/unzipped/meta.json"
  echo 'B64'
  echo 'docker restart jf-share-spike'
} | $RUNNER

for _ in $(seq 1 60); do
  if curl -fsS -m 3 "$URL" >/dev/null 2>&1; then echo "spike is up"; exit 0; fi
  sleep 2
done
echo "spike did not come back within 120s" >&2
exit 1
