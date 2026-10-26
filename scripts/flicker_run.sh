#!/usr/bin/env bash
# flicker_run.sh <tag> [frames] [raw] [sequence]: the standard look-around (still 1.5 s, right 90, left to -90 looking a
# little down, back up, round behind and down) with `lumalog` running, then scripts/flicker.py on it.
. "$(dirname "${BASH_SOURCE[0]}")/hof2env.sh"   # H: this worktree's environment (docs/two-environments.md)
TAG=$1; N=${2:-1900}; RAW=${3:-}
SEQ=${4:-"wait 1500; turn 0 90 0 0 4000; turn 90 -90 0 -10 8000; turn -90 0 -10 10 4000; turn 0 180 10 0 6000; turn 180 360 0 -20 6000"}
python3 $H/scripts/xrsim.py "head 0 1.75 0 0 0 0" >/dev/null
sleep 0.5
python3 $H/scripts/cmd.py "lumalog $TAG $N${RAW:+ $RAW}" >/dev/null
python3 $H/scripts/xrsim.py "$SEQ" >/dev/null
# the probe writes 2 lines a frame (left, right): wait until it has all of them (max 30 s more)
for i in $(seq 1 60); do [ "$(wc -l < $H/testdata/luma/$TAG.csv)" -ge $((2*N+1)) ] && break; sleep 0.5; done
sleep 0.5
python3 $H/scripts/flicker.py "$TAG" --top ${TOP:-8}
