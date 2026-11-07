#!/usr/bin/env bash
# Stage the vendored local-NuGet artifact + build the realtime image.
# Ar.Auth.OpenId is packed from the sibling repos into ../local-nuget with the
# same version pin as the published GitHub Packages artifact (Realtime.csproj);
# docker can't see outside the build context, so the nupkg is vendored into
# nuget-feed/ (gitignored — never committed).
set -euo pipefail
cd "$(dirname "$0")/.."

src="/mnt/lfs/Users/adolf/source/repos/local-nuget"
mkdir -p nuget-feed
# vendor every version; the csproj pin decides which one restores
cp "$src"/Ar.Auth.OpenId.*.nupkg nuget-feed/

exec docker build -t realtime:latest "$@" .
