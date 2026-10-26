#!/usr/bin/env bash
# env 2 round 3 diagnostics 2: what stops an on-deck waiter short of its band (the stop probe, DLL with StopPrefix)
set -u
LOG=/home/bara/Projects/Flat2VR-env2/HandOfFate2/logs/r3diag2.log
O=/home/bara/Projects/Flat2VR-env2/HandOfFate2/logs/r3diag2
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"
begin
ensure_table; t "P1 thieves waiting probe 60 s" "$O/P1.out" python3 scripts/audit_test.py --tag R3P1 --fixture thieves --equip $GEAR --stay --parts waiting
if state | grep -q Level_Play; then t "P1 probe (same fight)" "$O/P1p.out" python3 "$R3/waitprobe.py" 60; fi
if state | grep -q Level_Play; then t "P2 probe (same fight)" "$O/P2p.out" python3 "$R3/waitprobe.py" 60; fi
finish
