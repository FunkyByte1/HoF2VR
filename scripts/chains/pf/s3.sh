#!/usr/bin/env bash
# env 2 playtest fixes: session 3 - N2 (a back dash at the arena's end), N7 (the shove grace), F3 again, N3 (a gnome swarm)
set -u
H=/home/bara/Projects/Flat2VR-env2/HandOfFate2
cd "$H" || exit 1
. "$H/scripts/chains/r3/env.sh"
c() { python3 scripts/cmd.py "$@"; }
say() { echo "=== $* ($(date +%T))"; }
LOGF="/home/bara/Games/hof2-env2/Hand of Fate 2/BepInEx/LogOutput.log"
grep -q '"user": true' /run/user/1000/vr-session/status.json 2>/dev/null && { say "the user is in VR"; exit 2; }
say "DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8)"
python3 scripts/session.py start --snapshot magician_mid --to table | tail -1
python3 scripts/combat_fixture.py --fixture thieves --god --physical --equip BasicSword_noToken,BasicShield | tail -1
c "god on"; c "tough on"
say "N7: shoves on now"; c "vrshoves on" | cut -c1-160; sleep 1; c "shoves?" | cut -c1-160; sleep 6.5; c "shoves?" | cut -c1-160
grep -h "Shoves: the fight's first" "$LOGF" | tail -1
c "vrshoves setting" > /dev/null
say "N2: the floor and a back dash from the fixture's spot"
python3 scripts/xrsim.py "head 0 1.7 0 0 0 0"
for d in 0 90 180 270; do c "dash? free $d" | cut -c1-200; done
c "vrdash uyf" > /dev/null 2>&1
python3 scripts/xrsim.py "btn r a 150"; sleep 1.2; c "dash?" | grep -o "last .*" | cut -c1-300
grep -h "dash: " "$LOGF" | tail -2 | cut -c1-300
python3 scripts/stage2_test.py --tag PF3j --no-fixture --stay --parts linecut,linecut,linecut | grep -E "PASS|FAIL"
python3 -c "import sys; sys.path.insert(0, 'scripts'); import indicator_shots; print('left:', indicator_shots.leave())"
sleep 3
say "N3: a gnome swarm"
python3 scripts/combat_fixture.py --jump EL_Ambush_Thral --with "Card_MO_Gnome_Special*1" --god --physical --equip BasicSword_noToken,BasicShield | tail -2
c "god on"
c "enemy? Gnome" | cut -c1-200
python3 scripts/chains/pf/pics.py shot n3_gnome_swarm
c "killall" 10 | cut -c1-200
sleep 3
grep -h "boss death" "$LOGF" | tail -12 | cut -c1-200
c "bossdeath?"
cp "$LOGF" "$H/logs/pf/LogOutput-s3.log"
say "done"
