#!/usr/bin/env bash
# Env 1's follow-up batch (2026-10-18 ~02:15): b1 --start with the watchdog/fight-card fixes, the tour's menu + devkeys stages,
# the seated tour's table stage (the inventory's controls column), a pin pressed right after arriving, P2-2's A/B against
# candidate 8b's DLL at a challenge's opening, and the picture walk at the card table. Log: logs/p2env1.log.
set -u
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
. scripts/chains/p2env1/lib.sh
P="$G/BepInEx/plugins/HoF2VR/HoF2VR.dll"
B_b1() { t "b1 --start" $O/b1b.out python3 scripts/b1_test.py --start; exc "b1"; }
B_menu() { python3 scripts/playtest.py --only menu,devkeys > $O/tour_menu.out 2>&1; say "tour menu,devkeys rc $? | $(grep '====' $O/tour_menu.out | tail -1 | cut -c1-60)"; fails $O/tour_menu.out; }
B_seated_table() { python3 scripts/playtest.py --seated --only table > $O/tour_seated_table.out 2>&1; say "seated table stage rc $? | $(grep '====' $O/tour_seated_table.out | tail -1 | cut -c1-60)"; fails $O/tour_seated_table.out; grep 'prompts_clear' $O/tour_seated_table.out | cut -c1-200 >> "$LOG"; }
B_pin_fresh() { hub; sleep 5; t "P2-8 pin (first tap after arriving)" $O/pin_fresh.out python3 scripts/p2fix.py pin P2-8_pin_fresh; }
setup() {
  python3 -c "
import sys, time; sys.path.insert(0, 'scripts'); import cmd as C, p2fix as P
for k, w in (('A', 3), ('Y', 3), ('A', 2), ('X', 3)): C.send('press %s 6' % k, 5); time.sleep(w)
print('setup', P.wait_menu('ChallengeSetup', 40)); time.sleep(8)
s = C.status(); print(s.get('state'), s.get('menu'))" >> $O/sessions.out 2>&1
  say "at the challenge's opening: $(state | cut -c1-50) / $(menu)"
}
B_ab_fig() {
  python3 scripts/session.py stop >> $O/sessions.out 2>&1
  cp work/p2dll/old8b.dll "$P"; say "plugins = candidate 8b's DLL $(sha1sum < "$P" | cut -c1-8)"
  python3 scripts/session.py start --no-install --snapshot magician --to table >> $O/sessions.out 2>&1
  python3 -c "
import sys, time; sys.path.insert(0, 'scripts'); import cmd as C, p2fix as P
for i in range(3):
    time.sleep(2); C.send('press A 6', 5)
    if P.wait_menu('ChallengeMap', 25): break
time.sleep(3)" >> $O/sessions.out 2>&1
  setup
  t "P2-2 A/B: 8b's DLL, figurine_real at the challenge's opening (expected to fail)" $O/fig8b.out python3 scripts/interact_test.py figurine_real
  hub; setup
  say "plugins = build/bin $(sha1sum < "$P" | cut -c1-8)"
  t "P2-2 A/B: this build, figurine_real at the challenge's opening" $O/fignew.out python3 scripts/interact_test.py figurine_real
}
B_walk_table() { table; t "walk: the card table, the head moving" $O/walk_table.out python3 scripts/p2fix.py walk P2-final_table; }
start_chain
# a copy of each session's game log (run.sh deletes a test session's log at the next start)
( while kill -0 $$ 2>/dev/null; do f="$GAMELOG"; [ -f "$f" ] && cp "$f" "$O/game_$(stat -c %i "$f").log" 2>/dev/null; sleep 15; done ) > /dev/null 2>&1 &
for b in ${BLOCKS:-B_b1 B_menu B_seated_table B_pin_fresh B_ab_fig B_walk_table}; do guard; say "block $b"; $b; done
t "savecheck" $O/savecheck2.out bash scripts/savecheck.sh
say "P2ENV1 follow-up done"
