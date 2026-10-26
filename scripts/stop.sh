#!/usr/bin/env bash
# Stop the TEST instance of the game: ask the mod to quit, then SIGTERM, then SIGKILL. Verifies the real save
# after. A game the user started (no HOF2VR_TEST=1 in its environment) is never touched: exit 4.
# Only this worktree's environment (docs/two-environments.md): the other environment's test instance is left alone.
. "$(dirname "${BASH_SOURCE[0]}")/userguard.sh"
for p in $(hof2vr_user_game_pids); do
  echo "USER FIRST: Hand of Fate 2 pid $p is the user's game - not stopping it" >&2
  hof2vr_savecheck
  exit 4
done
if [ -n "$(hof2vr_test_pids)" ]; then
  python3 "$H/scripts/cmd.py" quit 3 >/dev/null 2>&1 || true
  for i in $(seq 1 20); do [ -n "$(hof2vr_test_pids)" ] || break; sleep 0.5; done
  P=$(hof2vr_test_pids); [ -n "$P" ] && kill -TERM $P 2>/dev/null; sleep 2
  P=$(hof2vr_test_pids); [ -n "$P" ] && kill -KILL $P 2>/dev/null
fi
rm -f "$HOF2VR_GAME_DIR/hof2vr_test_running"
# the one-game flag (run.sh): this environment's game is gone
VRS="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"; VRS="${VRS%/hof2vr-env*}/vr-session"
[ -n "$(hof2vr_test_pids)" ] || rm -f "$VRS/game-env$HOF2VR_ENV"
# the virtual screen (scripts/vdisplay.sh) goes with its last client: no test game and no Monado of this environment
# (after a flat run; monado.sh stop stops it otherwise)
[ -n "$(hof2vr_test_pids)" ] || [ -n "$(hof2vr_monado_pids)" ] || "$H/scripts/vdisplay.sh" stop > /dev/null
hof2vr_savecheck
