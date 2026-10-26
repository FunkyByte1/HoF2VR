#!/usr/bin/env bash
# The user's second Steam restart (2026-10-17 ~19:25, "one more ... no rush, just notify me"), between two blocks of
# candidate 8b's chain: wait for the running chain (pid $1) to finish B_mine, stop it (explicit PIDs), restart Steam in a
# clean environment, wait for its log-on, then start the chain again with the blocks that were left.
set -u
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
CH=$1
CLOG=$H/logs/chain_8b_2.log
OUT=$H/logs/steam_restart_8b.log
CONN=/home/bara/.local/share/Steam/logs/connection_log.txt
say() { echo "=== $* ($(date +%T))" >> "$OUT"; }
tp() { echo "$1"; local c; for c in $(pgrep -P "$1"); do tp "$c"; done; }

say "waiting for B_mine to finish in $CLOG (chain pid $CH)"
until grep -q "B_mine done" "$CLOG"; do
  kill -0 "$CH" 2>/dev/null || { say "the chain is gone before B_mine finished - stopping here"; exit 2; }
  sleep 0.5
done
ALL=$(tp "$CH" | tr '\n' ' ')
kill -STOP $ALL 2>/dev/null; kill -TERM $ALL 2>/dev/null; kill -CONT $ALL 2>/dev/null
say "chain held after B_mine: TERM to $ALL"
sleep 5
scripts/stop.sh >> "$OUT" 2>&1
scripts/monado.sh stop >> "$OUT" 2>&1
scripts/reserve.sh stop >> "$OUT" 2>&1
say "games left: $(pgrep -x 'Hand of Fate 2.' | tr '\n' ' ')(none = stopped)"

# Steam: a clean shutdown, then a start outside this shell's environment (T3's AppImage libraries)
T0=$(date '+%Y-%m-%d %H:%M:%S')
env -u LD_LIBRARY_PATH /usr/bin/steam -shutdown >> "$OUT" 2>&1
for i in $(seq 1 120); do pgrep -x steam > /dev/null || pgrep -x steamwebhelper > /dev/null || break; sleep 1; done
if pgrep -x steam > /dev/null; then say "steam still running after 120 s - TERM to $(pgrep -x steam | tr '\n' ' ')"; kill -TERM $(pgrep -x steam); sleep 10; fi
say "steam stopped: $(pgrep -x steam | tr '\n' ' ')(none = stopped)"
XA=$(ls -t /run/user/1000/xauth_* | head -1)
systemd-run --user --collect --unit=steam-restart-$(date +%H%M) -E DISPLAY=:0 -E WAYLAND_DISPLAY=wayland-0 \
  -E XDG_RUNTIME_DIR=/run/user/1000 -E XAUTHORITY="$XA" /usr/bin/steam -silent >> "$OUT" 2>&1
OK=
for i in $(seq 1 180); do
  if pgrep -x steam > /dev/null && awk -v t="[$T0]" '$0 >= t' "$CONN" 2>/dev/null | grep -q 'Logged On.*processing complete'; then OK=1; break; fi
  sleep 1
done
if [ -z "$OK" ]; then say "STEAM NOT LOGGED ON after 3 min - the chain is NOT restarted"; exit 3; fi
say "steam back: pid $(pgrep -x steam | tr '\n' ' ')- $(awk -v t="[$T0]" '$0 >= t' "$CONN" | grep 'Logged On.*processing complete' | tail -1 | cut -c1-21)"

# the chain again, with the blocks that were left
CAND8_WANT=8af22b737a802b9128cef75d77d0af0e669b96cf CAND8_O=logs/cand8b CAND8_PICS=$H/captures/cand8b \
  CAND8_LOG=$H/logs/chain_8b_3.log CAND8_BLOCKS="B_n1 B_limbo B_walk" \
  nohup setsid bash scripts/chains/chain_cand8.sh > "$H/logs/chain8b_3.nohup" 2>&1 < /dev/null &
say "chain restarted (pid $!): B_n1 B_limbo B_walk -> logs/chain_8b_3.log"
