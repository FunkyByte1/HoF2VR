#!/usr/bin/env bash
# Candidate 10's picture walk (env 1, 2026-10-19), the fights candidate 9's walk (cand9/fightpics.sh: thieves, soldiers,
# gnomes, bombs, a boss) does not have. One PART per call, from a session at the dungeon map; the fight is left running
# (the chain's ensure_table leaves it). Needs the merged main (hud + p3combat: p3_pics.py, lines_pics.py, hud_pics.py, the
# seams vrhud / vrstatus / fighthud / envstate / shots?).
#   shield     one Empire soldier (EL_Ambush_Empire): the head walk and p3_pics shieldon (the shield placed on its circle by
#              script) with Board + Shell; then Body + Rings: the head walk, a blow, the wrist (its timer shown by the
#              `fighthud timer` seam), shieldon again
#   archers    goblin archers (fixture archer), the shield up: the head walk, the shots' ground arrows (gold, the circle)
#   lobs_red   molotov throwers, no shield (HugeHammer): the head walk, p3_pics ground (the red ground arrow). The hammer
#              stays equipped for the session: the chain starts a fresh one after this part
#   lobs_gold  molotov throwers, the shield: the gold-white ground arrow and the circle
#   night      a true night arena (EL_Ambush_Empire + `envstate Night`, skeletons): hud_pics.py night (board, body, shell,
#              rings, near), then the head walk with Board + Shell and with Body + Rings, a blow with Body + Rings
# Both eyes -> $CAND9_PICS (captures/cand10/f_<part>_*.jpg, headwalk.py's poses), p3_pics -> $CAND9_PICS/p3_<part>/,
# hud_pics -> $HUD_PICS (captures/cand10/hud). Prints PASS/FAIL lines (fight start, pictures) and HEADWALK lines.
#   fightpics.sh PART
set -u
case "${1:-}" in shield|archers|lobs_red|lobs_gold|night) ;; *) echo "fightpics.sh shield|archers|lobs_red|lobs_gold|night"; exit 2 ;; esac
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
[ -n "${XAUTHORITY:-}" ] || export XAUTHORITY=$(ls -t /run/user/1000/xauth_* | head -1)
export CAND9_PICS=${CAND9_PICS:-$H/captures/cand10}
export HUD_PICS=${HUD_PICS:-$CAND9_PICS/hud}
FPO=${FIGHTPICS_O:-$H/logs/cand10}
mkdir -p "$CAND9_PICS" "$HUD_PICS" "$FPO"
GEAR="BasicSword_noToken,BasicShield"
GUARD="head 0 1.75 0 0 0 0; hand r 0.25 1.2 -0.25 0 -20 0; hand l -0.25 1.2 -0.25 0 -20 0"
SHIELDUP="head 0 1.75 0 0 -20 0; hand r 0.25 1.2 -0.35 0 5 0; hand l -0.3 1.15 -0.35 0 0 0"
c() { python3 scripts/cmd.py "$@" 2>/dev/null; }
p() { python3 scripts/chains/cand9/pics.py "$@" 2>&1 | tail -1; }
sim() { python3 scripts/xrsim.py "$1" > /dev/null 2>&1; }
hw() { timeout 240 python3 scripts/chains/cand9/headwalk.py "$@" 2>&1 | grep HEADWALK | tail -1; }
r() { echo "$(date +%T) $1 $2: $3"; }
fight() {   # fight KIND combat_fixture's args (with --equip)
  local kind=$1; shift
  echo "== $kind ($(date +%T))"
  if ! python3 scripts/combat_fixture.py "$@" --god --physical > "$FPO/fightpics_$kind.out" 2>&1; then
    r FAIL "fightpics_$kind" "no fight ($(tail -1 "$FPO/fightpics_$kind.out" | cut -c1-120))"; return 1; fi
  r PASS "fightpics_$kind" "$(tail -1 "$FPO/fightpics_$kind.out" | cut -c1-120)"
  c "god on" > /dev/null; c "tough on" > /dev/null; c "vrlook gilded" > /dev/null
}
# board_shell = the player's settings (Board + Shell by DefaultsVersion 4); body_rings = the seams' override
combo() {
  case $1 in
    body_rings) echo "combo $1: $(c 'vrhud body') / $(c 'vrstatus rings')" ;;
    *) echo "combo $1: $(c 'vrhud setting') / $(c 'vrstatus setting')" ;;
  esac
}
walk() { sim "$GUARD"; sleep 1.2; hw "$1" --yaw 0 --pitch "${2:--5}" --keep-hands; sim "$GUARD"; }
# up to 3 pictures while a shot's or lob's ground arrow shows (shots?: ground True), 60 s at most
groundpics() {
  local name=$1 n=0 t0 s
  t0=$(date +%s)
  sim "$SHIELDUP"; c "face nearest" > /dev/null
  while [ $(( $(date +%s) - t0 )) -lt 60 ] && [ $n -lt 3 ]; do
    s=$(c "shots?" 4)
    case "$s" in
      *"ground True"*) sleep 0.25; p shot "${name}_$n" > /dev/null; n=$((n+1)); echo "  $name $n: $(echo "$s" | cut -c1-220)"; sleep 1.0 ;;
      *) sleep 0.1 ;;
    esac
  done
  if [ $n -gt 0 ]; then r PASS "groundpics_$name" "$n pictures"; else r FAIL "groundpics_$name" "no ground arrow in 60 s"; fi
}
shieldon() {   # LABEL: p3_pics shieldon into $CAND9_PICS/p3_shield_LABEL
  local out
  out=$(LINES_OUT=$CAND9_PICS/p3_shield_$1 timeout 300 python3 scripts/p3_pics.py shieldon 2>&1)
  echo "$out" | grep -E 'shieldon' | cut -c1-240
  if echo "$out" | grep -q 'on the circle at try'; then r PASS "shieldon_$1" "$(ls "$CAND9_PICS/p3_shield_$1" 2>/dev/null | tr '\n' ' ')"
  else r FAIL "shieldon_$1" "the shield never judged on its circle"; fi
}

