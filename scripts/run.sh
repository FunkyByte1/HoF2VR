#!/usr/bin/env bash
# Launch Hand of Fate 2 for an automated test (test mode: isolated saves, no achievements).
#   run.sh monado|wivrn|flat [vulkan|gl] [extra Unity args...]
# Runs this worktree's test environment (docs/two-environments.md): env 1 = the Steam install, env N = its game copy
# and its own Monado.
set -euo pipefail
RT=${1:-monado}; API=${2:-gl}; shift $(( $# >= 2 ? 2 : $# ))
# the user always wins: never start while they play (scripts/userguard.sh; it also sets H, G and HOF2VR_ENV)
. "$(dirname "${BASH_SOURCE[0]}")/userguard.sh"
hof2vr_guard
hof2vr_savecheck
hof2vr_check_game_dir
if [ -n "$(hof2vr_test_pids)" ]; then echo "game already running"; exit 2; fi
# One test game at a time across the environments (coordinator 2026-10-12 ~06:30, after the 06:13 GPU reset): wait until no
# other environment's flag $VRS/game-envN is fresh (missing or older than 120 s; polled every 5 s, at most an hour), then
# hold this environment's own: touched every 5 s while this game runs, removed when it stops (and if the start fails).
VRS="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"; VRS="${VRS%/hof2vr-env*}/vr-session"
FLAG="$VRS/game-env$HOF2VR_ENV"
# Fair turns (2026-10-12 ~22:50: env 2 waited 40 minutes while env 1 ran sessions back to back - each new run.sh took the
# slot before env 2's 15 s poll saw it free): a run.sh that waits keeps a fresh $VRS/waiting-envN holding the time it began
# to wait, and gives way to any environment that has waited longer. A run.sh of another worktree without waiting flags
# (an older copy) counts as waiting while its environment's game flag is not fresh. Polls every 5 s.
WAITF="$VRS/waiting-env$HOF2VR_ENV"
mkdir -p "$VRS"
my_wait=$(date +%s.%N)
waited_longer() {
  local w st p cl envn
  for w in "$VRS"/waiting-env*; do
    [ -e "$w" ] || continue
    [ "$w" = "$WAITF" ] && continue
    [ $(( $(date +%s) - $(stat -c %Y "$w") )) -le 30 ] || continue
    st=$(cat "$w" 2>/dev/null); [ -n "$st" ] || st=0
    awk -v a="$st" -v b="$my_wait" 'BEGIN { exit !(a < b) }' && { echo "${w##*/}"; return 0; }
  done
  for p in $(pgrep -f '/HandOfFate2/scripts/run\.sh' 2>/dev/null); do
    # the process may have ended since pgrep listed it: a failed redirection must not end this script (set -e)
    cl=$({ tr '\0' ' ' < "/proc/$p/cmdline"; } 2>/dev/null || true)
    [ -n "$cl" ] || continue
    case "$cl" in *"$H/scripts/run.sh"*|"") continue;; esac
    envn=$(printf '%s' "$cl" | sed -n 's#.*/Flat2VR-env\([0-9]*\)/HandOfFate2/scripts/run\.sh.*#\1#p'); [ -n "$envn" ] || envn=1
    [ "$envn" = "$HOF2VR_ENV" ] && continue
    [ -e "$VRS/waiting-env$envn" ] && continue   # a new copy: its flag says it
    [ -e "$VRS/game-env$envn" ] && [ $(( $(date +%s) - $(stat -c %Y "$VRS/game-env$envn") )) -le 120 ] && continue   # its game runs: busy below
    echo "run.sh of env $envn (waiting)"; return 0
  done
  return 1
}
# Reservations (coordinator 2026-10-14 ~00:25, for a candidate chain that needs the slot for hours): while another
# environment's $VRS/reserved-envN is fresh (under 120 s old; scripts/reserve.sh keeps it so for a whole chain), this run.sh
# waits exactly as if that environment's game were running. An environment holding its own reservation never gives way to
# the others' waiting flags (they wait for its reservation: giving way would deadlock both).
MYRES="$VRS/reserved-env$HOF2VR_ENV"
reserved_by_other() {
  local f
  for f in "$VRS"/reserved-env*; do
    [ -e "$f" ] || continue
    [ "$f" = "$MYRES" ] && continue
    [ $(( $(date +%s) - $(stat -c %Y "$f") )) -le 120 ] && { echo "${f##*/}"; return 0; }
  done
  return 1
}
i_reserve() { [ -e "$MYRES" ] && [ $(( $(date +%s) - $(stat -c %Y "$MYRES") )) -le 120 ]; }
trap 'rm -f "$WAITF"' EXIT
busy=""
for i in $(seq 1 720); do
  busy=""
  for f in "$VRS"/game-env*; do
    [ -e "$f" ] || continue
    [ "$f" = "$FLAG" ] && continue
    [ $(( $(date +%s) - $(stat -c %Y "$f") )) -le 120 ] && busy="$f"
  done
  [ -z "$busy" ] && busy=$(reserved_by_other || true)
  [ -z "$busy" ] && ! i_reserve && busy=$(waited_longer || true)
  [ -z "$busy" ] && break
  echo "$my_wait" > "$WAITF"
  [ $((i % 12)) -eq 1 ] && echo "one game at a time: ${busy##*/} is fresh - waiting" >&2
  sleep 5
done
rm -f "$WAITF"
if [ -n "$busy" ]; then echo "one game at a time: ${busy##*/} stayed fresh for an hour - not starting" >&2; exit 3; fi
touch "$FLAG"
trap 'rm -f "$FLAG"' EXIT
# environment N >= 2: room on the GPU first, before the VRAM wait below. The quiet flag is held while this game runs (GPU
# voice jobs yield and free their memory), then the boot waits for free VRAM (scripts/gpu_gate.py)
if [ "$HOF2VR_ENV" != 1 ] && [ "$RT" != flat ]; then
  python3 "$H/scripts/gpu_gate.py" wait || exit 3
fi
# VRAM: 4 GB free before a test game starts. Measured 2026-10-17: a test game holds about 1.5 GB and Monado 0.15 GB, and with
# the user's Ollama model loaded (8.4 GB) 5.0-5.8 GB is free, so 6 GB could never be met. (2026-10-17: GPU faults in kwin while
# tests ran beside the user's Ollama model, 8.4 GB loaded all day; 2026-10-11 23:12 the GPU hung and reset when a voice job's model, a second
# test game and the desktop had used it up). Wait for it, at most 10 minutes. HOF2VR_VRAM_MIN_GB overrides the amount.
VRAM_DEV=/sys/bus/pci/devices/0000:0d:00.0
if [ -r "$VRAM_DEV/mem_info_vram_used" ] && [ -r "$VRAM_DEV/mem_info_vram_total" ]; then
  need=$(awk -v g="${HOF2VR_VRAM_MIN_GB:-4}" 'BEGIN{printf "%d", g*1024*1024*1024}')
  for i in $(seq 0 120); do
    free=$(( $(cat "$VRAM_DEV/mem_info_vram_total") - $(cat "$VRAM_DEV/mem_info_vram_used") ))
    [ "$free" -ge "$need" ] && break
    if [ "$i" -eq 120 ]; then
      echo "VRAM: only $((free / 1048576)) MB free after 10 min (need $((need / 1048576)) MB) - not starting the test game" >&2
      exit 5
    fi
    [ $((i % 6)) -eq 0 ] && echo "VRAM: $((free / 1048576)) MB free, waiting for $((need / 1048576)) MB before starting the test game" >&2
    sleep 5
  done
fi
if [ "$HOF2VR_ENV" != 1 ] && [ "$RT" = wivrn ]; then
  echo "environment $HOF2VR_ENV: the Quest (WiVRn) belongs to environment 1" >&2; exit 2
fi
# a stop request from an earlier user launch (hof2vr_launch.sh) and a dead instance's heartbeat are stale now
rm -f "$G/hof2vr_stop_tests" "$G/hof2vr_test_running"
mkdir -p "$H/testdata/remote" "$H/testdata/cmd" "$H/logs"
[ -f "$H/testdata/remote/player_data.profile" ] || cp "$H/backups/20261010-063954-pre-mod/userdata-456670/remote/player_data.profile" "$H/testdata/remote/"
rm -f "$H/testdata/cmd/command.txt" "$H/testdata/status.json"
# HOF2VR_TEST=1 is what makes the mod a test instance (isolated saves; it steps aside for the user). It never talks to the
# Steam client either (SteamGuard: no SteamAPI.Init - the user's Steam playtime and "in game" status stay untouched), so no
# SteamAppId / SteamGameId (2026-10-18; any inherited from the caller are dropped).
unset SteamAppId SteamGameId SteamOverlayGameId
# A flag file in the game folder alone never does (a Steam launch must never be a test run).
export HOF2VR_TEST=1 HOF2VR_TEST_DIR="$H/testdata" HOF2VR_UI="${HOF2VR_UI:-}"
# the player's launcher starts the game with these collector settings (2026-10-13, dist/hof2vr_launch.sh): tests run as the
# player's game does; set either to an empty value to start without it (e.g. GC_INITIAL_HEAP_SIZE= scripts/run.sh ...)
export GC_INITIAL_HEAP_SIZE="${GC_INITIAL_HEAP_SIZE-1073741824}" GC_FREE_SPACE_DIVISOR="${GC_FREE_SPACE_DIVISOR-1}"
# (HOF2VR_ENV, exported by hof2env.sh, tells which environment's harness may stop this instance)
# test runs start without the logo screens and the title intro ([Dev] SkipIntros); HOF2VR_SKIPINTROS=0 plays them
export HOF2VR_SKIPINTROS="${HOF2VR_SKIPINTROS:-1}"
# A small window, never full screen (the user, 2026-10-17 19:5x: "Stop starting the game in fullscreen" - 1920x1080 under
# KDE's 1.25 scaling covered most of their screen). The headset's pictures do not depend on it. HOF2VR_WINDOW=WxH overrides.
WIN=${HOF2VR_WINDOW:-1280x720}
ARGS=(-screen-fullscreen 0 -screen-width "${WIN%x*}" -screen-height "${WIN#*x}")
case $API in vulkan) ARGS+=(-force-vulkan);; gl) ARGS+=(-force-glcore);; esac
case $RT in
  # (env N: hof2vr_xr_env points the game at this environment's own Monado; env 1 unchanged)
  monado) export XR_RUNTIME_JSON=/usr/share/openxr/1/openxr_monado.json; hof2vr_xr_env ;;
  wivrn)  export XR_RUNTIME_JSON=/usr/share/openxr/1/openxr_wivrn.json ;;
  # flat: like the launcher, keep OpenVR from loading at all (with WiVRn connected, Unity's startup
  # probe otherwise opens a VR session even with -vrmode None)
  flat)   ARGS+=(-vrmode None); export VR_OVERRIDE=/nonexistent/hof2vr-flat ;;
