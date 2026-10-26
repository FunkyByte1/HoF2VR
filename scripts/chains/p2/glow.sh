#!/usr/bin/env bash
# env 2, P2-9: both-eye pictures of the full super meter's glow on the blade (a fight running; Gilded, the user's look):
# the sword pointing away from the eyes (mark 213810_15's pose), held up, and across the view.  glow.sh [TAG]
set -u
H=/home/bara/Projects/Flat2VR-env2/HandOfFate2
cd "$H" || exit 1
. "$H/scripts/chains/r3/env.sh"
T=${1:-p2_9}
export PICS_OUT=$H/captures/fixes-p2-combat
c() { python3 scripts/cmd.py "$@"; }
s() { python3 scripts/xrsim.py "$@"; }
c "vrlook gilded"; c "god on"
c "super? set 100" | cut -c1-90
# away from the eyes, forward and a little up (mark 15)
s "head 0 1.65 0 0 -10 0; hand r 0.18 1.15 -0.32 0 25 0; hand l -0.45 0.8 0.2 0 0 0"; sleep 0.6
python3 scripts/chains/pf/pics.py shot ${T}_glow_away
# held up beside the face
s "hand r 0.3 1.3 -0.35 0 80 0"; sleep 0.6
python3 scripts/chains/pf/pics.py shot ${T}_glow_up
# the meter spent: no glow (the same pose as the first)
c "super? set 0" | cut -c1-60
s "hand r 0.18 1.15 -0.32 0 25 0"; sleep 0.6
python3 scripts/chains/pf/pics.py shot ${T}_noglow_away
c "super?" | cut -c1-120
