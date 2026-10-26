#!/usr/bin/env bash
# env 3, the glove cut to HL:A's size (2026-10-20; docs/hand-size-hla.md): one turn of the game slot (<= 25 min) under
# reserved-env3. Steps (TURN_STEPS, in order):
#   before  candidate 11's DLL (build/before/HoF2VR-cand11.dll): handsize_test.py --tag before (table, fit, fight)
#   after   this build (build/bin): handsize_test.py --tag after (table, fit, fight)
#   tune    `hands back` candidates: the numbers at the table, the artefact badge in a fight (Body and Setting): --parts tunetable,tunefight
#   after2  the rebuilt DLL's table, fit, fight pictures (--tag after2)
#   size    the player's size at the table, 1.06 (the old ReachBoost) and 1.0: handsize_test.py --parts size,encounter
#   hub     the same at the caravan's side table (a session at the caravan): --parts hub
#   interactab  interact_test's first group twice: A the laser tap from under the table (IT_NOPOINT=1), B from over it
#   p4e3    env 3's p4e3_test crit,angles + posts,ground,cling (candidate 11's fails)
#   interact, fixtests, hud, p4f  the regression blocks of chain_cand11.sh (B_interact, its fixtests = the hand check's
#           slider; B_hud's hud_test; B_p4f's p4_test fight: the badges) on this build
# Run unsandboxed: nohup setsid bash scripts/chains/hands/turn.sh > /dev/null 2>&1 &   (TURN_STEPS="after interact")
# Logs: logs/hands_turn.log, logs/hands/. Pictures: captures/hands-2026-10-20/.
LOG=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/hands_turn.log
O=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/hands
export NORESERVE=0
. "$(dirname "${BASH_SOURCE[0]}")/../hud/lib.sh"
STEPS=${TURN_STEPS:-before after}
BEFORE=$H/build/before/HoF2VR-cand11.dll
PLUG="$G3/BepInEx/plugins/HoF2VR/HoF2VR.dll"
T0=$(date +%s)
# the slot is free: no other environment's fresh reservation or running game (and no user)
for f in /run/user/1000/vr-session/reserved-env* /run/user/1000/vr-session/game-env* /run/user/1000/vr-session/reserved-envsom; do
  [ -e "$f" ] || continue
  case $f in *-env3) continue ;; esac
  if [ $(( $(date +%s) - $(stat -c %Y "$f") )) -lt 120 ]; then echo "=== $(date +%T) the slot is taken (${f##*/}) - not started" >> "$LOG"; exit 4; fi
