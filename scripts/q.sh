#!/usr/bin/env bash
# q.sh "<command>" [wait_s]: run a mod command (cmd.py) and print what it logged (new LogOutput.log lines, perf lines left out)
. "$(dirname "${BASH_SOURCE[0]}")/hof2env.sh"   # this worktree's environment (docs/two-environments.md)
G="$G/BepInEx/LogOutput.log"
n=$(wc -l < "$G")
r=$(python3 "$H/scripts/cmd.py" "$1" 20)
sleep "${2:-1.5}"
echo "=> $r"
tail -n +"$((n+1))" "$G" | grep -v -E "perf 10s|perf stall|\[cmd\]" | cut -c1-"${Q_WIDTH:-400}"
