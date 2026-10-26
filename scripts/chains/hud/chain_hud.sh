#!/usr/bin/env bash
# env 3, the fight HUD and enemy status (2026-10-18): hud_test.py's parts, the pictures for the user (hud_pics.py: a bright arena
# with soldiers, a night arena with skeletons), then the regression: p3_test's parts in their fixtures and chain_pf's fight
# blocks (A B C D F G P N) and focus_test, as env 2's scripts/chains/p3/chain_p3.sh runs them, on one DLL (build/bin).
# NAME defaults to hudc; logs/$NAME.log, logs/$NAME/. NORESERVE=1 (default): fair turns, yielding between blocks.
# Pick parts: HUD=0/1 PICS=0/1 PERF=0/1 P3PICS=0/1 P3="M S R T" BLOCKS="A B C D F G P N" FOCUS=0/1 ROUNDS=1.
# Run unsandboxed: nohup setsid bash scripts/chains/hud/chain_hud.sh > /dev/null 2>&1 &
set -u
NAME=${NAME:-hudc}
LOG=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env3/HandOfFate2/scripts/chains/hud/lib.sh
ROUNDS=${ROUNDS:-1}
HUD=${HUD:-1}
PICS=${PICS:-0}
P3=${P3:-"M S R T"}
BLOCKS=${BLOCKS:-"A B C D F G P N"}
begin
if [ "$HUD" = 1 ]; then
  ensure_table; t "hud thieves: flat,board,body,shell,rings,switch" "$O/hud.out" python3 scripts/hud_test.py --tag HUDT --fixture thieves --equip $GEAR --stay --parts flat,board,body,shell,rings,switch
  savelog
fi
export HUD_PICS=${HUD_PICS:-/home/bara/Projects/flat2vr/HandOfFate2/captures/hud-built-2026-10-18/v2}
if [ "$PICS" = 1 ]; then
  ensure_table; t "pics snow + soldiers" "$O/pics1.out" python3 scripts/hud_pics.py bright --place "snow arena, soldiers" --jump EL_Ambush_Thral --with "EmpireSoldierREF*2,ThievesThugREF*1"
  ensure_table; t "pics night + skeletons" "$O/pics2.out" python3 scripts/hud_pics.py night --place "night arena, skeletons" --jump EL_Ambush_Undead --with "SkeletonNEWREF*2,SkeletonKnightREF*1"
  python3 scripts/hud_pics.py sheet >> "$O/pics.out" 2>&1
  savelog
fi
# the shell's cost (2026-10-19): Shell vs Rings on this DLL, then p3combat's DLL (env 2's build, read only) in the same fight
if [ "${PERF:-0}" = 1 ]; then
  ensure_table; t "perf big: shell vs rings" "$O/perf.out" bash scripts/chains/hud/perf.sh hud
  P3DLL=/home/bara/Projects/Flat2VR-env2/HandOfFate2/build/bin/HoF2VR.dll
  if [ -f "$P3DLL" ]; then
    guard; savelog; scripts/stop.sh > /dev/null 2>&1
    cp "$P3DLL" "$G3/BepInEx/plugins/HoF2VR/HoF2VR.dll"
    python3 scripts/session.py start --snapshot magician_mid --to table --no-install >> "$O/sessions.out" 2>&1
    say "p3combat's DLL at the table: $(sha1sum < "$G3/BepInEx/plugins/HoF2VR/HoF2VR.dll" | cut -c1-8)"
    t "perf big: p3combat's DLL" "$O/perf_p3c.out" bash scripts/chains/hud/perf.sh p3c
    table   # this build again (session.py installs build/bin)
  fi
fi
# the playtest-3 markers not yet pictured in a real fight (p3_pics.py): soldiers with shields' fixture, the user's look
if [ "${P3PICS:-0}" = 1 ]; then
  ensure_table
  python3 scripts/combat_fixture.py --fixture shield --god --physical --equip $GEAR >> "$O/p3pics.out" 2>&1
  t "p3 pics ${P3PICS_PARTS:-melee glow aim offview shieldlow}" "$O/p3pics.out2" env LINES_OUT=$HUD_PICS/p3 python3 scripts/p3_pics.py ${P3PICS_PARTS:-melee glow aim offview shieldlow}
  savelog
