#!/usr/bin/env bash
# env 2 playtest fixes: one session for F3/F4 rechecks, N2, N7, N8 and N3 (-> logs/pf/s2.out)
set -u
H=/home/bara/Projects/Flat2VR-env2/HandOfFate2
cd "$H" || exit 1
. "$H/scripts/chains/r3/env.sh"
c() { python3 scripts/cmd.py "$@"; }
say() { echo "=== $* ($(date +%T))"; }
grep -q '"user": true' /run/user/1000/vr-session/status.json 2>/dev/null && { say "the user is in VR"; exit 2; }
say "DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8)"
python3 scripts/session.py start --snapshot magician_mid --to table | tail -1
python3 scripts/stage2_test.py --tag PF3i --fixture thieves --equip BasicSword_noToken,BasicShield --stay --parts linecut,linecut,linecut,stickuse | grep -E "PASS|FAIL"
say "N2: the floor from the fixture's spot"; for d in 0 90 180 270; do c "dash? free $d" | cut -c1-330; done
say "N7"; c "shoves?" | cut -c1-200
c "abilitycharge 3" > /dev/null; python3 scripts/xrsim.py "head 0 1.7 0 0 0 0; hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0"; sleep 0.6
c "ability?" | grep -o "hudIcon [^|]*shows [0-9]*"
python3 scripts/chains/pf/pics.py shot f4_hud_stick_glyph_ready
python3 scripts/xrsim.py "btn r stick 150"; sleep 0.8; c "ability?" | grep -o "hudIcon [^|]*shows [0-9]*"
python3 scripts/chains/pf/pics.py shot f4_hud_no_glyph_after_use
python3 -c "import sys; sys.path.insert(0, 'scripts'); import indicator_shots; print('left:', indicator_shots.leave())"
sleep 3
say "N8: the Duelling Daggers"
python3 scripts/combat_fixture.py --fixture thieves --god --physical --equip DuellingDaggers | tail -2
c "god on"; c "tough on"
python3 scripts/xrsim.py "head 0 1.7 0 0 0 0; hand r 0.25 1.3 -0.35 0 0 0; hand l -0.25 1.3 -0.35 0 0 0"; sleep 0.6
python3 scripts/chains/pf/pics.py shot n8_daggers_dagger_size
grep -h "held at x\|DuellingDaggersModel(Clone) local bounds" "/home/bara/Games/hof2-env2/Hand of Fate 2/BepInEx/LogOutput.log" | tail -3 | cut -c1-200
python3 -c "import sys; sys.path.insert(0, 'scripts'); import indicator_shots; print('left:', indicator_shots.leave())"
sleep 3
say "N3: the gnomes"
c "jump list Gnome" 8 | cut -c1-300
python3 scripts/combat_fixture.py --jump Gnomes --god --physical --equip BasicSword_noToken,BasicShield | tail -2
c "state?"
python3 scripts/chains/pf/pics.py shot f8_gnomes_before
c "enemy? Gnome" | cut -c1-300
c "killall" 10 | cut -c1-200
sleep 2
grep -h "boss death" "/home/bara/Games/hof2-env2/Hand of Fate 2/BepInEx/LogOutput.log" | tail -12 | cut -c1-200
c "bossdeath?"
cp "/home/bara/Games/hof2-env2/Hand of Fate 2/BepInEx/LogOutput.log" "$H/logs/pf/LogOutput-s2.log"
say "done"
