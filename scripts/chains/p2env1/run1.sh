#!/usr/bin/env bash
# One short game run of env 1 with the slot reserved for it alone (env 2 shares the slot): run1.sh NAME CMD...
# -> logs/p2env1/NAME.out, a copy of the game log logs/p2env1/NAME_game.log, a line in logs/p2env1.log.
set -u
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
name=$1; shift
LOG=$H/logs/p2env1.log
GAMELOG="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2/BepInEx/LogOutput.log"
if grep -q '"user": true' /run/user/1000/vr-session/status.json 2>/dev/null; then echo "=== $name: the user is in VR - not starting ($(date +%T))" | tee -a "$LOG"; exit 9; fi
scripts/reserve.sh start $$ >> "$LOG" 2>&1
trap 'cp "$GAMELOG" "$H/logs/p2env1/${name}_game.log" 2>/dev/null; scripts/stop.sh >> "$LOG" 2>&1; scripts/monado.sh stop >> "$LOG" 2>&1; scripts/reserve.sh stop >> "$LOG" 2>&1' EXIT
"$@" > "$H/logs/p2env1/$name.out" 2>&1
rc=$?
echo "=== $name rc $rc | $(grep -E '====|interact_test: [0-9]|RESULT' "$H/logs/p2env1/$name.out" | tail -1 | cut -c1-160) | FAILs $(grep -cE '^..:..:.. FAIL|RESULT .* FAIL' "$H/logs/p2env1/$name.out") | DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8) ($(date +%T))" | tee -a "$LOG"
grep -E '^..:..:.. FAIL|RESULT .* FAIL' "$H/logs/p2env1/$name.out" | cut -c1-300 | sed 's/^/    /' | tee -a "$LOG"
