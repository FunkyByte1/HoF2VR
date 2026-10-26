#!/usr/bin/env bash
# env 3 (2026-10-19, v4): after hudv4 - hud_test's shell part again (hands behind the body during the re-form wait; the
# recover check reads the first re-forming), then the On you combo and the shell-recovering pictures again (the wrist framed
# in view; the recovering enemy re-placed and aimed at) into v4/, and the sheet.
# Run unsandboxed: NAME=hudv4b nohup setsid bash scripts/chains/hud/v4b.sh > /dev/null 2>&1 &
set -u
NAME=${NAME:-hudv4b}
LOG=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env3/HandOfFate2/scripts/chains/hud/lib.sh
export HUD_PICS=${HUD_PICS:-/home/bara/Projects/flat2vr/HandOfFate2/captures/hud-built-2026-10-18/v4}
begin
ensure_table; t "hud thieves: shell, shell, dead" "$O/hud.out" python3 scripts/hud_test.py --tag HUD4B --fixture thieves --equip $GEAR --stay --parts shell,shell,dead
savelog
ensure_table; t "pics v4: bodycombo, recover (snow, soldiers + thugs)" "$O/pics.out" python3 scripts/hud_pics.py v4 --place "snow arena, soldiers" --jump EL_Ambush_Thral --with "EmpireSoldierREF*2,ThievesThugREF*2" --parts bodycombo,recover
savelog
SHEET_TITLE="Hand of Fate 2 VR - the HUD audit fixes and the shield's frame ring (v4, 2026-10-19), both eyes" python3 scripts/hud_pics.py sheet >> "$O/sheet.out" 2>&1
say "sheet: $(tail -1 "$O/sheet.out")"
finish