done
userin && { echo "=== $(date +%T) the user is in VR - not started" >> "$LOG"; exit 5; }
mkdir -p "$O"
trap 'scripts/reserve.sh stop > /dev/null 2>&1' EXIT
trap 'exit 143' INT TERM
scripts/reserve.sh start $$ >> "$O/reserve.out" 2>&1
say "turn start ($STEPS): build/bin $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8), before $(sha1sum < "$BEFORE" | cut -c1-8), branch $(git rev-parse --short HEAD) (sched $(ps -o cls= -p $$))"
guard
scripts/monado.sh start >> "$O/monado.out" 2>&1
over() { [ $(( $(date +%s) - T0 )) -gt "${TURN_MAX:-1440}" ]; }
for s in $STEPS; do
  if over; then say "the turn's 24 minutes are used up - stopping before '$s'"; break; fi
  case $s in
    before)
      savelog; scripts/stop.sh > /dev/null 2>&1
      scripts/install.sh > /dev/null 2>&1
      install -m 644 "$BEFORE" "$(dirname "$PLUG")/.HoF2VR.dll.new" && mv -f "$(dirname "$PLUG")/.HoF2VR.dll.new" "$PLUG"
      python3 scripts/session.py start --snapshot magician_mid --to table --no-install >> "$O/sessions.out" 2>&1
      SLOT_SINCE=$(date +%s); say "session at the table (before): $(state | cut -c1-50) DLL $(sha1sum < "$PLUG" | cut -c1-8)"
      t "handsize before" $O/before.out python3 scripts/handsize_test.py --tag before --parts table,fit,fight
      leave ;;
    after)
      table
      t "handsize after" $O/after.out python3 scripts/handsize_test.py --tag after --parts table,fit,fight
      leave ;;
    tune)
      table
      t "handsize tunetable,tunefight (hands back candidates)" $O/tune.out python3 scripts/handsize_test.py --tag after --parts tunetable,tunefight
      leave ;;
    after2)
      table
      t "handsize after2" $O/after2.out python3 scripts/handsize_test.py --tag after2 --parts table,fit,fight
      leave ;;
    size)
      table
      t "handsize size + encounter (1.06 / 1.0)" $O/size.out python3 scripts/handsize_test.py --tag after --parts size,encounter ;;
    hub)
      savelog; scripts/stop.sh > /dev/null 2>&1
      python3 scripts/session.py start --snapshot magician --to table >> "$O/sessions.out" 2>&1
      SLOT_SINCE=$(date +%s); say "session at the caravan: $(state | cut -c1-50)"
      t "handsize hub (1.06 / 1.0)" $O/hub.out python3 scripts/handsize_test.py --tag after --parts hub ;;
    interact)
      table; t "interact figurine_pinch dice_reroll_vr card_flip" $O/interact.out python3 scripts/interact_test.py figurine_pinch dice_reroll_vr card_flip
      table; t "interact touch_mark dice_multi" $O/interact_dice.out python3 scripts/interact_test.py touch_mark dice_multi
      table; t "interact figurine" $O/interact_fig.out python3 scripts/interact_test.py figurine
      table; t "interact figurine_real" $O/interact_figreal.out python3 scripts/interact_test.py figurine_real ;;
    interactab)
      # the dice re-roll's laser tap marked nothing in turn 3 (6286715a, PlayerSize 1.0): A the old pose (the hand left under
      # the table, IT_NOPOINT=1), B the hand over the table (interact_test.point_over_table)
      table; t "interact A (the hand under the table) figurine_pinch dice_reroll_vr card_flip" $O/interact_a.out env IT_NOPOINT=1 python3 scripts/interact_test.py figurine_pinch dice_reroll_vr card_flip
      table; t "interact B (the hand over the table) figurine_pinch dice_reroll_vr card_flip" $O/interact_b.out python3 scripts/interact_test.py figurine_pinch dice_reroll_vr card_flip ;;
    interact2)
      table; t "interact figurine_drop" $O/interact_figdrop.out python3 scripts/interact_test.py figurine_drop
      table; t "interact figurine_drop_beside" $O/interact_figdrop_beside.out python3 scripts/interact_test.py figurine_drop_beside ;;
    fixtests)
      ensure_table; t "fixtests: sliders (hand check, VR settings), T6" $O/fixtests.out python3 scripts/chains/cand8/fixtests.py ;;
    handcheck)
      savelog; scripts/stop.sh > /dev/null 2>&1
      python3 scripts/session.py start --snapshot magician_mid >> "$O/sessions.out" 2>&1
      t "handcheck_test (at the title)" $O/handcheck.out python3 scripts/handcheck_test.py ;;
    hud)
      ensure_table; python3 scripts/cmd.py 'vrhud setting' 5 > /dev/null 2>&1; python3 scripts/cmd.py 'vrstatus setting' 5 > /dev/null 2>&1
      t "hud_test thieves: flat,board,body,shell,rings,switch" $O/hud.out python3 scripts/hud_test.py --tag HS --fixture thieves --equip $GEAR --stay --parts flat,board,body,shell,rings,switch
      leave ;;
    p4f)
      table; t "p4_test fight (the hand badges)" $O/p4_fight.out python3 scripts/p4_test.py --parts fight --pics $H/captures/hands-2026-10-20/p4
      leave ;;
    p4e3)
      # env 1's candidate 11 p4e3 fails (crit close / far, the gnome posts): the re-taken stand and the posts turned from the edge
      ensure_table; t "p4e3 crit,angles" $O/p4e3_crit.out python3 scripts/p4e3_test.py --tag HSP4E3c --stay --parts crit,angles
      savelog
      t "p4e3 posts,ground,cling" $O/p4e3_gnomes.out python3 scripts/p4e3_test.py --tag HSP4E3g --stay --parts posts,ground,cling
      leave ;;
    *) say "unknown step $s" ;;
  esac
  guard
done
savelog
say "exceptions in the game log: $(grep -c Exception "$G3/BepInEx/LogOutput.log" 2>/dev/null)"
stopall
say "turn end ($(( ($(date +%s) - T0) / 60 )) min)"
