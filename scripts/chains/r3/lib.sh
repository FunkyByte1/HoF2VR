# env 2 round 3 (2026-10-17): the chain helpers (round 2 lib.sh)
R3=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
H=/home/bara/Projects/Flat2VR-env2/HandOfFate2
cd "$H" || exit 1
. "$R3/env.sh"
GEAR="BasicSword_noToken,BasicShield"
ST=/run/user/1000/vr-session/status.json
say() { echo "=== $* ($(date +%T))" >> "$LOG"; }
stopall() { scripts/stopauto.sh > /dev/null 2>&1; scripts/stop.sh > /dev/null 2>&1; scripts/monado.sh stop > /dev/null 2>&1; }
userin() { grep -q '"user": true' "$ST" 2>/dev/null; }
gpufault() { journalctl -k -b --no-pager 2>/dev/null | grep -qiE 'GPU reset|VRAM is lost|amdgpu.*page fault'; }
guard() {
  if gpufault; then say "GPU fault in the kernel log - stopping everything, chain ends"; stopall; exit 3; fi
  if userin; then
    say "the user is in VR - game stopped, waiting"; scripts/stopauto.sh > /dev/null 2>&1; scripts/stop.sh > /dev/null 2>&1
    while userin; do sleep 30; done; say "the user left VR - going on"
  fi
}
state() { python3 scripts/cmd.py 'state?' 5 2>/dev/null; }
menu() { python3 scripts/cmd.py --status 2>/dev/null | python3 -c 'import sys, json; print(json.load(sys.stdin).get("menu", ""))' 2>/dev/null; }
savelog() { cp "/home/bara/Games/hof2-env2/Hand of Fate 2/BepInEx/LogOutput.log" "$O/LogOutput-$(date +%H%M%S).log" 2>/dev/null; }
table() { savelog; python3 scripts/session.py start --snapshot magician_mid --to table >> "$O/sessions.out" 2>&1; say "session at the table: $(state | cut -c1-50) DLL $(sha1sum < "/home/bara/Games/hof2-env2/Hand of Fate 2/BepInEx/plugins/HoF2VR/HoF2VR.dll" | cut -c1-8)"; }
leave() { python3 -c "import sys; sys.path.insert(0, 'scripts'); import indicator_shots; print('left:', indicator_shots.leave())" >> "$O/leave.out" 2>&1; }
# NORESERVE=1 (the coordinator, 2026-10-18 05:05: interleave with env 1): no chain-long reservation; before each block,
# while another environment waits for the slot or reserves it (its waiting-envN / reserved-envN under 30 s old), this
# game stops so that environment gets its turn; the next block's session waits for it in run.sh (fair turns)
NORESERVE=${NORESERVE:-0}
others_waiting() {
  local f n
  for f in /run/user/1000/vr-session/waiting-env* /run/user/1000/vr-session/reserved-env*; do
    [ -e "$f" ] || continue
    n=${f##*-env}; [ "$n" = 2 ] && continue
    [ $(( $(date +%s) - $(stat -c %Y "$f") )) -lt 30 ] && return 0
  done
  return 1
}
yield_slot() {
  if [ "$NORESERVE" = 1 ] && others_waiting; then
    say "another environment waits for the slot - this game stops, the next session waits its turn"
    savelog; scripts/stopauto.sh > /dev/null 2>&1; scripts/stop.sh > /dev/null 2>&1
  fi
}
ensure_table() {
  guard
  yield_slot
  if state | grep -q Level_Play; then leave; sleep 3; fi
  if state | grep -q GameState_Map && [ "$(menu)" = "Map" ]; then :; else say "ensure_table: $(state | cut -c1-40), menu '$(menu)' - a fresh session"; table; fi
}
t() { local name=$1 out=$2; shift 2; guard; "$@" > "$out" 2>&1; local rc=$?
  say "$name rc $rc | $(tail -1 "$out" | cut -c1-150)"; grep -E '^..:..:.. FAIL' "$out" | cut -c1-600 | sed 's/^/    /' >> "$LOG"; }
begin() {
  mkdir -p "$O"
  trap 'scripts/reserve.sh stop > /dev/null 2>&1' EXIT
  trap 'exit 143' INT TERM
  if [ "$NORESERVE" = 1 ]; then say "no reservation (NORESERVE=1): the slot by fair turns, yielding between blocks"
  else scripts/reserve.sh start $$ >> "$O/reserve.out" 2>&1; fi
  say "chain start: DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8) (sched $(ps -o cls= -p $$))"
  guard
  scripts/monado.sh start >> "$O/monado.out" 2>&1
  table
}
finish() {
  say "summary:"
  grep -hE '^..:..:.. (PASS|FAIL) ' "$O"/*.out | awk '{sub(":", "", $3); print $3, $2}' | sort | uniq -c >> "$LOG"
  savelog
  say "exceptions in the saved game logs: $(cat "$O"/LogOutput-*.log 2>/dev/null | grep -c Exception)"
  stopall
  say "chain end"
}
