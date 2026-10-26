#!/usr/bin/env bash
# waits until no other environment holds a reservation (or the user is in VR), then runs g1.sh (all gnome parts)
H=/home/bara/Projects/Flat2VR-env2/HandOfFate2
cd "$H" || exit 1
while scripts/reserve.sh status 2>&1 | grep -q "fresh" || grep -q '"user": true' /run/user/1000/vr-session/status.json 2>/dev/null; do sleep 30; done
echo "=== the slot is free ($(date +%T))"
PARTS=${PARTS:-pics,cling,watch,cut,dash} bash scripts/chains/pf/g1.sh
