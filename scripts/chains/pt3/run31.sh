#!/usr/bin/env bash
# Item 30 and the bug hunt of 2026-10-18 (env 1): one short run - a session (snapshot, at the table), then each ';;'-separated command (python3 scripts), then
# the game stopped. The user always wins: not started while status.json says the user is in VR (run.sh and session.py check too).
#   run31.sh NAME SNAPSHOT (SNAPSHOT - = no session: the scripts start their own) "scripts/b1_test.py --parts touch,button" ";;" "scripts/b1_test.py --parts touch --shots" ...
#   (logs/pt3/run_NAME.out; the game log saved as logs/pt3/LogOutput-NAME.log)
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
name=$1 snap=$2; shift 2
OUT=logs/pt3/run_$name.out
G="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2"
# the user's own Unity prefs: read only, never written by a test game (finding 3; its hash before and after the run)
P="$HOME/.config/unity3d/Defiant Development/Hand of Fate 2/prefs"
if grep -q '"user": true' /run/user/1000/vr-session/status.json 2>/dev/null; then echo "the user is in VR - not starting" > $OUT; exit 9; fi
echo "== $name $(date +%T) DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8) | $(scripts/savecheck.sh 2>&1 | tail -1) | cfg $(sha1sum < "$G/BepInEx/config/flat2vr.hof2vr.cfg" | cut -c1-12) | user prefs $(sha1sum < "$P" | cut -c1-12) $(stat -c %y "$P" | cut -c1-19)" > $OUT
if [ "$snap" != "-" ]; then
  python3 scripts/session.py start --snapshot "$snap" --to table >> $OUT 2>&1
  echo "== session up $(date +%T): $(python3 scripts/cmd.py 'state?' 5)" >> $OUT
fi
for c in "$@"; do
  [ "$c" = ";;" ] && continue
  if grep -q '"user": true' /run/user/1000/vr-session/status.json 2>/dev/null; then echo "== the user is in VR - stopping" >> $OUT; break; fi
  echo "== $(date +%T) $c" >> $OUT
  # (word splitting on purpose: each command is a script and its arguments)
  timeout ${RUN_TIMEOUT:-1500} python3 $c >> $OUT 2>&1
  echo "== rc $? $(date +%T)" >> $OUT
done
cp "$G/BepInEx/LogOutput.log" logs/pt3/LogOutput-$name.log
echo "== exceptions $(grep -c Exception logs/pt3/LogOutput-$name.log)" >> $OUT
python3 scripts/session.py stop >> $OUT 2>&1
echo "== stopped $(date +%T) | $(scripts/savecheck.sh 2>&1 | tail -1) | cfg $(sha1sum < "$G/BepInEx/config/flat2vr.hof2vr.cfg" | cut -c1-12) | user prefs $(sha1sum < "$P" | cut -c1-12) $(stat -c %y "$P" | cut -c1-19)" >> $OUT
