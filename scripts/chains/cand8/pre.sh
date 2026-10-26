#!/usr/bin/env bash
# candidate 8 (env 1 copy of env 2's pf/pre.sh): chain_pf's one-time checks (from the table): N2 - a back dash pressed 0.3 m from the arena's end turns along the edge;
# N3 - a gnome swarm (one boss bar): only the last gnome's death is a boss death. Prints PASS / FAIL lines.
set -u
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
[ -n "${XAUTHORITY:-}" ] || export XAUTHORITY=$(ls -t /run/user/1000/xauth_* | head -1); export DISPLAY=${DISPLAY:-:0}
c() { python3 scripts/cmd.py "$@"; }
LOGF="/home/bara/.local/share/Steam/steamapps/common/Hand of Fate 2/BepInEx/LogOutput.log"
r() { echo "$(date +%T) $1 $2: $3"; }
python3 scripts/combat_fixture.py --fixture thieves --god --physical --equip BasicSword_noToken,BasicShield | tail -1
c "god on" > /dev/null; c "tough on" > /dev/null; c "vrdash uyf" > /dev/null 2>&1
python3 scripts/xrsim.py "head 0 1.7 0 0 0 0; stick l 0 0 0"
e=$(c "dash? edge 0.3 180"); echo "edge: $e"
f=$(c "dash? free 180" | cut -c1-120); echo "free: $f"
g=$(c "dash? predict b"); echo "ring: $g"
python3 scripts/xrsim.py "btn r a 150"; sleep 1.2
d=$(c "dash?" | grep -o "last .*" | cut -c1-260); echo "dash: $d"
# the dash turned along the edge and moved, and the back flick's ring stands off the straight-back line (turned too)
deg=$(echo "$g" | sed -n 's/.* at \(-\?[0-9]*\) deg.*/\1/p'); deg=${deg:-180}; a=${deg#-}
case "$d" in *"turned"*) if [ "$a" -lt 175 ]; then r PASS edgeturn "$d | $g"; else r FAIL edgeturn "the ring straight back: $g | $d"; fi;; *) r FAIL edgeturn "$e | $f | $g | $d";; esac
python3 -c "import sys; sys.path.insert(0, 'scripts'); import indicator_shots; print('left:', indicator_shots.leave())"
sleep 4
for i in 1 2 3 4 5 6 7 8; do s=$(c "state?" 2>/dev/null); case "$s" in *GameState_Map*) break;; esac; python3 scripts/xrsim.py "btn r a 150"; sleep 2; done
python3 scripts/combat_fixture.py --jump EL_Ambush_Thral --with "Card_MO_Gnome_Special*1" --god --physical --equip BasicSword_noToken,BasicShield | tail -1
c "enemy? Gnome" | cut -c1-160
# F8's facts: what the gnomes do (20 s, god and tough on): every gnome's action, the hero snared or not, the strikes
c "god on" > /dev/null; c "tough on" > /dev/null
c "enemysurvey" 10 | cut -c1-300
for k in $(seq 1 40); do echo "$(date +%T.%N | cut -c1-12) $(c "enemy? Gnome" 4 | cut -c1-220) | strike $(c "strike?" 4 | cut -c1-120) | $(c "pcombat?" 4 | grep -o "snared [A-Za-z]* struggles [0-9]*")"; sleep 0.4; done
python3 scripts/chains/cand8/pics.py shot f8_gnomes_probe
c "tough off" > /dev/null
n0=$(grep -c "boss death: not yet" "$LOGF"); b0=$(grep -c "boss death: Gnome" "$LOGF")
c "killall" 10 | cut -c1-120; sleep 3
n1=$(grep -c "boss death: not yet" "$LOGF"); b1=$(grep -c "boss death: Gnome" "$LOGF")
grep -h "boss death" "$LOGF" | tail -6 | cut -c1-200
if [ $((n1 - n0)) -ge 1 ] && [ $((b1 - b0)) -eq 1 ]; then r PASS swarmboss "swarm deaths +$((n1 - n0)), boss deaths +1 (the last gnome)"; else r FAIL swarmboss "swarm deaths +$((n1 - n0)), boss deaths +$((b1 - b0))"; fi
