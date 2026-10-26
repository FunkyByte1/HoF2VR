#!/usr/bin/env bash
# Playtest 3 (env 1), item 3: the per-frame cost at one place, the old whole-world searches against the registries.
# Usage: pt3_perf.sh TAG  (the game at the place; logs the camtime / profile / perf lines into logs/pt3/perf_TAG.log)
H=/home/bara/Projects/flat2vr/HandOfFate2
G="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2"
GL="$G/BepInEx/LogOutput.log"
OUT=$H/logs/pt3/perf_$1.log
c() { python3 $H/scripts/cmd.py "$1" ${2:-8}; }
for mode in 1 0; do
  n0=$(wc -l < "$GL")
  echo "== $1 scans legacy $mode ($(date +%T)): $(c "scans legacy $mode")" >> $OUT
  sleep 2
  c "profile window" 60 >> $OUT
  c "camtime 240 6" >> $OUT
  sleep 21
  c "profile times 12" 20 >> $OUT
  sleep 1
  tail -n +$((n0+1)) "$GL" | grep -E "camtime (f|summary)|^  .*: [0-9]+ renders|profile times|ms/frame|perf 10s" | cut -c1-400 >> $OUT
done
c "profile off" >> $OUT
c "scans legacy 0" >> $OUT
echo "done $(date +%T)" >> $OUT
