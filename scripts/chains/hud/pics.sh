#!/usr/bin/env bash
# env 3: the pictures for the user (hud_pics.py) - a bright arena with soldiers, a night arena with skeletons, each from a
# fresh session (fair turns), then the labelled contact sheet. NAME defaults to hudpics.
# Run unsandboxed: nohup setsid bash scripts/chains/hud/pics.sh > /dev/null 2>&1 &
set -u
NAME=${NAME:-hudpics}
LOG=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env3/HandOfFate2/scripts/chains/hud/lib.sh
mkdir -p "$O"
say "pics start: DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8)"
guard; scripts/monado.sh start >> "$O/monado.out" 2>&1
for f in ${FIGHTS:-bright night}; do
  guard; table
  case $f in
    bright) t "pics bright: snow arena, soldiers" "$O/pics_bright.out" python3 scripts/hud_pics.py bright --place "snow arena, soldiers" --jump EL_Ambush_Thral --with "EmpireSoldierREF*2,ThievesThugREF*1" ;;
    night) t "pics night: night arena, skeletons" "$O/pics_night.out" python3 scripts/hud_pics.py night --place "night arena, skeletons" --jump EL_Ambush_Undead --with "SkeletonNEWREF*2,SkeletonKnightREF*1" ;;
  esac
  savelog
done
python3 scripts/hud_pics.py sheet >> "$O/sheet.out" 2>&1
say "sheet: $(tail -1 "$O/sheet.out")"
scripts/stop.sh > /dev/null 2>&1
say "pics end"
