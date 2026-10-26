#!/usr/bin/env bash
# Candidate 8's picture walk, the fights (env 1): thieves, soldiers, gnomes (the old gnome behaviour) and a boss - each:
# the start, an attack bar (the enemy's strike marker), a crit arrow (the nearest enemy's guard set to 0), after a dash,
# and the glove (the back of the shield hand toward the eyes). Both eyes -> captures/cand8/fight_<kind>_<view>.jpg.
# From the table (a session at the map). Prints one line per picture and PASS/FAIL per fight start.
set -u
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
[ -n "${XAUTHORITY:-}" ] || export XAUTHORITY=$(ls -t /run/user/1000/xauth_* | head -1)
GEAR="BasicSword_noToken,BasicShield"
c() { python3 scripts/cmd.py "$@" 2>/dev/null; }
p() { python3 scripts/chains/cand8/pics.py "$@" 2>&1 | tail -1; }
sim() { python3 scripts/xrsim.py "$1" > /dev/null 2>&1; }
leave() { python3 -c "import sys; sys.path.insert(0, 'scripts'); import indicator_shots; print('left:', indicator_shots.leave())" 2>&1 | tail -1; }
back_to_map() {
  local i s
  for i in 1 2 3 4 5 6 7 8 9 10; do s=$(c "state?" 3); case "$s" in *GameState_Map*) return 0;; esac; sim "btn r a 150"; sleep 2; done
  return 1
}
fight() {
  local kind=$1; shift
  echo "== $kind ($(date +%T))"
  if ! python3 scripts/combat_fixture.py "$@" --god --physical --equip $GEAR > "logs/cand8_fightpics_$kind.out" 2>&1; then
    echo "$(date +%T) FAIL fightpics_$kind: no fight ($(tail -1 logs/cand8_fightpics_$kind.out | cut -c1-120))"; back_to_map; return; fi
  echo "$(date +%T) PASS fightpics_$kind: $(tail -1 logs/cand8_fightpics_$kind.out | cut -c1-120)"
  c "god on" > /dev/null; c "tough on" > /dev/null
  sim "head 0 1.75 0 0 0 0; hand r 0.25 1.2 -0.25 0 -20 0; hand l -0.25 1.2 -0.25 0 -20 0"
  sleep 1.5
  p shot "fight_${kind}_start"
  p strike "fight_${kind}_attack_bar" 25
  c "poiseset nearest 0" > /dev/null
  p crit "fight_${kind}_crit_arrow" 6
  sim "stick l 0 -1 400; btn r a 150"
  sleep 1.2
  p shot "fight_${kind}_after_dash"
  # the glove: the left hand raised, its back toward the eyes (the health gems)
  sim "hand l -0.02 1.42 -0.28 -70 -10 -90"
  sleep 0.8
  p shot "fight_${kind}_glove"
  if [ "$kind" = gnomes ]; then
    # the left hand and the shield close up from the side (what the white bar beside the shield is)
    sim "head -0.62 1.30 -0.25 -90 -12 0"; sleep 0.8; p shot "fight_${kind}_shield_side"
    sim "head -0.25 1.55 -0.05 0 -55 0"; sleep 0.8; p shot "fight_${kind}_shield_above"
    c "weaponparts" 5 | cut -c1-300
    sim "head 0 1.75 0 0 0 0"
  fi
  sim "hand l -0.25 1.0 -0.2 0 -60 0"
  c "tough off" > /dev/null
  leave
  sleep 3
  back_to_map || echo "$(date +%T) note: not back on the map after $kind ($(c 'state?' 3))"
}
fight thieves --fixture thieves
fight soldiers --fixture shield
fight gnomes --jump Gnomes
fight boss --fixture boss_knight
echo "== done ($(date +%T))"
