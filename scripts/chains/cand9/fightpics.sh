#!/usr/bin/env bash
# Candidate 9's picture walk, the fights (env 1; from cand8/fightpics.sh): thieves, soldiers, gnomes (F8; with a bomb
# thrower if the fixture takes one, else a bomb fight of its own) and a boss. Each: the head walk at the start (both eyes at
# rest, turned 30 deg left / right, leaned 20 cm forward / right, the mod's objects checked to stay put: headwalk.py), an
# attack bar, a crit arrow, after a dash, the glove; the thieves also the sword's full-meter glow (P2-9, the user's Gilded
# look); the gnomes F8's moments (gnome_test pics: the wind-up line, the leap in slow motion, the ring, broken); the bombs (always
# their own fight too) the bar, the knock, the off-view marker (bomb_test --pics). Both eyes -> captures/cand9/f_<kind>_<view>.jpg.
# From the table (a session at the map). Prints one line per picture and PASS/FAIL per fight start.
set -u
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
[ -n "${XAUTHORITY:-}" ] || export XAUTHORITY=$(ls -t /run/user/1000/xauth_* | head -1)
export CAND9_PICS=${CAND9_PICS:-$H/captures/cand9}
export PICS_OUT=$CAND9_PICS
FPO=${FIGHTPICS_O:-logs/cand9}   # the fixtures' outputs (candidate 10's chain: logs/cand10)
GEAR="BasicSword_noToken,BasicShield"
c() { python3 scripts/cmd.py "$@" 2>/dev/null; }
p() { python3 scripts/chains/cand9/pics.py "$@" 2>&1 | tail -1; }
sim() { python3 scripts/xrsim.py "$1" > /dev/null 2>&1; }
hw() { timeout 240 python3 scripts/chains/cand9/headwalk.py "$@" 2>&1 | grep HEADWALK | tail -1; }
leave() { python3 -c "import sys; sys.path.insert(0, 'scripts'); import indicator_shots; print('left:', indicator_shots.leave())" 2>&1 | tail -1; }
back_to_map() {
  local i s
  for i in 1 2 3 4 5 6 7 8 9 10; do s=$(c "state?" 3); case "$s" in *GameState_Map*) return 0;; esac; sim "btn r a 150"; sleep 2; done
  return 1
}
start_fight() {
  local kind=$1; shift
  echo "== $kind ($(date +%T))"
  if ! python3 scripts/combat_fixture.py "$@" --god --physical --equip $GEAR > "$FPO/fightpics_$kind.out" 2>&1; then
    echo "$(date +%T) FAIL fightpics_$kind: no fight ($(tail -1 "$FPO/fightpics_$kind.out" | cut -c1-120))"; back_to_map; return 1; fi
  echo "$(date +%T) PASS fightpics_$kind: $(tail -1 "$FPO/fightpics_$kind.out" | cut -c1-120)"
  c "god on" > /dev/null; c "tough on" > /dev/null; c "vrlook gilded" > /dev/null
  return 0
}
end_fight() {
  sim "hand l -0.25 1.0 -0.2 0 -60 0; hand r 0.25 1.0 -0.2 0 -60 0"
  c "tough off" > /dev/null
  leave
  sleep 3
  back_to_map || echo "$(date +%T) note: not back on the map after $1 ($(c 'state?' 3))"
}
views() {
  local kind=$1
  sim "head 0 1.75 0 0 0 0; hand r 0.25 1.2 -0.25 0 -20 0; hand l -0.25 1.2 -0.25 0 -20 0"
  sleep 1.5
  echo "$(hw f_${kind} --yaw 0 --pitch -5 --keep-hands)"
  sim "head 0 1.75 0 0 0 0; hand r 0.25 1.2 -0.25 0 -20 0; hand l -0.25 1.2 -0.25 0 -20 0"
  p strike "f_${kind}_attack_bar" 25
  c "poiseset nearest 0" > /dev/null
  p crit "f_${kind}_crit_arrow" 6
  sim "stick l 0 -1 400; btn r a 150"
  sleep 1.2
  p shot "f_${kind}_after_dash"
  # the glove: the left hand raised, its back toward the eyes (the health gems)
  sim "hand l -0.02 1.42 -0.28 -70 -10 -90"
  sleep 0.8
  p shot "f_${kind}_glove"
  sim "hand l -0.25 1.2 -0.25 0 -20 0"
}

# thieves (+ P2-9: the sword's glow with the meter full, as the user sees it)
if start_fight thieves --fixture thieves; then
  views thieves
  c "super? set 100" | cut -c1-90
  sim "head 0 1.65 0 0 -10 0; hand r 0.18 1.15 -0.32 0 25 0; hand l -0.45 0.8 0.2 0 0 0"; sleep 0.6
  p shot f_thieves_glow_away
  sim "hand r 0.3 1.3 -0.35 0 80 0"; sleep 0.6
  p shot f_thieves_glow_up
  sim "hand r 0.25 1.2 -0.35 0 5 0"; sleep 0.6
  p shot f_thieves_glow_guard
  c "super? set 0" | cut -c1-60
  sim "hand r 0.18 1.15 -0.32 0 25 0"; sleep 0.6
  p shot f_thieves_noglow_away
  end_fight thieves
fi
# soldiers (shield bearers)
if start_fight soldiers --fixture shield; then views soldiers; end_fight soldiers; fi
# gnomes, with a molotov thrower if the fixture takes one
if start_fight gnomes --jump EL_Ambush_Thral --with "Card_MO_Gnome_Special*1,ThievesMolotovThrowerREF*1"; then
  c "enemy?" 6 | cut -c1-300
  mixed=$(c "enemy?" 6 | grep -c Molotov); echo "gnome fight with a molotov thrower: $mixed"
else
  mixed=0
  start_fight gnomes --jump EL_Ambush_Thral --with "Card_MO_Gnome_Special*1" || true
fi
if c "state?" 3 | grep -q Level_Play; then
  c "vrgnomes on" > /dev/null
  sim "head 0 1.75 0 0 0 0; hand r 0.25 1.2 -0.25 0 -20 0; hand l -0.25 1.2 -0.25 0 -20 0"; sleep 1.0
  echo "$(hw f_gnomes --yaw 0 --pitch -10 --keep-hands)"
  timeout 420 python3 scripts/gnome_test.py --tag C9GP --no-fixture --stay --parts pics,airpic 2>&1 | grep -E 'PASS|FAIL|pics:|airpic:' | cut -c1-400
  sim "hand l -0.02 1.42 -0.28 -70 -10 -90"; sleep 0.8; p shot f_gnomes_glove
  c "gnomes?" 4 | cut -c1-300
  end_fight gnomes
fi
# bombs: the throwers' own fight (the bar, the knock, the off-view marker in flight; the gnome fight had a thrower: $mixed)
if start_fight bombs --fixture lobs; then
  sim "head 0 1.75 0 0 0 0; hand r 0.25 1.2 -0.25 0 -20 0; hand l -0.25 1.2 -0.25 0 -20 0"; sleep 1.0
  echo "$(hw f_bombs --yaw 0 --pitch -5 --keep-hands)"
  timeout 300 python3 scripts/bomb_test.py --tag C9BP --no-fixture --stay --parts knock,offview --pics 2>&1 | grep -E 'PASS|FAIL|pic |snap saved' | cut -c1-300
  end_fight bombs
fi
# a boss
if start_fight boss --fixture boss_knight; then views boss; end_fight boss; fi
echo "== done ($(date +%T))"
