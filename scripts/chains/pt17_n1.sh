#!/usr/bin/env bash
# N1's proof (playtest fixes 2026-10-17, env 1): the bug mark's voice memo from a test game started the way Steam starts
# it (inside Steam Linux Runtime 1.0 on 2.0: run.sh HOF2VR_STEAMRT=1), recording from a stand-in PipeWire source whose
# name has "wivrn" in it (a null sink as a virtual source, unloaded at the end, also on failure).
# Run from the main worktree, outside the Bash sandbox. Output: logs/pt17_n1.out
set -uo pipefail
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
OUT="$H/logs/pt17_n1.out"
exec > >(tee -a "$OUT") 2>&1
echo "== $(date +%T) N1 proof"
MOD=$(pactl load-module module-null-sink sink_name=hof2vr_wivrn_test media.class=Audio/Source/Virtual 2>&1) || { echo "no stand-in source: $MOD"; exit 1; }
echo "stand-in source module $MOD"
cleanup() { pactl unload-module "$MOD" 2>/dev/null && echo "$(date +%T) stand-in source module $MOD unloaded"; }
trap cleanup EXIT
pactl list sources short | grep -i wivrn
export XAUTHORITY=$(ls /run/user/1000/xauth_* | head -1)
HOF2VR_STEAMRT=1 scripts/run.sh flat gl || { echo "run.sh failed"; exit 1; }
for i in $(seq 1 60); do
  s=$(python3 scripts/cmd.py "state?" 3 2>/dev/null || true)
  case "$s" in *StartScreen*|*Profile*|*Map*) break;; esac
  sleep 3
done
echo "game state: $s"
GP=$(pgrep -x 'Hand of Fate 2.' | while read -r p; do tr '\0' '\n' < /proc/$p/environ 2>/dev/null | grep -q '^HOF2VR_TEST=1' && echo $p; done | head -1)
echo "test game pid $GP"
if [ -n "$GP" ]; then
  echo "container: $(grep -c pressure-vessel /proc/$GP/mountinfo) pressure-vessel mounts; pactl inside: $([ -e /proc/$GP/root/usr/bin/pactl ] && echo yes || echo no); LD_LIBRARY_PATH has the scout runtime: $(tr '\0' '\n' < /proc/$GP/environ | grep -c '^LD_LIBRARY_PATH=.*steam-runtime')"
  tr '\0' '\n' < /proc/$GP/environ | grep -E '^PULSE_SERVER=|^PRESSURE_VESSEL_RUNTIME=' | head -3
fi
python3 scripts/cmd.py "mark voice" 10
sleep 14
M=$(ls -1td testdata/marks/*/ | head -1)
echo "mark dir $M"
ls -la "$M"
grep -h "^first person\|voice" "$M/state.txt" | head -2
cat "$M/voice.txt" 2>/dev/null
grep -a "BUG MARK\|bug mark voice\|bug mark capture" "/home/bara/.local/share/Steam/steamapps/common/Hand of Fate 2/BepInEx/LogOutput.log" | tail -4 | cut -c1-240
python3 scripts/session.py stop > /dev/null 2>&1 || scripts/stop.sh
scripts/savecheck.sh | tail -1
echo "== $(date +%T) done"
