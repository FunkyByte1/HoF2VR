#!/bin/sh
# Hand of Fate 2 VR launcher. Steam launch options:  ./hof2vr_launch.sh %command%
#  - The player always comes first: a test instance of the game started by the developer's tools
#    (HOF2VR_TEST=1 in its environment) is stopped and test files are removed before the game starts.
#    (hof2vr_launch.sh --make-room-only does just that; the "Hand of Fate 2 VR" app-list entry uses it.)
#  - VR when an OpenXR runtime is active (WiVRn publishes one only while the Quest is connected),
#    otherwise normal flat play (-vrmode None, so the OpenVR layer never starts).
#  - An empty file disable_vr.txt next to this script forces flat play.
cd "$(dirname "$0")" || exit 1

# ---- the player first -------------------------------------------------------------------------------
# Works inside Steam's container too: if the test instance's process is not visible from here, it still
# reads hof2vr_stop_tests and quits by itself; its heartbeat file tells when it is gone.
hof2vr_make_room() {
  rm -f hof2vr_testmode.txt
  : > hof2vr_stop_tests 2>/dev/null
  pids=
  for d in /proc/[0-9]*; do
    [ "$(cat "$d/comm" 2>/dev/null)" = "Hand of Fate 2." ] || continue
    tr '\0' '\n' < "$d/environ" 2>/dev/null | grep -qx 'HOF2VR_TEST=1' && pids="$pids ${d#/proc/}"
  done
  stopped=
  if [ -n "$pids" ]; then
    echo "hof2vr_launch: stopping test instance(s):$pids" >&2
    kill -TERM $pids 2>/dev/null
    stopped=1
  fi
  i=0
  while [ $i -lt 40 ]; do          # at most 10 s
    alive=
    for p in $pids; do [ -d "/proc/$p" ] && alive=1; done
    if [ -f hof2vr_test_running ]; then
      beat=$(stat -c %Y hof2vr_test_running 2>/dev/null || echo 0)
      [ $(( $(date +%s) - beat )) -lt 3 ] && alive=1 && stopped=1
    fi
    [ -z "$alive" ] && break
    [ $i -eq 20 ] && [ -n "$pids" ] && kill -KILL $pids 2>/dev/null
    sleep 0.25
    i=$((i + 1))
  done
  rm -f hof2vr_stop_tests hof2vr_test_running
  [ -n "$stopped" ] && echo "hof2vr_launch: test instance gone" >&2
  return 0
}
hof2vr_make_room
# the player gets the released build of the mod (test builds may be in plugins/ after a test run)
for f in BepInEx/hof2vr_release/*; do
  [ -f "$f" ] || continue
  dst="BepInEx/plugins/HoF2VR/${f##*/}"
  if ! cmp -s "$f" "$dst" 2>/dev/null; then
    cp -f "$f" "$dst.new" && mv -f "$dst.new" "$dst" && echo "hof2vr_launch: release build of ${f##*/} put back" >&2
  fi
done
if [ "$1" = "--make-room-only" ]; then
  [ -n "$stopped" ] && sleep 1    # let Steam notice that the test instance has quit
  exit 0
fi

# ---- the session's logs are kept (a later test launch overwrites BepInEx/LogOutput.log) ------------
START=$(date +%Y%m%d-%H%M%S)
hof2vr_save_logs() {
  d="BepInEx/hof2vr_sessions/$START"
  mkdir -p "$d" 2>/dev/null || return 0
  cp -f BepInEx/LogOutput.log "$d/" 2>/dev/null
  cp -f "${XDG_CONFIG_HOME:-$HOME/.config}/unity3d/Defiant Development/Hand of Fate 2/Player.log" "$d/" 2>/dev/null
  cp -f "${XDG_STATE_HOME:-$HOME/.local/state}/OpenComposite/logs/opencomposite.log" "$d/" 2>/dev/null
  cp -f BepInEx/config/flat2vr.hof2vr.cfg "$d/" 2>/dev/null
  # the last 12 sessions
  ls -1d BepInEx/hof2vr_sessions/*/ 2>/dev/null | sort | head -n -12 | while read -r old; do rm -rf "$old"; done
  return 0
}

# bug marks made in VR (Dev.BugMark) go into this session's folder, next to the logs copied there at the end
export HOF2VR_SESSION_DIR="$PWD/BepInEx/hof2vr_sessions/$START"

# The game's memory manager (Mono's Boehm collector) starts with a 1 GB heap and lets it grow before it tidies up, instead
# of tidying up to stay small (2026-10-13, two 32-minute soaks of table events, fights and loads: the hitches it causes at
# the Dealer's table in plain view 20 -> 10, in fights 1 -> 0; each one no longer; loads the same; about 0.1-0.2 GB more
# memory). Set GC_INITIAL_HEAP_SIZE to an empty value in Steam's launch options to start without it.
export GC_INITIAL_HEAP_SIZE="${GC_INITIAL_HEAP_SIZE-1073741824}" GC_FREE_SPACE_DIVISOR="${GC_FREE_SPACE_DIVISOR-1}"

VR=1
CFG="${XDG_CONFIG_HOME:-$HOME/.config}"
# (-L too: inside Steam's container the runtime file is a symlink into the host /usr and dangles)
have_runtime() { [ -e "$1" ] || [ -L "$1" ]; }
if [ -z "$XR_RUNTIME_JSON" ] && ! have_runtime "$CFG/openxr/1/active_runtime.json" && ! have_runtime /etc/xdg/openxr/1/active_runtime.json; then
  VR=0
fi
[ -e disable_vr.txt ] && VR=0
# Inside Steam's container the host /usr is under /run/host
if [ -d /run/host/usr/lib64/opencomposite/runtime ] && [ ! -d /usr/lib64/opencomposite/runtime ]; then
  export VR_OVERRIDE=/run/host/usr/lib64/opencomposite/runtime
fi
if [ "$VR" = 1 ]; then
  echo "hof2vr_launch: VR mode (VR_OVERRIDE=${VR_OVERRIDE:-})" >&2
  ./run_bepinex.sh "$@" -force-glcore -screen-fullscreen 0 -screen-width 1920 -screen-height 1080
  rc=$?
else
  echo "hof2vr_launch: flat mode (no OpenXR runtime active or disable_vr.txt)" >&2
  # keep OpenVR from loading at all: Unity still probes for a headset at startup, and with
  # WiVRn connected that probe opened a (black) VR session in the headset
  export VR_OVERRIDE=/nonexistent/hof2vr-flat
  # VR runs force a 1920x1080 window, which Unity remembers; flat play goes back to fullscreen
  RES=$(xrandr --listmonitors 2>/dev/null | awk 'NR==2{split($3,a,/[x+]/); split(a[1],w,"/"); split(a[2],h,"/"); print w[1], h[1]}')
  if [ -n "$RES" ]; then
    set -- "$@" -screen-fullscreen 1 -screen-width "${RES% *}" -screen-height "${RES#* }"
  fi
  ./run_bepinex.sh "$@" -vrmode None
  rc=$?
fi
hof2vr_save_logs
exit $rc
