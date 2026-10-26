#!/usr/bin/env bash
# Candidate 9 (env 1): two things seen in the tours' pictures, looked at again with the seams.
#  archers  the goblin archers' fight (the tours' fight stage was a goblin ambush): both eyes every 0.7 s for 20 s with the
#           shield up (a thin green streak from a goblin into the sky after a shield block, standing tour PT_fight_bars; a
#           blue arrowhead in the sky, seated tour PT_fight_bars), each with the mod's own renderers dumped (`bounds HoF2VR`)
#  title    what stands near the player at the title (the head walk's turn left showed a man at the view's left edge)
# From a session at the table (archers) / at the title (title). Output on stdout.
set -u
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
[ -n "${XAUTHORITY:-}" ] || export XAUTHORITY=$(ls -t /run/user/1000/xauth_* | head -1)
export CAND9_PICS=${CAND9_PICS:-$H/captures/cand9}
c() { python3 scripts/cmd.py "$@" 2>/dev/null; }
p() { python3 scripts/chains/cand9/pics.py "$@" 2>&1 | tail -1; }
sim() { python3 scripts/xrsim.py "$1" > /dev/null 2>&1; }
case "${1:-archers}" in
  archers)
    python3 scripts/combat_fixture.py --fixture archer --god --physical --equip BasicSword_noToken,BasicShield | tail -1
    c "god on" > /dev/null; c "tough on" > /dev/null; c "vrlook gilded" > /dev/null
    sim "head 0 1.65 0 0 0 0; hand r 0.25 1.2 -0.35 0 5 0; hand l -0.3 1.15 -0.35 0 0 0"
    sleep 1.5
    for k in $(seq 0 27); do
      echo "$(date +%T.%N | cut -c1-12) k $k | shots $(c 'shots?' 3 | cut -c1-160) | strike $(c 'strike?' 3 | cut -c1-120)"
      c "bounds HoF2VR 400" 5 > /dev/null
      p shot "x_archers_$k"
      sleep 0.3
    done
    c "shots?" 4 | cut -c1-300
    c "tough off" > /dev/null
    ;;
  title)
    for y in 30 60 90; do
      sim "head 0 1.66 0 $y -5 0"; sleep 1.0
      echo "turned left $y: $(c "eyenear 2.0" 5 | tr '|' '\n' | grep -v HoF2VR | head -12 | tr '\n' '|')"
      p shot "x_title_left$y"
    done
    sim "head 0 1.66 0 0 -5 0"
    ;;
esac
