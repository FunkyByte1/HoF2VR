#!/usr/bin/env bash
# measure.sh LABEL [frames]  - one measurement window through the mod's `perf N` command: frame
# times (avg, percentiles, >11.1 / >13 ms, missed refreshes), main-thread work, GC, and the GPU's
# clock / busy % / power sampled from sysfs over the same frames. Prints the line and appends it to
# logs/perf.log.  (scripts/ft.sh is the older 1-ms-bucket histogram used before 2026-10-11.)
. "$(dirname "${BASH_SOURCE[0]}")/hof2env.sh"   # H: this worktree (docs/two-environments.md)
N=${2:-900}
P="$H/testdata/perf.log"
n0=$(cat "$P" 2>/dev/null | wc -l)
python3 "$H/scripts/cmd.py" "perf $N $1" >/dev/null || exit 1
for i in $(seq 1 $((N / 30 + 40))); do
  sleep 0.5
  [ "$(cat "$P" 2>/dev/null | wc -l)" -gt "$n0" ] && break
done
line=$(tail -n +$((n0 + 1)) "$P" | head -1)
echo "$line" | tee -a "$H/logs/perf.log"
