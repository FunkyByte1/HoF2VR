#!/usr/bin/env bash
# env 3, playtest 4 (P4-9 crit arrows, P4-7/8 gnomes; NOTES "Playtest 4 crit and gnome fixes (env 3, 2026-10-19)"): one turn
# of the shared slot (the coordinator's rule 2026-10-20 00:04: at most 25 minutes, then release and let env 1 and env 2 go).
#   chain.sh BLOCK...    run these blocks under one reservation, then release it (game, Monado, reserved-env3)
# Blocks:
#   T     p4e3_test: crit, angles (skeletons), posts, ground, cling (gnomes), with both-eye pictures -> captures/p4e3/
#   N     gnome_test watch,cut,dash,cling (the cand10 chain's N)
#   F D   stage2_test linecut,crit,wrong,stickuse (thieves); combo,interruptcut (combo fixture)
#   A B C G P   audit_test leadhold,combogaps (shadows); waiting x2,dash,strafe,health (thieves); stage1 block x3 + move_test;
#         stage1 redbar,redlean (dodge); bomb_test knock,drop,dodge,offview
#   P3    p3_test M S R Q V L T (the cand10 chain's B_p3)
# Run unsandboxed: NAME=p4e3c1 nohup setsid bash scripts/chains/p4e3/chain.sh T N F D > /dev/null 2>&1 &
set -u
NAME=${NAME:-p4e3c}
LOG=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env3/HandOfFate2/scripts/chains/hud/lib.sh
export NORESERVE=0
# wait for the slot: no other environment's fresh reservation, the user out of VR
while :; do
  busy=""
  for f in /run/user/1000/vr-session/reserved-env*; do
    [ -e "$f" ] || continue
    case "$f" in *reserved-env3) continue ;; esac
    [ $(( $(date +%s) - $(stat -c %Y "$f") )) -lt 120 ] && busy="$busy ${f##*/}"
  done
  if [ -z "$busy" ] && ! userin; then break; fi
  sleep 30
done
begin
X="--equip $GEAR --stay"
for b in "$@"; do
  guard
  case $b in
    T) ensure_table; t "T p4e3_test crit,angles,posts,ground,cling (pictures)" "$O/T.out" python3 scripts/p4e3_test.py --tag P4E3 --stay --pics --parts crit,angles,posts,ground,cling ;;
    T2) ensure_table; t "T2 p4e3_test crit,angles,cling (pictures)" "$O/T2.out" python3 scripts/p4e3_test.py --tag P4E3b --stay --pics --parts crit,angles,cling ;;
    N) ensure_table; t "N gnomes watch,cut,dash,cling" "$O/N.out" python3 scripts/gnome_test.py --tag P4N --stay --parts watch,cut,dash,cling ;;
    F) ensure_table; t "F thieves linecut,crit,wrong,stickuse" "$O/F.out" python3 scripts/stage2_test.py --tag P4F --fixture thieves $X --parts linecut,crit,wrong,stickuse ;;
    D) ensure_table; t "D combo, interruptcut (combo fixture)" "$O/D.out" python3 scripts/stage2_test.py --tag P4D --fixture combo $X --parts combo,interruptcut ;;
    A) ensure_table; t "A shadows leadhold,combogaps" "$O/A.out" python3 scripts/audit_test.py --tag P4A --fixture shadows $X --parts leadhold,combogaps ;;
    B) ensure_table; t "B thieves waiting,waiting,dash,strafe,health" "$O/B.out" python3 scripts/audit_test.py --tag P4B --fixture thieves $X --parts waiting,waiting,dash,strafe,health ;;
    C) ensure_table; t "C thieves stage1 block x3" "$O/C.out" python3 scripts/stage1_test.py --tag P4C --fixture thieves $X --parts block,block,block
       if state | grep -q Level_Play; then t "C move_test (same fight)" "$O/M.out" python3 scripts/move_test.py --tag P4M --no-fixture; fi ;;
    G) ensure_table; t "G dodge fixture redbar,redlean" "$O/G.out" python3 scripts/stage1_test.py --tag P4G --fixture dodge $X --parts redbar,redlean ;;
    P) ensure_table; t "P lobs knock,drop,dodge,offview" "$O/P.out" python3 scripts/bomb_test.py --tag P4P --stay --parts knock,drop,dodge,offview ;;
    P3) ensure_table; t "p3 M mix" "$O/p3M.out" python3 scripts/p3_test.py --tag P4p3M --fixture mix $X --parts tiers,refill,damage,reverse,lined,glow,stick,shield
        ensure_table; t "p3 S soldiers: mirror,ring" "$O/p3S.out" python3 scripts/p3_test.py --tag P4p3S --fixture shield $X --parts mirror,ring
        ensure_table; t "p3 R thieves: ring" "$O/p3R.out" python3 scripts/p3_test.py --tag P4p3R --fixture thieves $X --parts ring
        ensure_table; t "p3 Q thieves: postbreak,pause" "$O/p3Q.out" python3 scripts/p3_test.py --tag P4p3Q --fixture thieves $X --parts postbreak,pause
        ensure_table; t "p3 V dodge2: leanarrow" "$O/p3V.out" python3 scripts/p3_test.py --tag P4p3V --fixture dodge2 $X --parts leanarrow
        ensure_table; t "p3 L lobs: groundgold" "$O/p3L.out" python3 scripts/p3_test.py --tag P4p3L --fixture lobs $X --parts groundgold
        ensure_table; t "p3 T tutorial: pips,ground" "$O/p3T.out" python3 scripts/p3_test.py --tag P4p3T --fixture tut --equip HugeHammer --stay --parts pips,ground ;;
    *) say "unknown block $b" ;;
  esac
  say "  exceptions in the game log so far: $(grep -c Exception "$G3/BepInEx/LogOutput.log")"
done
finish
scripts/reserve.sh stop >> "$O/reserve.out" 2>&1
say "slot released"
