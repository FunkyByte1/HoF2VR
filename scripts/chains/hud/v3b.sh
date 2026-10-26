#!/usr/bin/env bash
# env 3 (2026-10-19, v3): after hudv3b - the final build's hud_test (18) and the shield picture again (p3_pics shieldon: also
# the shield as low as still counts on the circle). Run unsandboxed: NAME=hudv3c nohup setsid bash scripts/chains/hud/v3b.sh > /dev/null 2>&1 &
set -u
NAME=${NAME:-hudv3c}
LOG=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env3/HandOfFate2/scripts/chains/hud/lib.sh
export HUD_PICS=${HUD_PICS:-/home/bara/Projects/flat2vr/HandOfFate2/captures/hud-built-2026-10-18/v3}
begin
ensure_table; t "hud thieves: flat,board,body,shell,rings,switch" "$O/hud.out" python3 scripts/hud_test.py --tag HUDW --fixture thieves --equip $GEAR --stay --parts flat,board,body,shell,rings,switch
savelog
ensure_table
python3 scripts/combat_fixture.py --jump EL_Ambush_Empire --with "EmpireSoldierREF*1" --god --physical --equip $GEAR >> "$O/shield.out" 2>&1
t "p3 pics shieldon (scripted)" "$O/shield.out2" env LINES_OUT=$HUD_PICS/p3 python3 scripts/p3_pics.py shieldon
savelog
# the perf once more on the final DLL (hudv3b: p99 33 ms in this build's session, Rings too - compositor waits, not logic/render)
ensure_table; t "perf big: final build (shell, worn, rings)" "$O/perf.out" bash scripts/chains/hud/perf.sh hud
finish
