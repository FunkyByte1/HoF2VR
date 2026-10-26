#!/usr/bin/env bash
# env 3 dev session: Monado, the table, a fight (FIX: a combat_fixture name, default thieves), god mode, Physical, the gear.
# Run unsandboxed: nohup setsid bash scripts/chains/hud/dev.sh > /dev/null 2>&1 &
set -u
NAME=${NAME:-hudev}
LOG=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env3/HandOfFate2/scripts/chains/hud/lib.sh
mkdir -p "$O"
say "dev start: DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8)"
guard
scripts/monado.sh start >> "$O/monado.out" 2>&1
table
FIX=${FIX:-thieves}
python3 scripts/combat_fixture.py --fixture "$FIX" --god --physical --equip "$GEAR" >> "$O/fixture.out" 2>&1
say "fight: $(state | cut -c1-60)"