esac
# Unity's prefs in this worktree's testdata/ (every environment since 2026-10-18), env N also the OpenComposite log
hof2vr_game_files_env
# The virtual screen (scripts/vdisplay.sh, 2026-10-17, the user: "Can you have it run on an imaginary screen?"): the test
# game's window goes into this environment's headless gamescope, next to Monado's, never onto the user's desktop, and KWin
# never composites it. HOF2VR_VDISPLAY=0: on the desktop as before. Runs on the Quest (wivrn) stay on the desktop.
if [ "$RT" != wivrn ] && hof2vr_vdisplay_on; then
  "$H/scripts/vdisplay.sh" start > /dev/null && hof2vr_vdisplay_env || {
    echo "no virtual screen for the test game (scripts/vdisplay.sh start; HOF2VR_VDISPLAY=0 runs it on the desktop)" >&2; exit 6; }
fi
# HOF2VR_DEV=1: Unity's development player (same engine version, has symbols) for readable crash
# stacks. Copied into the game folder as "Hand of Fate 2.dev" (same 15-char process name and
# data folder); remove it at handover.
EXE="Hand of Fate 2.x86_64"
if [ -n "${HOF2VR_DEV:-}" ]; then
  EXE="Hand of Fate 2.dev"
  [ -x "$G/$EXE" ] || install -m 755 "$H/downloads/unity-linux-support/LinuxPlayer-dev" "$G/$EXE"
