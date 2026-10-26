#!/usr/bin/env bash
# a short look (env 2): a thieves fight's start (the push-out log), the glove pose (the white bar?), a crit slash up close
set -u
H=/home/bara/Projects/Flat2VR-env2/HandOfFate2
cd "$H" || exit 1
. "$H/scripts/chains/r3/env.sh"
c() { python3 scripts/cmd.py "$@"; }
L="/home/bara/Games/hof2-env2/Hand of Fate 2/BepInEx/LogOutput.log"
scripts/monado.sh start | tail -1
python3 scripts/session.py start --snapshot magician_mid --to table | tail -1
python3 scripts/combat_fixture.py --fixture thieves --god --physical --equip BasicSword_noToken,BasicShield | tail -1
python3 scripts/chains/pf/pics.py shot c8fix_thieves_start
grep -h "the fight's start" "$L" | tail -1
c "god on" > /dev/null; c "tough on" > /dev/null
python3 scripts/xrsim.py "head 0 1.7 0 0 0 0; hand r 0.25 1.35 -0.35 0 5 0; hand l -0.02 1.42 -0.27 0 70 0"; sleep 0.6
python3 scripts/chains/pf/pics.py shot c8fix_glove_pose
c "near 0 0 0 0.1" > /dev/null
python3 scripts/xrsim.py "hand l -0.32 0.85 0.05 0 -80 0"
c "poiseset nearest 0" > /dev/null; c "heroat nearest 0 1.0" > /dev/null; c "face nearest" > /dev/null; sleep 0.4
python3 scripts/chains/pf/pics.py shot c8fix_crit_close
c "pcombat?" | grep -o "nearPlaced [0-9]*"
c "poise? nearest" | cut -c1-200
scripts/stop.sh | tail -1
