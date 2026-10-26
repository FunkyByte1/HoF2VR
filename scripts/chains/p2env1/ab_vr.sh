#!/usr/bin/env bash
# The seated tour's VR settings stage alone (vrsettings_page_by_touch, and touch_with_grip_held over its pokes) with one DLL:
# ab_vr.sh new|old  (old = work/p2dll/pre_a950d8b.dll: b768940, the commit before the boards change, built in a temp folder)
set -u
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
P="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2/BepInEx/plugins/HoF2VR/HoF2VR.dll"
if [ "$1" = old ]; then
  python3 scripts/session.py stop > /dev/null 2>&1
  cp work/p2dll/pre_a950d8b.dll "$P"
  python3 scripts/session.py start --no-install --snapshot magician_mid 2>&1 | tail -1
else
  python3 scripts/session.py start --snapshot magician_mid 2>&1 | tail -1
fi
echo "plugins: $(sha1sum < "$P" | cut -c1-8)"
python3 scripts/playtest.py --skip-start --seated --only vrsettings
