#!/usr/bin/env bash
# Remove the Hand of Fate 2 VR mod and put the game back the way Steam installed it.
#   uninstall.sh
# - deletes the mod files from the game folder and restores the original data.unity3d
# - clears the Steam launch option (only possible while Steam is closed; otherwise a harmless
#   pass-through hof2vr_launch.sh stays so the game still starts, and you can clear the option
#   yourself: Steam > Hand of Fate 2 > Properties > Launch options)
# - removes the "Hand of Fate 2 VR" desktop entry
# Your saves are not touched (they live in Steam Cloud). Reinstall with: build.sh && install.sh
set -euo pipefail
# (H, G from hof2env.sh; only environment 1's Steam install is ever uninstalled, docs/two-environments.md)
. "$(dirname "${BASH_SOURCE[0]}")/hof2env.sh"
[ "$HOF2VR_ENV" = 1 ] || { echo "environment $HOF2VR_ENV: uninstall.sh is for the user's Steam install (environment 1)" >&2; exit 2; }
D="$G/Hand of Fate 2_Data"
B="$H/backups/20261010-063954-pre-mod"
ORIG_SHA=$(grep 'data.unity3d' "$B/game-sha256.txt" | cut -d' ' -f1)

if pgrep -x 'Hand of Fate 2.' >/dev/null; then echo "Quit the game first."; exit 1; fi

# data.unity3d back to the original (only the VR device list and the HRTF spatializer's name and preload entry differ)
if [ "$(sha256sum < "$D/data.unity3d" | cut -d' ' -f1)" != "$ORIG_SHA" ]; then
  cp "$B/data.unity3d" "$D/data.unity3d"
fi
[ "$(sha256sum < "$D/data.unity3d" | cut -d' ' -f1)" = "$ORIG_SHA" ] && echo "data.unity3d: original restored" \
  || { echo "data.unity3d: could not restore - use Steam > Properties > Installed Files > Verify integrity"; exit 1; }

rm -rf "$G/BepInEx"
rm -f "$G/.doorstop_version" "$G/libdoorstop.so" "$G/run_bepinex.sh" "$G/libmonosgen-2.0.so" \
      "$G/hof2vr_testmode.txt" "$G/disable_vr.txt" "$G/Hand of Fate 2.dev" "$G/hof2vr_stop_tests" "$G/hof2vr_test_running" \
      "$D/Plugins/x86_64/libopenvr_api.so" "$D/Plugins/x86_64/libaudioplugin_phonon.so" "$D/Plugins/x86_64/libphonon.so"
rm -f "$HOME/.local/share/applications/Hand of Fate 2 VR.desktop"
rm -rf "$HOME/.local/share/hof2vr"

if pgrep -x steam >/dev/null; then
  printf '#!/bin/sh\n# Hand of Fate 2 VR was uninstalled; this only starts the game normally.\nexec "$@"\n' > "$G/hof2vr_launch.sh"
  chmod +x "$G/hof2vr_launch.sh"
  echo "Steam is running: left a pass-through hof2vr_launch.sh. Clear the launch option in Steam"
  echo "(Hand of Fate 2 > Properties > Launch options), then you may delete hof2vr_launch.sh."
else
  "$HOF2VR_SHARED/tools-venv/bin/python" -I "$HOF2VR_SHARED/scripts/steam_launch_options.py" set 456670 ""
  rm -f "$G/hof2vr_launch.sh"
  echo "Steam launch option cleared."
fi
echo "Hand of Fate 2 VR removed."
