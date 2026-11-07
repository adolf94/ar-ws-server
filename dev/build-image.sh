#!/usr/bin/env bash
# Stage the vendored local-NuGet artifact + build the realtime image.
# Ar.Auth.OpenId is produced by the sibling repo (`ar-auth` → ../local-nuget);
# docker can't see outside the build context, so the needed nupkg is vendored
# into nuget-feed/ (gitignored — never committed).
set -euo pipefail
cd "$(dirname "$0")/.."

src="/mnt/lfs/Users/adolf/source/repos/local-nuget"
mkdir -p nuget-feed
# vendor every version; the csproj pins which one restores
cp "$src"/Ar.Auth.OpenId.*-local.nupkg nuget-feed/

exec docker build -t realtime:latest "$@" .