fi
for b in $P3; do
  case $b in
    M) ensure_table; t "p3 M mix: tiers,refill,damage,reverse,lined,glow,stick,shield" "$O/p3M.out" python3 scripts/p3_test.py --tag H3M --fixture mix --equip $GEAR --stay --parts tiers,refill,damage,reverse,lined,glow,stick,shield ;;
    S) ensure_table; t "p3 S soldiers: mirror,ring" "$O/p3S.out" python3 scripts/p3_test.py --tag H3S --fixture shield --equip $GEAR --stay --parts mirror,ring ;;
    R) ensure_table; t "p3 R thieves: ring" "$O/p3R.out" python3 scripts/p3_test.py --tag H3R --fixture thieves --equip $GEAR --stay --parts ring ;;
    L) ensure_table; t "p3 L lobs with a shield: groundgold" "$O/p3L.out" python3 scripts/p3_test.py --tag H3L --fixture lobs --equip $GEAR --stay --parts groundgold ;;
    T) ensure_table; t "p3 T tutorial skeletons + throwers, no shield: pips,ground" "$O/p3T.out" python3 scripts/p3_test.py --tag H3T --fixture tut --equip HugeHammer --stay --parts pips,ground
       leave; guard; yield_slot; table ;;
  esac
done
savelog
for i in $(seq 1 "$ROUNDS"); do
  for b in $BLOCKS; do
    case $b in
      A) ensure_table; t "r$i A shadows leadhold,combogaps" "$O/A$i.out" python3 scripts/audit_test.py --tag H${i}A --fixture shadows --equip $GEAR --stay --parts leadhold,combogaps ;;
      B) ensure_table; t "r$i B thieves waiting,waiting,dash,strafe,health" "$O/B$i.out" python3 scripts/audit_test.py --tag H${i}B --fixture thieves --equip $GEAR --stay --parts waiting,waiting,dash,strafe,health ;;
      C) ensure_table; t "r$i C thieves stage1 block x3" "$O/C$i.out" python3 scripts/stage1_test.py --tag H${i}C --fixture thieves --equip $GEAR --stay --parts block,block,block
         if state | grep -q Level_Play; then t "r$i C move_test (same fight)" "$O/M$i.out" python3 scripts/move_test.py --tag H${i}M --no-fixture; else say "r$i move_test: the fight is over"; fi ;;
      D) ensure_table; t "r$i D combo, interruptcut (combo fixture)" "$O/D$i.out" python3 scripts/stage2_test.py --tag H${i}D --fixture combo --equip $GEAR --stay --parts combo,interruptcut ;;
      F) ensure_table; t "r$i F thieves linecut,crit,wrong,stickuse" "$O/F$i.out" python3 scripts/stage2_test.py --tag H${i}F --fixture thieves --equip $GEAR --stay --parts linecut,crit,wrong,stickuse ;;
      G) ensure_table; t "r$i G dodge fixture redbar,redlean" "$O/G$i.out" python3 scripts/stage1_test.py --tag H${i}G --fixture dodge --equip $GEAR --stay --parts redbar,redlean ;;
      P) ensure_table; t "r$i P lobs knock,drop,dodge,offview" "$O/P$i.out" python3 scripts/bomb_test.py --tag H${i}P --stay --parts knock,drop,dodge,offview ;;
      N) ensure_table; t "r$i N gnomes watch,cut,dash,cling" "$O/N$i.out" python3 scripts/gnome_test.py --tag H${i}N --stay --parts watch,cut,dash,cling ;;
    esac
  done
  savelog
done
if [ "${FOCUS:-1}" = 1 ]; then guard; yield_slot; savelog; t "E focus_test (autoplay's walk from a fresh table)" "$O/E.out" python3 scripts/focus_test.py; fi
finish