fi
TS=$(date +%Y%m%d-%H%M%S)
LOG="$H/logs/player-$TS.log"
ln -sfn "$LOG" "$H/logs/player-latest.log"
cd "$G"
# the watchdog below must not see the previous run's log; a log of the player's own session is kept first
if [ -f "$G/BepInEx/LogOutput.log" ] && ! grep -aq "test mode=True" "$G/BepInEx/LogOutput.log"; then
  mkdir -p "$H/logs/user-sessions"
  cp "$G/BepInEx/LogOutput.log" "$H/logs/user-sessions/LogOutput-$(date -r "$G/BepInEx/LogOutput.log" +%Y%m%d-%H%M%S).log"
fi
rm -f "$G/BepInEx/LogOutput.log"
if [ -n "${GDB:-}" ]; then
  # Debug launch: doorstop env by hand; gdb stops at Unity's crash abort and dumps the stack.
  export DOORSTOP_ENABLED=1 DOORSTOP_TARGET_ASSEMBLY="$G/BepInEx/core/BepInEx.Preloader.dll" LD_LIBRARY_PATH="$G:${LD_LIBRARY_PATH:-}"
  cp "$H/scripts/crash.gdb" "$H/logs/gdb-$TS.cmd"
  setsid gdb -q -batch -x "$H/logs/gdb-$TS.cmd" --args "./$EXE" "${ARGS[@]}" -logFile "$LOG" "$@" > "$H/logs/gdb-$TS.log" 2>&1 < /dev/null &
