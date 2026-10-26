#!/usr/bin/env bash
# Candidate 9's chain (env 1, 2026-10-18): the exact DLL in build/bin (841f5770, main c469e9f = env 1's P2-1..P2-8 + branch
# gnomes: P2-9..P2-11, F8 the gnome fight, G's redbar fix), under one reservation. Blocks: the uyf scan and savecheck; pre
# (N2 edge turn, N3 swarm boss); the fights 2 rounds (chain_pf's blocks as merged on main: A B C D F G P N) and focus_test;
# the tours standing and seated; env 1's tests (b1, seated_check, switches_test, the playtest fixes: figurine pinch and
# set-down, dice re-roll by tapping, sliders, and the P2 checks: p2fix still attract with the game frozen, pin, hubwalk,
# figurine_real at the challenge's opening); N1's voice in the Steam runtime; limbo_test; the picture walk with the head
# moving (captures/cand9/: the title and the table's places, the side table, then a fight each of thieves, soldiers,
# gnomes, bombs and a boss).
# The user always wins: a watcher stops the running block, the game and Monado and releases the reservation as soon as
# status.json says "user": true; the chain waits until it is false, reserves again and reruns the block. (No cut-off time.)
# A GPU fault (amdgpu reset / page fault / ring timeout in the kernel log) stops everything at once and ends the chain.
# Run unsandboxed: nohup setsid bash scripts/chains/chain_cand9.sh > /dev/null 2>&1 &
set -u
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
T=/tmp/claude-hof2
mkdir -p $T ${CAND9_PICS:-captures/cand9}
LOG=${CAND9_LOG:-$H/logs/chain_cand9.log}
WANT=${CAND9_WANT:-841f5770a31650afd33e1951e50655f805e5a196}
G="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2"
GAMELOG="$G/BepInEx/LogOutput.log"
CFG="$G/BepInEx/config/flat2vr.hof2vr.cfg"
ST=/run/user/1000/vr-session/status.json
MARK=$T/cand9_user_in
BPID=$T/cand9_block.pid
GEAR="BasicSword_noToken,BasicShield"
P="$G/BepInEx/plugins/HoF2VR/HoF2VR.dll"
export XAUTHORITY=${XAUTHORITY:-$(ls -t /run/user/1000/xauth_* | head -1)}
export HOF2VR_RESERVE_MAX_H=8
say() { echo "=== $* ($(date +%T))" | tee -a "$LOG"; }
sha() { sha1sum < "$1" | cut -d' ' -f1; }
treepids() { local c; echo "$1"; for c in $(pgrep -P "$1"); do treepids "$c"; done; }
killtree() { local all; all=$(treepids "$1"); kill -STOP $all 2>/dev/null; kill -TERM $all 2>/dev/null; kill -CONT $all 2>/dev/null; }
userseen() { grep -q '"user": true' "$ST" 2>/dev/null || [ ! -f "$ST" ] || [ $(( $(date +%s) - $(stat -c %Y "$ST") )) -gt 300 ]; }
userin() { userseen && { sleep 2; userseen; }; }
watcher() {
  while sleep 3; do
    if [ ! -f $MARK ] && userin; then
      date +%s > $MARK
      [ -f $BPID ] && killtree "$(cat $BPID)"
      scripts/stop.sh > /dev/null 2>&1
      scripts/monado.sh stop > /dev/null 2>&1
      scripts/reserve.sh stop > /dev/null 2>&1
      echo "=== the user is in VR: the block stopped, the game and Monado stopped, the reservation released ($(date +%T))" >> "$LOG"
    fi
  done
}
waitout() {
  if userin || [ -f $MARK ]; then
    while userin; do sleep 60; done
    sleep 60
    while userin; do sleep 60; done
    say "the user is out of VR: resuming"
    scripts/reserve.sh start $$ >> "$LOG" 2>&1
  fi
}
GPUPAT='amdgpu.*(page fault|timeout|wedged|reset|VRAM is lost)|GPU reset|ring .* timeout'
gpu() {
  local n; n=$(journalctl -k --since "@$1" --no-pager 2>/dev/null | grep -ciE "$GPUPAT")
  [ "$n" = 0 ] && return 0
  say "GPU FAULT: $n lines in the kernel log during $2: $(journalctl -k --since "@$1" --no-pager 2>/dev/null | grep -iE "$GPUPAT" | head -3 | cut -c1-200 | tr '\n' ' ')"
  say "GPU FAULT: stopping all testing (the game, Monado) and ending the chain - tell the coordinator, reboot before VR"
  scripts/stop.sh > /dev/null 2>&1; scripts/monado.sh stop > /dev/null 2>&1
  touch $T/cand9_gpu_fault
  return 3
}
run_block() {
  local name=$1 n=0 rc t0; shift
  while :; do
    waitout
    rm -f $MARK
    say "block $name"
    t0=$(date +%s)
    ( "$@" ) &
    echo $! > $BPID
    wait $!; rc=$?
    rm -f $BPID
    gpu $t0 "$name" || return 3
    if [ -f $MARK ]; then
      n=$((n+1)); say "block $name: interrupted by the user (try $n)"
      [ $n -ge 3 ] && { say "block $name: given up after 3 interruptions"; return 99; }
      continue
    fi
    return $rc
  done
}
state() { python3 scripts/cmd.py 'state?' 5 2>/dev/null; }
inafight() { state | grep -q Level_Play; }
attable() { state | grep -q GameState_Map; }
savelog() { cp "$GAMELOG" "$O/LogOutput-$(date +%H%M%S).log" 2>/dev/null; }
table() { savelog; python3 scripts/session.py start --snapshot magician_mid --to table >> logs/cand9/sessions.out 2>&1; say "session at the table: $(state | cut -c1-50)"; }
menu() { python3 scripts/cmd.py --status 2>/dev/null | python3 -c 'import sys, json; print(json.load(sys.stdin).get("menu", ""))' 2>/dev/null; }
# at the table with the map ready (no encounter or activation left open: seated_check leaves one, and the fixtures' jump
# is refused until it is finished - candidate 7's first run, 11:04-11:14) - else a fresh session
ensure_table() { if attable && [ "$(menu)" = "Map" ]; then :; else say "ensure_table: $(state | cut -c1-40), menu '$(menu)' - a fresh session"; table; fi; }
leave() { python3 -c "import sys; sys.path.insert(0, 'scripts'); import indicator_shots; print('left:', indicator_shots.leave())" >> logs/cand9/leave.out 2>&1; }
score() { tail -1 "$1" | grep -oE '[0-9]+/[0-9]+' | head -1; }
below() { local s; s=$(score "$1"); [ -z "$s" ] || [ "${s%/*}" -lt "$2" ]; }
gaveup() { tail -n 6 "$1" | grep -q 'FAIL fixture'; }
fails() { grep -E '^..:..:.. FAIL' "$1" | cut -c1-300 | sed 's/^/    /' >> "$LOG"; }
res() { local name=$1 rc=$2 out=$3; say "$name rc $rc | $(tail -1 "$out" | cut -c1-160)"; fails "$out"; }
t() { local name=$1 out=$2; shift 2; "$@" > "$out" 2>&1; res "$name" $? "$out"; }
exc() { say "$1: the game log's exceptions $(grep -c Exception "$GAMELOG"); plugins DLL $(sha "$G/BepInEx/plugins/HoF2VR/HoF2VR.dll" | cut -c1-8)"; }
tourres() { say "$1 | $(grep '====' "$2" | tail -1 | cut -c1-40) | FAILs $(grep -c '^..:..:.. FAIL' "$2")"; fails "$2"; exc "$1"; }
keepshots() { mkdir -p testdata/shots/$1; find testdata/shots -maxdepth 1 -name 'PT_*' -newer "$2" -exec cp -p {} testdata/shots/$1/ \; ; say "shots kept: testdata/shots/$1 ($(ls testdata/shots/$1 | wc -l))"; }

