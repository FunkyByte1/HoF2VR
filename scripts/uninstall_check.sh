#!/usr/bin/env bash
# uninstall.sh's game-file steps checked on a scratch copy (the coordinator 2026-10-14: "uninstall.sh restores
# data.unity3d byte-identical (check the hash) and removes the .so files"). The real uninstall.sh is never run on
# environment 1's Steam install for a test: it also deletes BepInEx/ (the player's settings and session logs), the
# desktop entry, ~/.local/share/hof2vr and the Steam launch option.
#   uninstall_check.sh
# Copies the installed data.unity3d and Plugins/x86_64/*.so into a scratch game folder, runs the lines of uninstall.sh
# that restore data.unity3d and delete the game files (extracted from the script itself, so a change there is what gets
# checked) with G pointed at the scratch folder, then compares data.unity3d's sha256 with the pre-mod backup's and checks
# that the HRTF libraries (and the OpenVR one) are gone. Exit 0 = PASS.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/hof2env.sh"
REAL_D="$G/Hand of Fate 2_Data"
S=$(mktemp -d "${TMPDIR:-/tmp}/hof2-uninstall-check.XXXXXX")
trap 'rm -rf "$S"' EXIT
SD="$S/Hand of Fate 2_Data"
mkdir -p "$SD/Plugins/x86_64"
cp --reflink=auto "$REAL_D/data.unity3d" "$SD/data.unity3d"
before_sha=$(sha256sum < "$SD/data.unity3d" | cut -d' ' -f1)
present=""
for f in libaudioplugin_phonon.so libphonon.so libopenvr_api.so; do
  if [ -e "$REAL_D/Plugins/x86_64/$f" ]; then cp "$REAL_D/Plugins/x86_64/$f" "$SD/Plugins/x86_64/"; present="$present $f"; fi
done
B="$H/backups/20261010-063954-pre-mod"
ORIG_SHA=$(grep 'data.unity3d' "$B/game-sha256.txt" | cut -d' ' -f1)
# the block of uninstall.sh from "# data.unity3d back" up to (not including) the desktop entry's removal
block=$(sed -n '/^# data.unity3d back to the original/,/^rm -f "\$HOME\/.local\/share\/applications/p' "$H/scripts/uninstall.sh" | grep -v '^rm -f "\$HOME/.local/share/applications')
[ -n "$block" ] || { echo "FAIL: the block was not found in uninstall.sh"; exit 1; }
echo "installed: data.unity3d sha256 ${before_sha:0:16} (original ${ORIG_SHA:0:16}); libraries:${present:- none}"
( G="$S"; D="$SD"; eval "$block" )
after_sha=$(sha256sum < "$SD/data.unity3d" | cut -d' ' -f1)
left=$(ls "$SD/Plugins/x86_64/" 2>/dev/null | tr '\n' ' ')
ok=1
[ "$after_sha" = "$ORIG_SHA" ] || ok=0
[ -z "$left" ] || ok=0
echo "after the uninstall's steps: data.unity3d sha256 ${after_sha:0:16} ($( [ "$after_sha" = "$ORIG_SHA" ] && echo "= the original, byte-identical" || echo "NOT the original")); left in Plugins/x86_64: ${left:-nothing}"
[ "$ok" = 1 ] && { echo "PASS uninstall_restores_data_and_removes_libraries"; exit 0; } || { echo "FAIL uninstall_restores_data_and_removes_libraries"; exit 1; }
