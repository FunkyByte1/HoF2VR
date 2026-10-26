#!/usr/bin/env bash
# ab.sh PREFIX STEP...  - A/B helper for the mod's `gfx` knobs (test session must be running).
# A STEP is either "LABEL" (measure 900 frames with the current settings) or "=CMD" (send a mod
# command, e.g. "=gfx ao 12 half", then wait 3 s for the change to settle).
# Prints one compact line per measurement (also appended to logs/perf.log by measure.sh).
. "$(dirname "${BASH_SOURCE[0]}")/hof2env.sh"   # H: this worktree (docs/two-environments.md)
P=$1; shift
for s in "$@"; do
  if [ "${s:0:1}" = "=" ]; then
    python3 "$H/scripts/cmd.py" "${s:1}" >/dev/null
    sleep 3
  else
    "$H/scripts/measure.sh" "$P $s" 900 | sed -E 's/\| misses.*//; s/\| cpu logic [^|]*//; s/^[0-9:]+ perf 900 //'
  fi
done