# ---- the blocks ----
O=${CAND9_O:-logs/cand9}
mkdir -p $O
putdll() { install -m 644 "$1" "$(dirname "$P")/.HoF2VR.dll.new" && mv -f "$(dirname "$P")/.HoF2VR.dll.new" "$P"; }
# before each block: build/bin is still the candidate, and so is the installed DLL (else put back, and the next block's
# session boots it: the running game stops)
dllcheck() {
  [ "$(sha build/bin/HoF2VR.dll)" = "$WANT" ] || { say "build/bin is not the candidate any more ($(sha build/bin/HoF2VR.dll | cut -c1-8)) - stopping"; return 8; }
  if [ -f "$P" ] && [ "$(sha "$P")" != "$WANT" ]; then
    say "the installed DLL is $(sha "$P" | cut -c1-8), not the candidate: put back, the game stopped"
    savelog; scripts/stop.sh > /dev/null 2>&1; putdll build/bin/HoF2VR.dll
  fi
  return 0
}
B_scan() {
  t "uyf_scan" $O/uyf_scan.out python3 scripts/uyf_scan.py
  t "savecheck before" $O/savecheck1.out bash scripts/savecheck.sh
}
B_pre() {
  table
  t "pf pre: N2 edgeturn, N3 swarmboss (F8 probe)" $O/pre.out bash scripts/chains/cand9/pre.sh
  grep -E '(PASS|FAIL) ' $O/pre.out | cut -c1-260 | sed 's/^/    /' >> "$LOG"
  leave
  exc "pre"
}
# chain_pf's blocks (scripts/chains/pf/chain_pf.sh as merged on main), one round
B_fights() {
  local i=$1
  ensure_table; t "r$i A shadows leadhold,combogaps" $O/A$i.out python3 scripts/audit_test.py --tag C9${i}A --fixture shadows --equip $GEAR --stay --parts leadhold,combogaps
  ensure_table; t "r$i B thieves waiting,waiting,dash,strafe,health" $O/B$i.out python3 scripts/audit_test.py --tag C9${i}B --fixture thieves --equip $GEAR --stay --parts waiting,waiting,dash,strafe,health
  ensure_table; t "r$i C thieves stage1 block x3" $O/C$i.out python3 scripts/stage1_test.py --tag C9${i}C --fixture thieves --equip $GEAR --stay --parts block,block,block
  if inafight; then t "r$i C move_test (same fight)" $O/M$i.out python3 scripts/move_test.py --tag C9${i}M --no-fixture; else say "r$i move_test: the fight is over"; fi
  ensure_table; t "r$i D combo, interruptcut (combo fixture)" $O/D$i.out python3 scripts/stage2_test.py --tag C9${i}D --fixture combo --equip $GEAR --stay --parts combo,interruptcut
  ensure_table; t "r$i F thieves linecut,crit,wrong,stickuse" $O/F$i.out python3 scripts/stage2_test.py --tag C9${i}F --fixture thieves --equip $GEAR --stay --parts linecut,crit,wrong,stickuse
  ensure_table; t "r$i G dodge fixture redbar,redlean" $O/G$i.out python3 scripts/stage1_test.py --tag C9${i}G --fixture dodge --equip $GEAR --stay --parts redbar,redlean
  ensure_table; t "r$i P lobs knock,drop,dodge,offview" $O/P$i.out python3 scripts/bomb_test.py --tag C9${i}P --stay --parts knock,drop,dodge,offview
  ensure_table; t "r$i N gnomes watch,cut,dash,cling" $O/N$i.out python3 scripts/gnome_test.py --tag C9${i}N --stay --parts watch,cut,dash,cling
  exc "fights round $i"
}
B_fights1() { B_fights 1; }
B_fights2() { B_fights 2; }
B_focus() {
  savelog
  t "E focus_test (autoplay's walk from a fresh table)" $O/E.out python3 scripts/focus_test.py
  exc "focus"
}
B_tour() {
  touch $T/cand9_t0
  savelog
  python3 scripts/playtest.py > $O/tour.out 2>&1; tourres "tour standing rc $?" $O/tour.out
  say "tour standing PROXIMITY lines: $(grep -c PROXIMITY "$GAMELOG")"
  keepshots cand9_standing $T/cand9_t0
}
B_tour_seated() {
  touch $T/cand9_t1
  savelog
  python3 scripts/playtest.py --seated > $O/tour_seated.out 2>&1; tourres "tour seated rc $?" $O/tour_seated.out
  say "seated reach: $(grep 'seated_touch_within_reach' $O/tour_seated.out | tail -1 | cut -c10-160)"
  say "tour seated PROXIMITY lines: $(grep -c PROXIMITY "$GAMELOG")"
  keepshots cand9_seated $T/cand9_t1
}
# a session at the caravan (the magician save: GameState_Attract)
hub() { savelog; python3 scripts/session.py start --snapshot magician --to table >> $O/sessions.out 2>&1; say "session at the caravan: $(state | cut -c1-50) / $(menu)"; }
p2res() { say "$1 | $(grep -E 'RESULT' "$2" | cut -c10-200 | tr '\n' ' ' | cut -c1-400)"; grep -E 'RESULT .* FAIL' "$2" | cut -c1-300 | sed 's/^/    /' >> "$LOG"; }
B_mine() {
  ensure_table
  t "b1 --start standing" $O/b1.out python3 scripts/b1_test.py --start
  t "b1 --start --seated" $O/b1_seated.out python3 scripts/b1_test.py --start --seated
  t "seated_check" $O/seated_check.out python3 scripts/seated_check.py
  t "switches_test --start (a fresh session)" $O/switches.out python3 scripts/switches_test.py --start
  exc "b1 / seated / switches"
  table
  t "interact figurine_pinch dice_reroll_vr card_flip" $O/interact.out python3 scripts/interact_test.py figurine_pinch dice_reroll_vr card_flip
  table
  t "interact touch_mark dice_multi (P2-3: taps from above)" $O/interact_dice.out python3 scripts/interact_test.py touch_mark dice_multi
  table
  t "interact figurine (a fresh map: pick up, set down)" $O/interact_fig.out python3 scripts/interact_test.py figurine
  table
  t "interact figurine_real (P2-2, on the map)" $O/interact_figreal.out python3 scripts/interact_test.py figurine_real
  ensure_table
  t "fixtests: sliders (hand check, VR settings), T6" $O/fixtests.out python3 scripts/chains/cand8/fixtests.py
  exc "playtest fixes"
  # the P2 checks at the side table (NOTES "Playtest 2 fixes (env 1)")
  hub
  python3 scripts/cmd.py "timescale 0" 5 >> "$LOG"
  python3 scripts/p2fix.py still attract_frozen > $O/p2_still.out 2>&1; p2res "P2-1 still attract (the game frozen: held cards and the head)" $O/p2_still.out
  python3 scripts/cmd.py "timescale 1" 5 >> "$LOG"
  python3 -c "
import sys, time; sys.path.insert(0, 'scripts'); import cmd as C, p2fix as P
for i in range(3):
    time.sleep(2); C.send('press A 6', 5)
    if P.wait_menu('ChallengeMap', 25): break
time.sleep(5)" >> $O/sessions.out 2>&1
  say "at the challenge map: $(state | cut -c1-40) / $(menu)"
  python3 scripts/p2fix.py pin P2-8_pin_c9 > $O/p2_pin.out 2>&1; p2res "P2-8 pin (a fingertip onto a pin opens its deck builder)" $O/p2_pin.out
  timeout 600 python3 scripts/p2fix.py hubwalk P2hub_c9 > $O/p2_hubwalk.out 2>&1; p2res "P2-4..P2-8 hubwalk (rc $?; the swipe test, the opening)" $O/p2_hubwalk.out
  grep -E 'no deck builder|no challenge setup|setup:|after Recommend|no inventory' $O/p2_hubwalk.out | cut -c1-240 | sed 's/^/    /' >> "$LOG"
  python3 scripts/chains/cand9/opening.py >> $O/p2_opening.out 2>&1
  say "challenge opening: $(tail -1 $O/p2_opening.out | cut -c1-200)"
  if state | grep -q ChallengeSetup; then
    t "interact figurine_real (P2-2, at the challenge's opening)" $O/interact_figreal_setup.out python3 scripts/interact_test.py figurine_real
  else say "figurine_real at the challenge's opening: not reached ($(state | cut -c1-40) / $(menu))"; fi
  exc "P2 side table"
}
B_n1() {
  savelog
  python3 scripts/session.py stop > /dev/null 2>&1
  bash scripts/chains/pt17_n1.sh > $O/n1.out 2>&1
  say "N1 voice in the Steam runtime: $(grep -E 'recorded from|no voice|no headset' $O/n1.out | tail -1 | cut -c1-140) | $(grep 'container:' $O/n1.out | cut -c1-140)"
  pactl list sources short 2>/dev/null | grep -q hof2vr_wivrn_test && say "N1: the stand-in source is still loaded!" || say "N1: the stand-in source is gone"
  exc "N1"
}
B_limbo() {
  savelog
  python3 scripts/limbo_test.py --snapshot first_fight --tag L9c > $O/limbo.out 2>&1; rc=$?
  say "limbo_test rc $rc | states: $(grep -oE 'GameState_[A-Za-z_]+' $O/limbo.out | uniq | tr '\n' ' ' | cut -c1-300)"
  grep -aE 'progress: fight_in done|gate: go' $O/limbo.out | tail -3 | cut -c1-200 | sed 's/^/    /' >> "$LOG"
  exc "limbo"
}
walkres() { grep -E 'HEADWALK' "$1" | cut -c10-400 | sed 's/^/    /' >> "$LOG"; }
B_walk() {
  savelog
  python3 scripts/session.py start --snapshot magician_mid >> $O/sessions.out 2>&1; say "session at the title: $(state | cut -c1-50)"
  t "walk: the title and the table's places" $O/walk.out timeout 2400 python3 scripts/chains/cand9/walk.py table
  walkres $O/walk.out
  exc "walk (table)"
  hub
  t "walk: the side table (challenge map, deck builder; the Adventure tab)" $O/walk_hub.out timeout 900 python3 scripts/chains/cand9/walk.py hub adventure
  walkres $O/walk_hub.out
  exc "walk (side table)"
  ensure_table
  t "walk: the fights (thieves, soldiers, gnomes, bombs, boss)" $O/fightpics.out timeout 2700 bash scripts/chains/cand9/fightpics.sh
  walkres $O/fightpics.out
  grep -E 'FAIL|gnome fight with' $O/fightpics.out | cut -c1-200 | sed 's/^/    /' >> "$LOG"
  say "pictures in ${CAND9_PICS:-captures/cand9}: $(ls ${CAND9_PICS:-captures/cand9} | wc -l)"
  exc "walk (fights)"
}
# after the first run (11:05-12:..): the side table's P2 checks in the screens' order (side.py), and two things from the
# tours' pictures (looks.sh: the goblin archers' fight, what stands near the player at the title)
B_side() {
  hub
  t "side table again: P2-8 pin, P2-5 swipe in Build, P2-2 figurine_real at the opening" $O/side.out timeout 900 python3 scripts/chains/cand9/side.py
  grep -E 'RESULT|PASS|FAIL|passed|->|opening:' $O/side.out | cut -c10-260 | sed 's/^/    /' >> "$LOG"
  exc "side table"
}
B_extra() {
  B_side
  table
  t "campfire before a fight (the head walk at READY)" $O/campfire.out timeout 400 python3 scripts/chains/cand9/campfire.py
  walkres $O/campfire.out
  table
  t "looks: the goblin archers' fight" $O/looks_archers.out timeout 400 bash scripts/chains/cand9/looks.sh archers
  exc "archers"
  savelog
  python3 scripts/session.py start --snapshot magician_mid >> $O/sessions.out 2>&1; say "session at the title: $(state | cut -c1-50)"
  t "looks: the title, turned left" $O/looks_title.out timeout 200 bash scripts/chains/cand9/looks.sh title
  grep 'turned left' $O/looks_title.out | cut -c1-600 | sed 's/^/    /' >> "$LOG"
}

