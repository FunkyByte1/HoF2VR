#!/usr/bin/env bash
# Leave the machine ready for real play: no test mode, no test tools running, final build installed.
# Moves (never deletes) the leftovers of testing into backups/handover-<time>/.
set -uo pipefail
# (H, G from hof2env.sh; a handover is environment 1's: the user's Steam install, docs/two-environments.md)
. "$(dirname "${BASH_SOURCE[0]}")/hof2env.sh"
[ "$HOF2VR_ENV" = 1 ] || { echo "environment $HOF2VR_ENV: the handover is done from environment 1 (main) after merging" >&2; exit 2; }
B="$H/backups/handover-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$B"

bash "$H/scripts/stopauto.sh"
bash "$H/scripts/stop.sh" >/dev/null 2>&1
bash "$H/scripts/monado.sh" stop >/dev/null 2>&1; sleep 2

# test leftovers in the game folder
for f in hof2vr_testmode.txt "Hand of Fate 2.dev"; do
  [ -e "$G/$f" ] && mv "$G/$f" "$B/" && echo "moved $f"
done
# settings file: regenerated with the final defaults at the first start
[ -e "$G/BepInEx/config/flat2vr.hof2vr.cfg" ] && mv "$G/BepInEx/config/flat2vr.hof2vr.cfg" "$B/" && echo "moved flat2vr.hof2vr.cfg"

# the game's per-user data on this PC did not exist before my tests (prefs, Player.log, caches)
[ -e "$HOME/.config/unity3d/Defiant Development" ] && mkdir -p "$B/config-unity3d" && mv "$HOME/.config/unity3d/Defiant Development" "$B/config-unity3d/" && echo "moved ~/.config/unity3d/Defiant Development"
[ -e "$HOME/.cache/unity3d/Defiant Development" ] && mkdir -p "$B/cache-unity3d" && mv "$HOME/.cache/unity3d/Defiant Development" "$B/cache-unity3d/" && echo "moved ~/.cache/unity3d/Defiant Development"

# Steam's own backup of localconfig.vdf from when I set the launch option
for f in "$HOME"/.local/share/Steam/userdata/STEAM_ACCOUNT_ID/config/localconfig.vdf.dvr-backup-*; do
  [ -e "$f" ] && mv "$f" "$H/backups/" && echo "moved $(basename "$f") to backups/"
done

bash "$H/scripts/build.sh" >/dev/null && bash "$H/scripts/install.sh" --release
bash "$H/scripts/savecheck.sh"
echo "test mode flag: $([ -e "$G/hof2vr_testmode.txt" ] && echo PRESENT || echo gone)"
echo "launch option: $("$HOF2VR_SHARED/tools-venv/bin/python" -I "$HOF2VR_SHARED/scripts/steam_launch_options.py" show 456670)"
pgrep -a -x 'Hand of Fate 2.' && echo "!!! game still running" || echo "game not running"
pgrep -x monado-service >/dev/null && echo "!!! monado still running" || echo "monado not running"
echo "handover leftovers in $B"
