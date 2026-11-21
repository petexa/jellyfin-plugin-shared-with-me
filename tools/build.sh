#!/usr/bin/env bash
# Build or test the plugin inside the .NET 10 SDK container (needs Docker; nothing else to install).
# Usage: tools/build.sh build|test
# build: writes artifacts/Jellyfin.Plugin.SharedWithMe.dll
set -euo pipefail

MODE="${1:-}"
case "$MODE" in build|test) ;; *) echo "usage: $0 build|test" >&2; exit 2;; esac

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
CACHE="${SWM_NUGET_CACHE:-$HOME/.cache/swm-nuget}"
mkdir -p "$CACHE" "$REPO/artifacts"

# Copy the sources into the container so build output never lands in your working tree as root-owned files.
docker run --rm -v "$REPO":/repo:ro -v "$CACHE":/nuget -e NUGET_PACKAGES=/nuget -e MODE="$MODE" \
  -v "$REPO/artifacts":/out "$IMAGE" bash -euo pipefail -c '
    mkdir -p /src && cd /repo && tar -c --exclude=./artifacts --exclude=./dist --exclude=./.git --exclude=bin --exclude=obj . | tar -x -C /src
    cd /src
    if [ "$MODE" = build ]; then
      dotnet build Jellyfin.Plugin.SharedWithMe/Jellyfin.Plugin.SharedWithMe.csproj -c Release --nologo
      mkdir -p /src/artifacts
      cp /src/artifacts/Jellyfin.Plugin.SharedWithMe.dll /out/
    else
      dotnet test Jellyfin.Plugin.SharedWithMe.Tests/Jellyfin.Plugin.SharedWithMe.Tests.csproj -c Release --nologo
    fi
  '
[ "$MODE" = build ] && echo "built: $REPO/artifacts/Jellyfin.Plugin.SharedWithMe.dll"
exit 0
