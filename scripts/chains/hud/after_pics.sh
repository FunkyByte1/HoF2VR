#!/usr/bin/env bash
# env 3 (00:35): once the night pictures are in, stop chain 2092790 (DLL 9ab2a376, before the p3combat merge), build the merged
# branch and run the regression on it (hud_test again, p3 parts, the fight blocks, focus_test). Unattended.
cd /home/bara/Projects/Flat2VR-env3/HandOfFate2
L=logs/after_pics.log
for i in $(seq 1 1400); do grep -q "pics night" logs/hudc.log && break; grep -q "chain end" logs/hudc.log && break; sleep 10; done
echo "night pictures done ($(date +%T))" >> $L
tree() { echo $1; for c in $(pgrep -P $1); do tree $c; done; }
[ -e /proc/2092790 ] && kill -TERM $(tree 2092790) 2>/dev/null
sleep 5
python3 scripts/hud_pics.py sheet >> $L 2>&1
scripts/stop.sh >> $L 2>&1
while grep -q '"user": true' /run/user/1000/vr-session/status.json; do sleep 30; done
timeout 900 scripts/build.sh > logs/after_pics_build.out 2>&1; echo "build rc $? DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8) head $(git rev-parse --short HEAD) ($(date +%T))" >> $L
NAME=hudr HUD=1 PICS=0 nohup setsid bash scripts/chains/hud/chain_hud.sh > /dev/null 2>&1 &
echo "regression chain hudr started ($(date +%T))" >> $L
