#!/usr/bin/env bash
# env 3 (2026-10-19): after chain hudr2 - the shell's steps from one frozen moment (hud_pics force, snow arena), hud_test's
# shell and rings again (641aa95: the blade down), move_test once more (hudr2's dash_window: no attack in its 6 s window).
# Run unsandboxed: NAME=hudr3 nohup setsid bash scripts/chains/hud/finish.sh > /dev/null 2>&1 &
set -u
NAME=${NAME:-hudr3}
LOG=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env3/HandOfFate2/scripts/chains/hud/lib.sh
export HUD_PICS=${HUD_PICS:-/home/bara/Projects/flat2vr/HandOfFate2/captures/hud-built-2026-10-18/v2}
begin
ensure_table; t "pics force: the shell's steps, one frozen moment" "$O/force.out" python3 scripts/hud_pics.py bright --place "snow arena, soldiers" --jump EL_Ambush_Thral --with "EmpireSoldierREF*2,ThievesThugREF*2" --parts force
ensure_table; t "hud thieves: shell,rings (the blade down)" "$O/hud.out" python3 scripts/hud_test.py --tag HUDS --fixture thieves --equip $GEAR --stay --parts shell,rings
if state | grep -q Level_Play; then t "move_test (same fight)" "$O/M.out" python3 scripts/move_test.py --tag HFM --no-fixture; fi
finish
