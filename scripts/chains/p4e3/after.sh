#!/usr/bin/env bash
# env 3's turn in the rotation (the coordinator's order 2026-10-20 00:04: env 1, then env 2, then env 3): wait until env 2
# has had its turn (its reservation seen, then gone) - or the slot has stood free for 6 minutes - then run chain.sh with
# these blocks (it takes the slot as soon as no other reservation is fresh and the user is out of VR).
#   NAME=p4e3c2 nohup setsid bash scripts/chains/p4e3/after.sh T A B C > /dev/null 2>&1 &
V=/run/user/1000/vr-session
fresh() { [ -e "$1" ] && [ $(( $(date +%s) - $(stat -c %Y "$1") )) -lt 120 ]; }
anyfresh() { local f; for f in $V/reserved-env*; do case "$f" in *env3) continue ;; esac; fresh "$f" && return 0; done; return 1; }
seen2=0; free_since=""
while :; do
  fresh $V/reserved-env2 && seen2=1
  [ "$seen2" = 1 ] && ! fresh $V/reserved-env2 && break
  if anyfresh; then free_since=""; else [ -z "$free_since" ] && free_since=$(date +%s); [ $(( $(date +%s) - free_since )) -ge 360 ] && break; fi
  sleep 15
done
exec bash "$(dirname "$0")/chain.sh" "$@"
