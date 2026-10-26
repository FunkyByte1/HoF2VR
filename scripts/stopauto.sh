#!/usr/bin/env bash
# Stop test drivers (autoplay / fight / soak) without matching the caller's own command line.
# Only this worktree's drivers (docs/two-environments.md): the other environment's keep running.
. "$(dirname "${BASH_SOURCE[0]}")/hof2env.sh"
for p in autoplay fight soak; do
  pids=$(hof2vr_script_pids "$p.py")
  [ -n "$pids" ] && kill $pids 2>/dev/null
done
exit 0
