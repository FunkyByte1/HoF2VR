#!/usr/bin/env bash
# env 2, test reliability 2026-10-17 round 3: every item 5 times on one DLL (logs/$NAME/, logs/$NAME.log; NAME defaults to
# rel17r3b - the first run, logs/rel17r3, was stopped after its round 3 for the user's playtest).
# Run unsandboxed: nohup setsid bash scripts/chains/r3/chain_r3.sh > /dev/null 2>&1 &
# To stop it: kill its bash and the running test's python by PID (bash runs its trap only once the test returns), then
# scripts/stop.sh and scripts/monado.sh stop. It waits by itself while the user is in VR.
set -u
NAME=${NAME:-rel17r3b}
LOG=/home/bara/Projects/Flat2VR-env2/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env2/HandOfFate2/logs/$NAME
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"
ROUNDS=${ROUNDS:-5}
BLOCKS=${BLOCKS:-"A B C D"}
begin
for i in $(seq 1 "$ROUNDS"); do
  for b in $BLOCKS; do
    case $b in
      A) ensure_table; t "r$i A shadows leadhold,combogaps" "$O/A$i.out" python3 scripts/audit_test.py --tag R3${i}A --fixture shadows --equip $GEAR --stay --parts leadhold,combogaps ;;
      B) ensure_table; t "r$i B thieves waiting,waiting,dash,strafe" "$O/B$i.out" python3 scripts/audit_test.py --tag R3${i}B --fixture thieves --equip $GEAR --stay --parts waiting,waiting,dash,strafe ;;
      C) ensure_table; t "r$i C thieves stage1 block x3" "$O/C$i.out" python3 scripts/stage1_test.py --tag R3${i}C --fixture thieves --equip $GEAR --stay --parts block,block,block
         if state | grep -q Level_Play; then t "r$i C move_test (same fight)" "$O/M$i.out" python3 scripts/move_test.py --tag R3${i}M --no-fixture; else say "r$i move_test: the fight is over"; fi ;;
      D) ensure_table; t "r$i D combo, interruptcut (combo fixture)" "$O/D$i.out" python3 scripts/stage2_test.py --tag R3${i}D --fixture combo --equip $GEAR --stay --parts combo,interruptcut ;;
    esac
  done
  savelog
done
# item 7: autoplay's walk to a fight from a fresh session (focus_test starts its own)
if [ "${FOCUS:-1}" = 1 ]; then guard; savelog; t "E focus_test (autoplay's walk from a fresh table)" "$O/E.out" python3 scripts/focus_test.py; fi
finish
