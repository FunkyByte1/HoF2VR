#!/usr/bin/env bash
# F8 on the gnomes branch: a table, then gnome_test (watch, cut, dash, cling) with pictures -> logs/pf/g1.out
set -u
H=/home/bara/Projects/Flat2VR-env2/HandOfFate2
cd "$H" || exit 1
. "$H/scripts/chains/r3/env.sh"
P=${PARTS:-watch,cut,dash,cling}
echo "=== DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8) ($(date +%T)) parts $P"
scripts/monado.sh start | tail -1
python3 scripts/session.py start --snapshot magician_mid --to table | tail -1
python3 scripts/gnome_test.py --tag G1 --stay --parts "$P"
python3 scripts/cmd.py "gnomes?" 5 | cut -c1-400
cp "/home/bara/Games/hof2-env2/Hand of Fate 2/BepInEx/LogOutput.log" "$H/logs/pf/LogOutput-g1.log"
scripts/stop.sh | tail -1
echo "=== done ($(date +%T))"
