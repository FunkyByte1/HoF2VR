#!/usr/bin/env bash
# env 3 (2026-10-19, v4): after hudv4f - the shell-recovering picture again (the hero placed while the enemy stands stunned,
# the view kept on it), the perf once more (hudv4f's session of this build paced p99 33 ms in every mode - compositor
# waits - as hudv4's session of 89ee74e3 had), and the sheet. Run unsandboxed: NAME=hudv4c nohup setsid bash scripts/chains/hud/v4c.sh > /dev/null 2>&1 &
set -u
NAME=${NAME:-hudv4c}
LOG=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env3/HandOfFate2/scripts/chains/hud/lib.sh
export HUD_PICS=${HUD_PICS:-/home/bara/Projects/flat2vr/HandOfFate2/captures/hud-built-2026-10-18/v4}
begin
ensure_table; t "pics v4: recover (snow, soldiers + thugs)" "$O/pics.out" python3 scripts/hud_pics.py v4 --place "snow arena, soldiers" --jump EL_Ambush_Thral --with "EmpireSoldierREF*2,ThievesThugREF*2" --parts recover
savelog
ensure_table; t "perf big: this build (shell, worn, rings)" "$O/perf.out" bash scripts/chains/hud/perf.sh hud
savelog
SHEET_TITLE="Hand of Fate 2 VR - the HUD audit fixes and the shield's frame ring (v4, 2026-10-19), both eyes" python3 scripts/hud_pics.py sheet >> "$O/sheet.out" 2>&1
say "sheet: $(tail -1 "$O/sheet.out")"
finish
