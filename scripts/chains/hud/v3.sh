#!/usr/bin/env bash
# env 3 (2026-10-19, the v3 review): the shell near the player and its low steps. Pictures -> captures/hud-built-2026-10-18/v3/
# (near before/after at 1, 2, 4 m and the forced steps, snow arena; a night arena via `envstate Night`; the shield placed on
# its circle by script), then hud_test (18), p3 M and S, and the perf (the big fixture) on this build and on bd1946c's DLL
# (logs/HoF2VR-374cc5ee.dll). Pick: PICS=0/1 NIGHT=0/1 SHIELD=0/1 TESTS=0/1 PERF=0/1 NIGHT_JUMP NIGHT_ENV.
# Run unsandboxed: NAME=hudv3 nohup setsid bash scripts/chains/hud/v3.sh > /dev/null 2>&1 &
set -u
NAME=${NAME:-hudv3}
LOG=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env3/HandOfFate2/scripts/chains/hud/lib.sh
export HUD_PICS=${HUD_PICS:-/home/bara/Projects/flat2vr/HandOfFate2/captures/hud-built-2026-10-18/v3}
mkdir -p "$HUD_PICS/p3"
begin
if [ "${PICS:-1}" = 1 ]; then
  ensure_table; t "pics near + force (snow, soldiers + thugs)" "$O/near.out" python3 scripts/hud_pics.py bright --place "snow arena, soldiers" --jump EL_Ambush_Thral --with "EmpireSoldierREF*2,ThievesThugREF*2" --parts near,force
  savelog
fi
if [ "${NIGHT:-1}" = 1 ]; then
  ensure_table; t "pics night (${NIGHT_JUMP:-EL_Ambush_Empire}, envstate ${NIGHT_ENV:-Night})" "$O/night.out" python3 scripts/hud_pics.py night --place "night arena, skeletons" --jump "${NIGHT_JUMP:-EL_Ambush_Empire}" --with "SkeletonNEWREF*2,SkeletonKnightREF*1" --env "${NIGHT_ENV:-Night}" --parts near,rings,shell,board
  savelog
fi
if [ "${SHIELD:-1}" = 1 ]; then
  ensure_table
  # one soldier: every blow is the one in view (v3's second run: the other soldier struck off-view)
  python3 scripts/combat_fixture.py --jump EL_Ambush_Empire --with "EmpireSoldierREF*1" --god --physical --equip $GEAR >> "$O/shield.out" 2>&1
  t "p3 pics shieldon (scripted)" "$O/shield.out2" env LINES_OUT=$HUD_PICS/p3 python3 scripts/p3_pics.py shieldon
  savelog
fi
if [ "${PICS:-1}" = 1 ] || [ "${NIGHT:-1}" = 1 ]; then python3 scripts/hud_pics.py sheet >> "$O/sheet.out" 2>&1; say "sheet: $(tail -1 "$O/sheet.out")"; fi
if [ "${TESTS:-1}" = 1 ]; then
  ensure_table; t "hud thieves: flat,board,body,shell,rings,switch" "$O/hud.out" python3 scripts/hud_test.py --tag HUDV --fixture thieves --equip $GEAR --stay --parts flat,board,body,shell,rings,switch
  savelog
  ensure_table; t "p3 M mix: tiers,refill,damage,reverse,lined,glow,stick,shield" "$O/p3M.out" python3 scripts/p3_test.py --tag V3M --fixture mix --equip $GEAR --stay --parts tiers,refill,damage,reverse,lined,glow,stick,shield
  ensure_table; t "p3 S soldiers: mirror,ring" "$O/p3S.out" python3 scripts/p3_test.py --tag V3S --fixture shield --equip $GEAR --stay --parts mirror,ring
  savelog
fi
if [ "${PERF:-1}" = 1 ]; then
  ensure_table; t "perf big: this build (shell, worn, rings)" "$O/perf.out" bash scripts/chains/hud/perf.sh hud
  OLD=$H/logs/HoF2VR-374cc5ee.dll
  if [ -f "$OLD" ]; then
    guard; yield_slot; savelog; scripts/stop.sh > /dev/null 2>&1
    cp "$OLD" "$G3/BepInEx/plugins/HoF2VR/HoF2VR.dll"
    python3 scripts/session.py start --snapshot magician_mid --to table --no-install >> "$O/sessions.out" 2>&1
    SLOT_SINCE=$(date +%s)
    say "bd1946c's DLL at the table: $(sha1sum < "$G3/BepInEx/plugins/HoF2VR/HoF2VR.dll" | cut -c1-8)"
    t "perf big: bd1946c's DLL (shell)" "$O/perf_old.out" bash scripts/chains/hud/perf.sh bd1946c
    savelog; scripts/stop.sh > /dev/null 2>&1
    bash scripts/install.sh >> "$O/install.out" 2>&1   # a test install into env 3's game (never --release)
    say "this build reinstalled: $(sha1sum < "$G3/BepInEx/plugins/HoF2VR/HoF2VR.dll" | cut -c1-8)"
  fi
fi
finish
