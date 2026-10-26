#!/usr/bin/env bash
# Env 1's regression batch after the second playtest's table fixes (P2-1..P2-8, 2026-10-18): the A/B checks of P2-2, P2-3
# and P2-8, interact_test, the tours (standing, seated), b1 --start and the fix tests. Holds the game slot (reserve.sh) for
# its own PID; stops and waits while the user is in VR (status.json "user": true). Log: logs/p2env1.log (blocks: $BLOCKS).
set -u
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
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

B_ab() {
  table
  python3 scripts/cmd.py "sset TouchInput CubeFaces false" >> "$LOG"; python3 scripts/cmd.py "sset FigurineGrab NearMemory 0" >> "$LOG"
  t "A/B old: figurine_real dice_multi (expected to fail)" $O/ab_old.out python3 scripts/interact_test.py figurine_real dice_multi
  python3 scripts/cmd.py "sset TouchInput CubeFaces true" >> "$LOG"; python3 scripts/cmd.py "sset FigurineGrab NearMemory 0.5" >> "$LOG"
  table
  t "A/B new: figurine_real dice_multi" $O/ab_new.out python3 scripts/interact_test.py figurine_real dice_multi
  exc "A/B"
}
B_pin() {
  hub
  t "P2-8 pin (new)" $O/pin_new.out python3 scripts/p2fix.py pin P2-8_pin_new
  python3 scripts/cmd.py "sset TouchInput PoleTops false" >> "$LOG"
  t "P2-8 pin (old: expected to fail)" $O/pin_old.out python3 scripts/p2fix.py pin P2-8_pin_old
  python3 scripts/cmd.py "sset TouchInput PoleTops true" >> "$LOG"
  t "P2-8 pin (new again)" $O/pin_new2.out python3 scripts/p2fix.py pin P2-8_pin_new2
  exc "pin"
}
B_interact() {
  table
  t "interact touch_mark figurine_pinch dice_reroll_vr card_flip" $O/interact.out python3 scripts/interact_test.py touch_mark figurine_pinch dice_reroll_vr card_flip
  table
  t "interact figurine (a fresh map)" $O/interact_fig.out python3 scripts/interact_test.py figurine
  exc "interact"
}
B_tour() { python3 scripts/playtest.py > $O/tour.out 2>&1; say "tour standing rc $? | $(grep '====' $O/tour.out | tail -1 | cut -c1-60) | FAILs $(grep -c '^..:..:.. FAIL' $O/tour.out)"; fails $O/tour.out; exc "tour"; }
B_tour_seated() { python3 scripts/playtest.py --seated > $O/tour_seated.out 2>&1; say "tour seated rc $? | $(grep '====' $O/tour_seated.out | tail -1 | cut -c1-60) | FAILs $(grep -c '^..:..:.. FAIL' $O/tour_seated.out)"; fails $O/tour_seated.out; exc "tour seated"; }
B_b1() { t "b1 --start" $O/b1.out python3 scripts/b1_test.py --start; exc "b1"; }
B_fix() { ensure_table; t "fixtests: sliders, T6" $O/fixtests.out python3 scripts/chains/cand8/fixtests.py; exc "fixtests"; }

: >> "$LOG"
if userin; then say "the user is in VR - not starting"; exit 9; fi
scripts/reserve.sh start $$ >> "$LOG" 2>&1
trap 'scripts/stop.sh >> "$LOG" 2>&1; scripts/monado.sh stop >> "$LOG" 2>&1; scripts/reserve.sh stop >> "$LOG" 2>&1' EXIT
trap 'exit 1' INT TERM
say "P2ENV1 start $(git rev-parse --short HEAD), DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8)"
for b in ${BLOCKS:-B_ab B_pin B_interact B_tour B_tour_seated B_b1 B_fix}; do
  guard
  say "block $b"
  $b
done
t "savecheck" $O/savecheck.out bash scripts/savecheck.sh
say "P2ENV1 done"