case "${1:-}" in
  shield)
    fight shield --jump EL_Ambush_Empire --with "EmpireSoldierREF*1" --equip $GEAR || exit 1
    c "fighthud timer 90" > /dev/null
    combo board_shell
    walk f_shield_board_shell
    shieldon board_shell
    combo body_rings
    walk f_shield_body_rings
    p strike f_shield_body_rings_attack 25
    # the shield hand turned like a watch: the wrist's timer band and the health gems
    sim "hand l -0.02 1.42 -0.28 -70 -10 -90"; sleep 0.8; p shot f_shield_body_rings_wrist
    sim "$GUARD"
    shieldon body_rings
    c "fighthud timer 0" > /dev/null; combo board_shell; c "tough off" > /dev/null
    ;;
  archers)
    fight archers --fixture archer --equip $GEAR || exit 1
    walk f_archers
    groundpics f_archers_ground_arrow
    c "shots?" 4 | cut -c1-300
    c "tough off" > /dev/null
    ;;
  lobs_red)
    fight lobs_red --fixture lobs --equip HugeHammer || exit 1
    walk f_lobs_red
    out=$(LINES_OUT=$CAND9_PICS/p3_lobs_red timeout 200 python3 scripts/p3_pics.py ground 2>&1)
    if ls "$CAND9_PICS/p3_lobs_red/"*.jpg > /dev/null 2>&1; then r PASS ground_red "$(ls "$CAND9_PICS/p3_lobs_red" | tr '\n' ' ')"
    else r FAIL ground_red "no picture: $(echo "$out" | tail -1 | cut -c1-160)"; fi
    c "tough off" > /dev/null
    ;;
  lobs_gold)
    fight lobs_gold --fixture lobs --equip $GEAR || exit 1
    groundpics f_lobs_gold_ground_arrow
    c "tough off" > /dev/null
    ;;
  night)
    echo "== night ($(date +%T))"
    timeout 600 python3 scripts/hud_pics.py night --place "night arena, skeletons" --jump EL_Ambush_Empire \
      --with "SkeletonNEWREF*2,SkeletonKnightREF*1" --env Night --parts board,body,shell,rings,near > "$FPO/fightpics_night_hud.out" 2>&1
    grep -E 'fixture:|envstate:|no fight|error' "$FPO/fightpics_night_hud.out" | cut -c1-200
    echo "hud_pics: $(grep -c 'saved' "$FPO/fightpics_night_hud.out") pictures saved"
    case "$(c 'state?' 3)" in *Level_Play*) r PASS fightpics_night "$(c 'envstate' 5 | cut -c1-160)" ;; *) r FAIL fightpics_night "no fight: $(c 'state?' 3)"; exit 1 ;; esac
    c "god on" > /dev/null; c "tough on" > /dev/null; c "vrlook gilded" > /dev/null
    combo board_shell
    walk f_night_board_shell
    c "fighthud timer 90" > /dev/null
    combo body_rings
    walk f_night_body_rings
    p strike f_night_body_rings_attack 25
    c "fighthud timer 0" > /dev/null; combo board_shell; c "tough off" > /dev/null
    ;;
esac
echo "== done $1 ($(date +%T))"
