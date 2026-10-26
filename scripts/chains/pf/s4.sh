#!/usr/bin/env bash
# env 2 playtest fixes after the reboot: a table, then pre.sh (N2's edge turn and ring, N3's gnome swarm) -> logs/pf/s4.out
set -u
H=/home/bara/Projects/Flat2VR-env2/HandOfFate2
cd "$H" || exit 1
. "$H/scripts/chains/r3/env.sh"
echo "=== DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8) ($(date +%T))"
scripts/monado.sh start | tail -1
python3 scripts/session.py start --snapshot magician_mid --to table | tail -2
bash scripts/chains/pf/pre.sh
cp "/home/bara/Games/hof2-env2/Hand of Fate 2/BepInEx/LogOutput.log" "$H/logs/pf/LogOutput-s4.log"
echo "=== done ($(date +%T))"
