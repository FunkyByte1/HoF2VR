#!/usr/bin/env bash
# Build the BepInEx plugin -> build/bin/HoF2VR.dll
set -euo pipefail
# no heavy CPU work while the user is in VR (scripts/userguard.sh; it also sets H and HOF2VR_SHARED)
. "$(dirname "$0")/userguard.sh"
hof2vr_guard
cd "$H/src/HoF2VR"
# the workspace's toolchain caches are shared by every worktree (the output goes to this worktree's build/)
export DOTNET_CLI_HOME="$HOF2VR_SHARED/dotnet-home" \
       NUGET_PACKAGES="$HOF2VR_SHARED/nuget-packages" \
       DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
dotnet build -c Release -nologo -clp:NoSummary -v q "$@" 2>&1 | grep -vE '^\s*$' || true
test -f ../../build/bin/HoF2VR.dll && ls -la ../../build/bin/HoF2VR.dll
