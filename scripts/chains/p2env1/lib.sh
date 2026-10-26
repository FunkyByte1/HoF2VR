# shared helpers of env 1's p2env1 chains (sourced)
O=$H/logs/p2env1
mkdir -p "$O"
LOG=$H/logs/p2env1.log
G="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2"
GAMELOG="$G/BepInEx/LogOutput.log"
ST=/run/user/1000/vr-session/status.json
say() { echo "=== $* ($(date +%T))" | tee -a "$LOG"; }
userin() { grep -q '"user": true' "$ST" 2>/dev/null; }
state() { python3 scripts/cmd.py 'state?' 5 2>/dev/null; }
menu() { python3 scripts/cmd.py --status 2>/dev/null | python3 -c 'import sys, json; print(json.load(sys.stdin).get("menu", ""))' 2>/dev/null; }
table() { python3 scripts/session.py start --snapshot magician_mid --to table >> $O/sessions.out 2>&1; say "session at the table: $(state | cut -c1-50)"; }
hub() {
  python3 scripts/session.py start --snapshot magician --to table >> $O/sessions.out 2>&1
  python3 -c "
import sys, time; sys.path.insert(0, 'scripts'); import cmd as C, p2fix as P
for i in range(3):
    time.sleep(2); C.send('press A 6', 5)
    if P.wait_menu('ChallengeMap', 25): break
time.sleep(3)" >> $O/sessions.out 2>&1
  say "session at the side table: $(state | cut -c1-50) / $(menu)"
}
attable() { state | grep -q GameState_Map; }
ensure_table() { if attable && [ "$(menu)" = "Map" ]; then :; else table; fi; }
fails() { grep -E '^..:..:.. FAIL|RESULT .* FAIL' "$1" | cut -c1-300 | sed 's/^/    /' >> "$LOG"; }
t() { local name=$1 out=$2; shift 2; "$@" > "$out" 2>&1; local rc=$?; say "$name rc $rc | $(tail -1 "$out" | cut -c1-200)"; fails "$out"; }
exc() { say "$1: the game log's exceptions $(grep -c Exception "$GAMELOG" 2>/dev/null)"; }
guard() { if userin; then say "the user is in VR: stopping here"; scripts/stop.sh > /dev/null 2>&1; scripts/monado.sh stop > /dev/null 2>&1; exit 9; fi; }

start_chain() {
  : >> "$LOG"
  if userin; then say "the user is in VR - not starting"; exit 9; fi
  scripts/reserve.sh start $$ >> "$LOG" 2>&1
  trap 'scripts/stop.sh >> "$LOG" 2>&1; scripts/monado.sh stop >> "$LOG" 2>&1; scripts/reserve.sh stop >> "$LOG" 2>&1' EXIT
  trap 'exit 1' INT TERM
  say "P2ENV1 start $(git rev-parse --short HEAD), DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8)"
}
