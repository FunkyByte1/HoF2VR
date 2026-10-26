#!/usr/bin/env bash
# env 2, playtest 3 fixes + the audit of 2026-10-18 (a copy of chain_p3.sh with the audit's blocks: never edit a running
# chain): p3_test's parts in their fixtures (M R S T L, Q = postbreak and pause, V = leanarrow), then chain_pf's fight blocks
# and focus_test, on one DLL (build/bin). NAME defaults to p3h. NORESERVE=1: fair turns.
# Run unsandboxed: NORESERVE=1 nohup setsid bash scripts/chains/p3/chain_p3b.sh > /dev/null 2>&1 &
set -u
NAME=${NAME:-p3h}
LOG=/home/bara/Projects/Flat2VR-env2/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env2/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env2/HandOfFate2/scripts/chains/r3/lib.sh
ROUNDS=${ROUNDS:-1}
P3=${P3:-"M S R T"}
BLOCKS=${BLOCKS:-"A B C D F G P N"}
begin
for b in $P3; do
  case $b in
    M) ensure_table; t "p3 M mix: tiers,refill,damage,reverse,lined,glow,stick,shield" "$O/p3M.out" python3 scripts/p3_test.py --tag P3M --fixture mix --equip $GEAR --stay --parts tiers,refill,damage,reverse,lined,glow,stick,shield ;;
    S) ensure_table; t "p3 S soldiers: mirror,ring" "$O/p3S.out" python3 scripts/p3_test.py --tag P3S --fixture shield --equip $GEAR --stay --parts mirror,ring ;;
    R) ensure_table; t "p3 R thieves: ring" "$O/p3R.out" python3 scripts/p3_test.py --tag P3R --fixture thieves --equip $GEAR --stay --parts ring ;;
    Q) ensure_table; t "p3 Q thieves: postbreak,pause" "$O/p3Q.out" python3 scripts/p3_test.py --tag P3Q --fixture thieves --equip $GEAR --stay --parts postbreak,pause ;;
    V) ensure_table; t "p3 V dodge fixture: leanarrow" "$O/p3V.out" python3 scripts/p3_test.py --tag P3V --fixture dodge2 --equip $GEAR --stay --parts leanarrow ;;
    L) ensure_table; t "p3 L lobs with a shield: groundgold" "$O/p3L.out" python3 scripts/p3_test.py --tag P3L --fixture lobs --equip $GEAR --stay --parts groundgold ;;
    T) ensure_table; t "p3 T tutorial skeletons + throwers, no shield: pips,ground" "$O/p3T.out" python3 scripts/p3_test.py --tag P3T --fixture tut --equip HugeHammer --stay --parts pips,ground
       leave; guard; yield_slot; table ;;   # a fresh session: the snapshot's own gear again (no two-handed hammer)
  esac
done
savelog
for i in $(seq 1 "$ROUNDS"); do
  for b in $BLOCKS; do
    case $b in
      A) ensure_table; t "r$i A shadows leadhold,combogaps" "$O/A$i.out" python3 scripts/audit_test.py --tag P3${i}A --fixture shadows --equip $GEAR --stay --parts leadhold,combogaps ;;
      B) ensure_table; t "r$i B thieves waiting,waiting,dash,strafe,health" "$O/B$i.out" python3 scripts/audit_test.py --tag P3${i}B --fixture thieves --equip $GEAR --stay --parts waiting,waiting,dash,strafe,health ;;
      C) ensure_table; t "r$i C thieves stage1 block x3" "$O/C$i.out" python3 scripts/stage1_test.py --tag P3${i}C --fixture thieves --equip $GEAR --stay --parts block,block,block
         if state | grep -q Level_Play; then t "r$i C move_test (same fight)" "$O/M$i.out" python3 scripts/move_test.py --tag P3${i}M --no-fixture; else say "r$i move_test: the fight is over"; fi ;;
      D) ensure_table; t "r$i D combo, interruptcut (combo fixture)" "$O/D$i.out" python3 scripts/stage2_test.py --tag P3${i}D --fixture combo --equip $GEAR --stay --parts combo,interruptcut ;;
      F) ensure_table; t "r$i F thieves linecut,crit,wrong,stickuse" "$O/F$i.out" python3 scripts/stage2_test.py --tag P3${i}F --fixture thieves --equip $GEAR --stay --parts linecut,crit,wrong,stickuse ;;
      G) ensure_table; t "r$i G dodge fixture redbar,redlean" "$O/G$i.out" python3 scripts/stage1_test.py --tag P3${i}G --fixture dodge --equip $GEAR --stay --parts redbar,redlean ;;
      P) ensure_table; t "r$i P lobs knock,drop,dodge,offview" "$O/P$i.out" python3 scripts/bomb_test.py --tag P3${i}P --stay --parts knock,drop,dodge,offview ;;
      N) ensure_table; t "r$i N gnomes watch,cut,dash,cling" "$O/N$i.out" python3 scripts/gnome_test.py --tag P3${i}N --stay --parts watch,cut,dash,cling ;;
    esac
  done
  savelog
done
if [ "${FOCUS:-1}" = 1 ]; then guard; yield_slot; savelog; t "E focus_test (autoplay's walk from a fresh table)" "$O/E.out" python3 scripts/focus_test.py; fi
finish
