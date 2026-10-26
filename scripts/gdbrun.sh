#!/usr/bin/env bash
# Run the game under gdb with the BepInEx/doorstop environment; print backtraces of crashes.
#   gdbrun.sh [extra Unity args...]     (test mode via HOF2VR_TEST=1)
# (H, G and this worktree's environment from userguard.sh / hof2env.sh, docs/two-environments.md)
. "$(dirname "${BASH_SOURCE[0]}")/userguard.sh"; hof2vr_guard   # never while the user plays
hof2vr_savecheck || exit 3
hof2vr_check_game_dir
cd "$G"
unset SteamAppId SteamGameId SteamOverlayGameId   # (test runs never talk to Steam: SteamGuard)
export HOF2VR_TEST=1 HOF2VR_TEST_DIR="$H/testdata"
# env N: its own Monado (if XR_RUNTIME_JSON points at Monado) and per-user files; env 1 unchanged
[ "$HOF2VR_ENV" = 1 ] || export XR_RUNTIME_JSON=/usr/share/openxr/1/openxr_monado.json
hof2vr_xr_env; hof2vr_game_files_env
export DOORSTOP_ENABLED=1 DOORSTOP_TARGET_ASSEMBLY="$G/BepInEx/core/BepInEx.Preloader.dll" DOORSTOP_IGNORE_DISABLED_ENV=0
export LD_LIBRARY_PATH="$G:${LD_LIBRARY_PATH:-}"
GCMD="$H/logs/gdbrun.cmd"
cat > "$GCMD" <<'G'
set pagination off
set confirm off
set print thread-events off
handle SIGXCPU SIGPWR SIG32 SIG33 SIG34 SIG35 SIG36 SIG37 SIGUSR1 SIGUSR2 SIGPIPE nostop noprint pass
handle SIGSEGV stop print pass
handle SIGABRT stop print pass
set environment LD_PRELOAD=libdoorstop.so
run
echo \n=== STOP 1 ===\n
bt 25
info sharedlibrary
continue
echo \n=== STOP 2 ===\n
bt 25
continue
echo \n=== STOP 3 ===\n
bt 25
kill
quit
G
timeout 180 gdb -q -batch -x "$GCMD" --args "./Hand of Fate 2.x86_64" -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -logFile "$H/logs/player-gdb.log" "$@" > "$H/logs/gdb.log" 2>&1
echo "gdb exit $?"
hof2vr_savecheck
