#!/usr/bin/env bash
# env 2 round 3 diagnostics 1: the strafe clock (UYF's), waiting's too-far tally, the dash's floor check, interruptcut's re-cut
set -u
LOG=/home/bara/Projects/Flat2VR-env2/HandOfFate2/logs/r3diag1.log
O=/home/bara/Projects/Flat2VR-env2/HandOfFate2/logs/r3diag1
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"
begin
for i in 1 2; do
  ensure_table; t "d$i B thieves waiting,waiting,dash,strafe" "$O/B$i.out" python3 scripts/audit_test.py --tag R3D${i}B --fixture thieves --equip $GEAR --stay --parts waiting,waiting,dash,strafe
done
ensure_table; t "d1 D combo, interruptcut (combo fixture)" "$O/D1.out" python3 scripts/stage2_test.py --tag R3D1D --fixture combo --equip $GEAR --stay --parts combo,interruptcut
finish