# ---- the chain ----
: > "$LOG"
rm -f $T/cand9_gpu_fault
if userin; then say "the user is in VR - not starting"; exit 9; fi
[ "$(sha build/bin/HoF2VR.dll)" = "$WANT" ] || { say "build/bin is not the candidate - not starting"; exit 8; }
scripts/reserve.sh start $$ >> "$LOG" 2>&1
watcher & WPID=$!
trap 'kill $WPID 2>/dev/null; [ "$(sha "$P")" = "$WANT" ] || { putdll build/bin/HoF2VR.dll; say "plugins put back: $(sha "$P" | cut -c1-8)"; }; scripts/reserve.sh stop >> "$LOG" 2>&1; rm -f $BPID $MARK' EXIT
trap 'exit 1' INT TERM
CFG0=$(sha "$CFG" | cut -c1-12)
say "CAND9 start main $(git rev-parse --short HEAD) DLL ${WANT:0:8}; the player's cfg sha1 $CFG0; sched $(ps -o cls= -p $$)"
for b in ${CAND9_BLOCKS:-B_scan B_pre B_fights1 B_fights2 B_focus B_tour B_tour_seated B_mine B_n1 B_limbo B_walk}; do
  dllcheck || break
  run_block $b $b
  rc=$?
  say "$b done rc $rc"
  [ -f $T/cand9_gpu_fault ] && { say "the chain ends: GPU fault"; break; }
done
savelog
python3 scripts/session.py stop > /dev/null 2>&1
sleep 3
say "game stopped: $(pgrep -x 'Hand of Fate 2.' | tr '\n' ' ')(none = stopped)"
scripts/monado.sh stop >> "$LOG" 2>&1
t "savecheck after" $O/savecheck2.out bash scripts/savecheck.sh
[ "$(sha "$P")" = "$WANT" ] || { putdll build/bin/HoF2VR.dll; say "plugins put back: $(sha "$P" | cut -c1-8)"; }
say "exceptions in the saved game logs: $(cat $O/LogOutput-*.log 2>/dev/null | grep -c Exception) (files $(ls $O/LogOutput-*.log 2>/dev/null | wc -l))"
say "CAND9 end; the player's cfg sha1 $(sha "$CFG" | cut -c1-12) (must be $CFG0); plugins $(sha "$P" | cut -c1-8); build/bin $(sha build/bin/HoF2VR.dll | cut -c1-8)"