elif [ "${HOF2VR_STEAMRT:-}" = 1 ]; then
  # As Steam starts the game (steamapps/../logs/gameprocess_log.txt, 2026-10-17): inside Steam Linux Runtime 1.0 on 2.0
  # (pressure-vessel), run_bepinex.sh inside the container (its SteamLaunch rotation puts it there). Steam's own launch
  # wrapper and reaper are left out (they talk to the Steam client). For N1's proof: the game's process sees what it sees
  # in the user's launch (/usr of the runtime, the forwarded pulse socket).
  SLR="$HOME/.local/share/Steam/steamapps/common"
  setsid "$SLR/SteamLinuxRuntime_soldier/_v2-entry-point" --verb=waitforexitandrun -- "$SLR/SteamLinuxRuntime/scout-on-soldier-entry-point-v2" -- \
    "$G/run_bepinex.sh" "$G/$EXE" "${ARGS[@]}" -logFile "$LOG" "$@" > "$H/logs/stdout-$TS.log" 2>&1 < /dev/null &
else
  setsid ./run_bepinex.sh "./$EXE" "${ARGS[@]}" -logFile "$LOG" "$@" > "$H/logs/stdout-$TS.log" 2>&1 < /dev/null &
fi
GPID=$!   # setsid and run_bepinex.sh exec: this is the game itself
echo "launched ($RT/$API) pid $GPID log $LOG"
# the one-game flag: fresh while this game runs, gone when it stops. The keeper wakes up to 30 s after its game died; by
# then a restart's run.sh may hold the flag already (before, the old keeper deleted it: no flag for up to 30 s, 09:26).
# So it deletes the flag only when no test game of this environment runs and nobody touched the flag since it did.
trap - EXIT
(
  mine=$(stat -c %.9Y "$FLAG" 2>/dev/null || true)
  while kill -0 "$GPID" 2>/dev/null; do touch "$FLAG"; mine=$(stat -c %.9Y "$FLAG" 2>/dev/null || true); sleep 5; done
  [ -z "$(hof2vr_test_pids)" ] && [ "$(stat -c %.9Y "$FLAG" 2>/dev/null || true)" = "$mine" ] && rm -f "$FLAG"
) > /dev/null 2>&1 &
disown
# Watchdog: the mod must confirm its save isolation within 40 s, or the test run is killed
# before the game can touch the real profile (the mod also kills itself if its guards fail).
(
  for i in $(seq 1 80); do
    sleep 0.5
    grep -q "test isolation\] verified" "$G/BepInEx/LogOutput.log" 2>/dev/null && exit 0
  done
  echo "WATCHDOG: no save-isolation confirmation - killing the test instance $GPID" | tee -a "$LOG.watchdog"
  # only the instance this script started, or this environment's test instances (HOF2VR_TEST=1; under gdb
  # $GPID is gdb) - never a game the user started, never the other environment's test instance
  kill -KILL "$GPID" $(hof2vr_test_pids) 2>/dev/null
) &
disown
