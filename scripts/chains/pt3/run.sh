#!/usr/bin/env bash
# Playtest 3 (env 1): one short run - a session (snapshot, place), a script, the game stopped. The user always wins: not started
# while status.json says the user is in VR.
#   run.sh NAME SNAPSHOT SCRIPT [ARGS]   (logs/pt3/run_NAME.out; the game log saved as logs/pt3/LogOutput-NAME.log)
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
name=$1 snap=$2; shift 2
OUT=logs/pt3/run_$name.out
G="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2"
if grep -q '"user": true' /run/user/1000/vr-session/status.json 2>/dev/null; then echo "the user is in VR - not starting" > $OUT; exit 9; fi
echo "== $name $(date +%T) DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8)" > $OUT
python3 scripts/session.py start --snapshot "$snap" --to table >> $OUT 2>&1
echo "== session up $(date +%T): $(python3 scripts/cmd.py 'state?' 5)" >> $OUT
timeout 1500 python3 "$@" >> $OUT 2>&1
echo "== script done rc $? $(date +%T)" >> $OUT
cp "$G/BepInEx/LogOutput.log" logs/pt3/LogOutput-$name.log
echo "== exceptions $(grep -c Exception logs/pt3/LogOutput-$name.log)" >> $OUT
python3 scripts/session.py stop >> $OUT 2>&1
echo "== stopped $(date +%T)" >> $OUT
