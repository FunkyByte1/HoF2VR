#!/usr/bin/env bash
# env 3, playtest 4 (crit arrows, gnomes): a dev session held by a reservation - Monado, the table (this build installed).
# The reservation's holder is a sleep (logs/p4e3dev/hold.pid); end it all with: bash scripts/chains/p4e3/dev.sh stop
# Run unsandboxed: nohup setsid bash scripts/chains/p4e3/dev.sh > /dev/null 2>&1 &
set -u
NAME=${NAME:-p4e3dev}
LOG=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env3/HandOfFate2/scripts/chains/hud/lib.sh
mkdir -p "$O"
if [ "${1:-}" = stop ]; then
  savelog; stopall
  scripts/reserve.sh stop >> "$O/reserve.out" 2>&1
  [ -f "$O/hold.pid" ] && kill "$(cat "$O/hold.pid")" 2>/dev/null; rm -f "$O/hold.pid"
  say "dev stop: game, Monado and the reservation released"
  exit 0
fi
guard
sleep 14400 & echo $! > "$O/hold.pid"
scripts/reserve.sh start "$(cat "$O/hold.pid")" >> "$O/reserve.out" 2>&1
say "dev start: DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8) branch $(git rev-parse --short HEAD) (sched $(ps -o cls= -p $$)), reserved"
scripts/monado.sh start >> "$O/monado.out" 2>&1
table
say "at the table: $(state | cut -c1-60)"
