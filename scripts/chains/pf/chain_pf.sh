#!/usr/bin/env bash
# env 2, the playtest fixes of 2026-10-17: round 3's chain (scripts/chains/r3/chain_r3.sh) adapted to the user's rulings,
# every test 5 times on one DLL (logs/$NAME/, logs/$NAME.log; NAME defaults to pf17b - pf17 was stopped by the 12:04 GPU fault).
#  A shadows leadhold, combogaps            B thieves waiting x2, dash, strafe, health (EnemyHealth 0.15)
#  C thieves stage1 block x3 + move_test    D combo fixture combo, interruptcut
#  F thieves stage2 linecut, crit, wrong, stickuse (F3: a cut through the line at any depth, the 50 deg leeway; F4)
#  G dodge fixture stage1 redbar, redlean (F5: red blows blocked on a bar, no arrows, no lean dodge)
#  N a gnome swarm: gnome_test watch, cut, dash, cling (F8: the player's gnome design)
#  P lobs fixture bomb_test knock, drop, dodge, offview (P2-10, P2-11: bombs knocked away, shots warned off view)
# Run unsandboxed: nohup setsid bash scripts/chains/pf/chain_pf.sh > /dev/null 2>&1 &
# To stop it: TERM its bash and kill the running test's python by PID, then scripts/stop.sh and scripts/monado.sh stop.
set -u
NAME=${NAME:-pf17b}
LOG=/home/bara/Projects/Flat2VR-env2/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env2/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env2/HandOfFate2/scripts/chains/r3/lib.sh
ROUNDS=${ROUNDS:-5}
BLOCKS=${BLOCKS:-"A B C D F G N"}
begin
# once: N2's edge turn and N3's swarm boss (scripts/chains/pf/pre.sh)
if [ "${PRE:-1}" = 1 ]; then t "P N2 edge turn, N3 gnome swarm" "$O/P.out" bash scripts/chains/pf/pre.sh; fi
for i in $(seq 1 "$ROUNDS"); do
  for b in $BLOCKS; do
    case $b in
      A) ensure_table; t "r$i A shadows leadhold,combogaps" "$O/A$i.out" python3 scripts/audit_test.py --tag PF${i}A --fixture shadows --equip $GEAR --stay --parts leadhold,combogaps ;;
      B) ensure_table; t "r$i B thieves waiting,waiting,dash,strafe,health" "$O/B$i.out" python3 scripts/audit_test.py --tag PF${i}B --fixture thieves --equip $GEAR --stay --parts waiting,waiting,dash,strafe,health ;;
      C) ensure_table; t "r$i C thieves stage1 block x3" "$O/C$i.out" python3 scripts/stage1_test.py --tag PF${i}C --fixture thieves --equip $GEAR --stay --parts block,block,block
         if state | grep -q Level_Play; then t "r$i C move_test (same fight)" "$O/M$i.out" python3 scripts/move_test.py --tag PF${i}M --no-fixture; else say "r$i move_test: the fight is over"; fi ;;
      D) ensure_table; t "r$i D combo, interruptcut (combo fixture)" "$O/D$i.out" python3 scripts/stage2_test.py --tag PF${i}D --fixture combo --equip $GEAR --stay --parts combo,interruptcut ;;
      F) ensure_table; t "r$i F thieves linecut,crit,wrong,stickuse" "$O/F$i.out" python3 scripts/stage2_test.py --tag PF${i}F --fixture thieves --equip $GEAR --stay --parts linecut,crit,wrong,stickuse ;;
      N) ensure_table; t "r$i N gnomes watch,cut,dash,cling" "$O/N$i.out" python3 scripts/gnome_test.py --tag PF${i}N --stay --parts watch,cut,dash,cling ;;
      G) ensure_table; t "r$i G dodge fixture redbar,redlean" "$O/G$i.out" python3 scripts/stage1_test.py --tag PF${i}G --fixture dodge --equip $GEAR --stay --parts redbar,redlean ;;
      P) ensure_table; t "r$i P lobs knock,drop,dodge,offview" "$O/P$i.out" python3 scripts/bomb_test.py --tag PF${i}P --stay --parts knock,drop,dodge,offview ;;
    esac
  done
  savelog
done
if [ "${FOCUS:-1}" = 1 ]; then guard; yield_slot; savelog; t "E focus_test (autoplay's walk from a fresh table)" "$O/E.out" python3 scripts/focus_test.py; fi
finish
