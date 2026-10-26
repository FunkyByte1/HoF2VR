#!/usr/bin/env bash
# Reserve the one-game slot for a whole test chain (coordinator 2026-10-14 ~00:25: the candidate chain needs hours of the
# slot; docs/two-environments.md "Reservations").
#   reserve.sh start [PID]   keep $VRS/reserved-envN fresh (touched every 20 s) while PID lives (default: the caller's
#                            shell); every other environment's run.sh waits meanwhile, as if this environment's game ran
#   reserve.sh stop          remove it (and its keeper)
#   reserve.sh status        who holds a fresh reservation
# In a chain:  scripts/reserve.sh start $$; trap 'scripts/reserve.sh stop' EXIT INT TERM
# The keeper also removes the file and ends by itself once PID is gone (a chain killed without its trap), and after
# HOF2VR_RESERVE_MAX_H hours (default 6) at the latest.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/hof2env.sh"
VRS="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"; VRS="${VRS%/hof2vr-env*}/vr-session"
RES="$VRS/reserved-env$HOF2VR_ENV"
PIDF="$H/testdata/reserve.pid"
mkdir -p "$VRS" "$H/testdata"
case "${1:-status}" in
  start)
    owner="${2:-$PPID}"
    if [ -f "$PIDF" ] && kill -0 "$(cat "$PIDF")" 2>/dev/null; then kill "$(cat "$PIDF")" 2>/dev/null || true; fi
    touch "$RES"
    max=$(( ${HOF2VR_RESERVE_MAX_H:-6} * 3600 ))
    (
      t0=$(date +%s)
      while kill -0 "$owner" 2>/dev/null && [ $(( $(date +%s) - t0 )) -lt "$max" ]; do touch "$RES"; sleep 20; done
      rm -f "$RES"
    ) > /dev/null 2>&1 &
    echo $! > "$PIDF"
    echo "reserved the game slot for env $HOF2VR_ENV while pid $owner lives (keeper $(cat "$PIDF"))"
    ;;
  stop)
    if [ -f "$PIDF" ]; then kill "$(cat "$PIDF")" 2>/dev/null || true; rm -f "$PIDF"; fi
    rm -f "$RES"
    echo "reservation of env $HOF2VR_ENV released"
    ;;
  status)
    now=$(date +%s)
    for f in "$VRS"/reserved-env*; do
      [ -e "$f" ] || continue
      age=$(( now - $(stat -c %Y "$f") ))
      echo "${f##*/}: touched ${age} s ago$([ "$age" -le 120 ] && echo ' (fresh: the slot is reserved)' || echo ' (stale)')"
    done
    ;;
  *) echo "usage: reserve.sh start [PID] | stop | status" >&2; exit 2 ;;
esac
