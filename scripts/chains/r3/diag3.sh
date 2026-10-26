#!/usr/bin/env bash
# env 2 round 3 diagnostics 3: the on-deck waiter stops at once (Standby.StopPrefix)
set -u
LOG=/home/bara/Projects/Flat2VR-env2/HandOfFate2/logs/r3diag3.log
O=/home/bara/Projects/Flat2VR-env2/HandOfFate2/logs/r3diag3
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"
begin
ensure_table; t "B1 thieves waiting x3, strafe" "$O/B1.out" python3 scripts/audit_test.py --tag R3Q1 --fixture thieves --equip $GEAR --stay --parts waiting,waiting,waiting,strafe
if state | grep -q Level_Play; then t "P1 probe (same fight)" "$O/P1p.out" python3 "$R3/waitprobe.py" 45; fi
savelog
ensure_table; t "B2 thieves waiting x2, dash, strafe" "$O/B2.out" python3 scripts/audit_test.py --tag R3Q2 --fixture thieves --equip $GEAR --stay --parts waiting,waiting,dash,strafe
finish
