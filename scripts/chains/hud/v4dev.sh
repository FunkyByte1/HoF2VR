#!/usr/bin/env bash
# env 3 (2026-10-19, the HUD audit fixes): a dev session - Monado, the table (this build installed), the game's shield cards
# listed (`weapons` -> the log), then a fight with one Empire soldier (EL_Ambush_Empire), god, Physical, the gear (GEAR4,
# default the sword and BasicShield). LEFT=1: left-handed for this run (`cfg LeftHanded true`, not saved) before the fight.
# Run unsandboxed: NAME=hud4dev nohup setsid bash scripts/chains/hud/v4dev.sh > /dev/null 2>&1 &
set -u
NAME=${NAME:-hud4dev}
LOG=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env3/HandOfFate2/scripts/chains/hud/lib.sh
mkdir -p "$O"
say "dev start: DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8)"
guard
scripts/monado.sh start >> "$O/monado.out" 2>&1
table
python3 scripts/cmd.py "weapons" >> "$O/weapons.out" 2>&1
[ "${LEFT:-0}" = 1 ] && python3 scripts/cmd.py "cfg LeftHanded true" >> "$O/fixture.out" 2>&1
python3 scripts/combat_fixture.py --jump EL_Ambush_Empire --with "${WITH:-EmpireSoldierREF*1}" --god --physical --equip "${GEAR4:-$GEAR}" >> "$O/fixture.out" 2>&1
say "fight: $(state | cut -c1-60)"
