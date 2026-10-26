#!/usr/bin/env bash
# env 3 (2026-10-19, the HUD audit's item 2): the HUD's per-method allocations (alloc.py) on this build, then on 89ee74e3
# (logs/HoF2VR-89ee74e3.dll), each from a fresh table. Run unsandboxed: NAME=hudalloc nohup setsid bash scripts/chains/hud/alloc.sh > /dev/null 2>&1 &
set -u
NAME=${NAME:-hudalloc}
LOG=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env3/HandOfFate2/scripts/chains/hud/lib.sh
begin
t "alloc: this build" "$O/alloc.out" python3 scripts/chains/hud/alloc.py
OLD=$H/logs/HoF2VR-89ee74e3.dll
guard; yield_slot; savelog; scripts/stop.sh > /dev/null 2>&1
cp "$OLD" "$G3/BepInEx/plugins/HoF2VR/HoF2VR.dll"
python3 scripts/session.py start --snapshot magician_mid --to table --no-install >> "$O/sessions.out" 2>&1
SLOT_SINCE=$(date +%s)
say "89ee74e3 at the table: $(sha1sum < "$G3/BepInEx/plugins/HoF2VR/HoF2VR.dll" | cut -c1-8)"
t "alloc: 89ee74e3" "$O/alloc_old.out" python3 scripts/chains/hud/alloc.py
savelog; scripts/stop.sh > /dev/null 2>&1
bash scripts/install.sh >> "$O/install.out" 2>&1   # a test install into env 3's game (never --release)
say "this build reinstalled: $(sha1sum < "$G3/BepInEx/plugins/HoF2VR/HoF2VR.dll" | cut -c1-8)"
finish
