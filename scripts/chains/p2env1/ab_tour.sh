#!/usr/bin/env bash
# The seated tour's start and the four checks the coordinator listed (title_flicker, title_menu_board, jumpmenu_chord,
# fight_exit_stalls_hidden), with one DLL: ab_tour.sh new|8b  (8b = work/p2dll/release_8b.dll, a copy of the Steam install's
# hof2vr_release DLL 8af22b73; the copy goes into the test plugins folder, session.py start --no-install)
set -u
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
P="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2/BepInEx/plugins/HoF2VR/HoF2VR.dll"
if [ "$1" = 8b ]; then
  python3 scripts/session.py stop > /dev/null 2>&1
  cp work/p2dll/release_8b.dll "$P"
  echo "plugins: $(sha1sum < "$P" | cut -c1-8) (8b)"
  python3 scripts/session.py start --no-install --snapshot magician_mid 2>&1 | tail -1
else
  python3 scripts/session.py start --snapshot magician_mid 2>&1 | tail -1
  echo "plugins: $(sha1sum < "$P" | cut -c1-8) (build/bin $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8))"
fi
python3 scripts/playtest.py --skip-start --seated --only title,table,devkeys,fight
