#!/usr/bin/env bash
# Real Quest 2 over USB for tests - local ADB only, the headset stays offline.
#   quest.sh status        wake state, focused app, overrides in force
#   quest.sh up            keep it awake unworn (prox_close), pause guardian, reverse the WiVRn port,
#                          start the WiVRn client pointed at 127.0.0.1:9757
#   quest.sh shot NAME     screenshot of what the headset shows -> testdata/shots/quest_NAME.png
#   quest.sh down          undo EVERYTHING "up" did (always run this when done)
set -uo pipefail
# (H and the environment from hof2env.sh; the one Quest belongs to environment 1, docs/two-environments.md)
. "$(dirname "${BASH_SOURCE[0]}")/hof2env.sh"
[ "$HOF2VR_ENV" = 1 ] || { echo "environment $HOF2VR_ENV: the Quest belongs to environment 1" >&2; exit 2; }
SERIAL=${QUEST_SERIAL:?set QUEST_SERIAL to the headset's adb serial}
WIVRN_PKG=org.meumeu.wivrn.github
q() { adb -s "$SERIAL" "$@"; }

case ${1:-status} in
  status)
    q shell dumpsys power | grep -E "mWakefulness=" | head -1
    echo "guardian_pause=$(q shell getprop debug.oculus.guardian_pause)"
    echo "reverse: $(q reverse --list | tr '\n' ' ')"
    q shell dumpsys activity activities | grep -E "topResumedActivity|mResumedActivity" | head -2
    q shell dumpsys battery | grep -E " level:" ;;
  up)
    . "$H/scripts/userguard.sh"; hof2vr_guard   # never while the user plays
    q shell input keyevent KEYCODE_WAKEUP
    q shell am broadcast -a com.oculus.vrpowermanager.prox_close >/dev/null
    q shell setprop debug.oculus.guardian_pause 1
    q reverse tcp:9757 tcp:9757 >/dev/null
    sleep 2
    q shell am start -a android.intent.action.VIEW -d "wivrn+tcp://127.0.0.1:9757" "$WIVRN_PKG" ;;
  shot)
    # what the headset panel shows right now (both eyes, lens-warped), via Android screencap;
    # uses one temporary file of its own and deletes it again
    name=${2:-shot}
    q shell screencap -p /sdcard/hof2vr_shot.png && q pull /sdcard/hof2vr_shot.png "$H/testdata/shots/quest_$name.png" >/dev/null \
      && echo "testdata/shots/quest_$name.png"
    q shell rm -f /sdcard/hof2vr_shot.png ;;
  down)
    q shell am force-stop "$WIVRN_PKG"
    q reverse --remove tcp:9757 2>/dev/null
    q shell setprop debug.oculus.guardian_pause 0
    q shell am broadcast -a com.oculus.vrpowermanager.automation_disable >/dev/null
    echo "overrides undone"; "$0" status ;;
esac
