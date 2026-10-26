#!/usr/bin/env bash
# ft.sh LABEL - 450-frame histogram via the mod; prints avg ms and the share of frames over 13 ms
. "$(dirname "${BASH_SOURCE[0]}")/hof2env.sh"   # H, G: this worktree's environment (docs/two-environments.md)
n0=$(grep -c "frame times over" "$G/BepInEx/LogOutput.log")
python3 $H/scripts/cmd.py "frametimes 450" >/dev/null
for i in $(seq 1 40); do sleep 0.5; [ "$(grep -c 'frame times over' "$G/BepInEx/LogOutput.log")" -gt "$n0" ] && break; done
awk -v want=$((n0+1)) -v lbl="$1" '/frame times over/{k++; if(k==want){on=1; split($0,a,"avg "); avg=a[2]; next}} on && /ms: /{split($0,b,"ms: "); ms=b[1]+0; c=b[2]+0; tot+=c; if(ms>13) slow+=c} on && !/ms: / && !/frame times/{on=0} END{printf "%-28s avg %s  slow(>13ms) %.1f%%\n", lbl, avg, 100*slow/tot}' "$G/BepInEx/LogOutput.log"
