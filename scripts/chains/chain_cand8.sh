#!/usr/bin/env bash
# Candidate 8's chain (env 1, 2026-10-17): the exact DLL in build/bin (9dadfa64, main a6cddcb = main + combat 73f0d37:
# F1-F7, N2, N3, N7, N8; the gnomes' F8 left out), under one reservation. Blocks: the fights 3 rounds (env 2's pf blocks
# A B C D F G + pf/pre.sh's N2 / N3 checks, env 1 copies in scripts/chains/cand8/), the tours standing and seated, env 1's
# tests (b1, seated_check, switches_test, the playtest fixes: figurine pinch and set-down, dice re-roll, sliders, T6,
# N1's voice in the Steam runtime, limbo_test), the picture walk (captures/cand8/: the title and the table's places, then
# a fight each of thieves, soldiers, gnomes and a boss).
# The user always wins: a watcher stops the running block, the game and Monado and releases the reservation as soon as
# status.json says "user": true; the chain waits until it is false, reserves again and reruns the block. (No cut-off time.)
set -u
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
T=/tmp/claude-hof2
mkdir -p $T ${CAND8_PICS:-captures/cand8}
LOG=${CAND8_LOG:-$H/logs/chain_cand8.log}
WANT=${CAND8_WANT:-9dadfa6417b90bdbc2246a42df2da82892ca01cb}
G="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2"
GAMELOG="$G/BepInEx/LogOutput.log"
CFG="$G/BepInEx/config/flat2vr.hof2vr.cfg"
ST=/run/user/1000/vr-session/status.json
MARK=$T/cand8_user_in
BPID=$T/cand8_block.pid
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
gpu() { local n; n=$(journalctl -k --since "@$1" --no-pager 2>/dev/null | grep -ciE 'amdgpu.*(page fault|timeout|wedged|reset)'); [ "$n" = 0 ] || say "GPU: $n amdgpu fault / timeout lines in the kernel log during $2: $(journalctl -k --since "@$1" --no-pager 2>/dev/null | grep -iE 'amdgpu.*(timeout|Process)' | head -2 | cut -c1-160 | tr '\n' ' ')"; }
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
    gpu $t0 "$name"
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
table() { python3 scripts/session.py start --snapshot magician_mid --to table >> logs/cand8_sessions.out 2>&1; say "session at the table: $(state | cut -c1-50)"; }
menu() { python3 scripts/cmd.py --status 2>/dev/null | python3 -c 'import sys, json; print(json.load(sys.stdin).get("menu", ""))' 2>/dev/null; }
# at the table with the map ready (no encounter or activation left open: seated_check leaves one, and the fixtures' jump
# is refused until it is finished - candidate 7's first run, 11:04-11:14) - else a fresh session
ensure_table() { if attable && [ "$(menu)" = "Map" ]; then :; else say "ensure_table: $(state | cut -c1-40), menu '$(menu)' - a fresh session"; table; fi; }
leave() { python3 -c "import sys; sys.path.insert(0, 'scripts'); import indicator_shots; print('left:', indicator_shots.leave())" >> logs/cand8_leave.out 2>&1; }
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
O=${CAND8_O:-logs/cand8}
mkdir -p $O
putdll() { install -m 644 "$1" "$(dirname "$P")/.HoF2VR.dll.new" && mv -f "$(dirname "$P")/.HoF2VR.dll.new" "$P"; }
B_scan() {
  t "uyf_scan" $O/uyf_scan.out python3 scripts/uyf_scan.py
  t "savecheck before" $O/savecheck1.out bash scripts/savecheck.sh
}
B_pre() {
  table
  t "pf pre: N2 edgeturn, N3 swarmboss (F8 probe)" $O/pre.out bash scripts/chains/cand8/pre.sh
  grep -E '(PASS|FAIL) ' $O/pre.out | cut -c1-260 | sed 's/^/    /' >> "$LOG"
  leave
  exc "pre"
}
# env 2's pf blocks (scripts/chains/pf/chain_pf.sh), one round (no walk out between them: the next block starts a fresh
# session anyway - the walk out ended at the challenge map and cost 2 min a block, 14:24-14:32)
B_fights() {
  local i=$1
  ensure_table; t "r$i A shadows leadhold,combogaps" $O/A$i.out python3 scripts/audit_test.py --tag C8${i}A --fixture shadows --equip $GEAR --stay --parts leadhold,combogaps
  ensure_table; t "r$i B thieves waiting,waiting,dash,strafe,health" $O/B$i.out python3 scripts/audit_test.py --tag C8${i}B --fixture thieves --equip $GEAR --stay --parts waiting,waiting,dash,strafe,health
  ensure_table; t "r$i C thieves stage1 block x3" $O/C$i.out python3 scripts/stage1_test.py --tag C8${i}C --fixture thieves --equip $GEAR --stay --parts block,block,block
  if inafight; then t "r$i C move_test (same fight)" $O/M$i.out python3 scripts/move_test.py --tag C8${i}M --no-fixture; else say "r$i move_test: the fight is over"; fi
  ensure_table; t "r$i D combo, interruptcut (combo fixture)" $O/D$i.out python3 scripts/stage2_test.py --tag C8${i}D --fixture combo --equip $GEAR --stay --parts combo,interruptcut
  ensure_table; t "r$i F thieves linecut,crit,wrong,stickuse" $O/F$i.out python3 scripts/stage2_test.py --tag C8${i}F --fixture thieves --equip $GEAR --stay --parts linecut,crit,wrong,stickuse
  ensure_table; t "r$i G dodge fixture redbar,redlean" $O/G$i.out python3 scripts/stage1_test.py --tag C8${i}G --fixture dodge --equip $GEAR --stay --parts redbar,redlean
  exc "fights round $i"
}
B_fights1() { B_fights 1; }
B_fights2() { B_fights 2; }
B_fights3() { B_fights 3; }
B_tour() {
  touch $T/cand8_t0
  python3 scripts/playtest.py > $O/tour.out 2>&1; tourres "tour standing rc $?" $O/tour.out
  keepshots cand8_standing $T/cand8_t0
}
B_tour_seated() {
  touch $T/cand8_t1
  python3 scripts/playtest.py --seated > $O/tour_seated.out 2>&1; tourres "tour seated rc $?" $O/tour_seated.out
  say "seated reach: $(grep 'seated_touch_within_reach' $O/tour_seated.out | tail -1 | cut -c10-160)"
  keepshots cand8_seated $T/cand8_t1
}
B_mine() {
  ensure_table
  t "b1 standing" $O/b1.out python3 scripts/b1_test.py
  t "b1 seated" $O/b1_seated.out python3 scripts/b1_test.py --seated
  t "seated_check" $O/seated_check.out python3 scripts/seated_check.py
  t "switches_test --start (a fresh session)" $O/switches.out python3 scripts/switches_test.py --start
  exc "b1 / seated / switches"
  table
  t "interact figurine_pinch dice_reroll_vr card_flip" $O/interact.out python3 scripts/interact_test.py figurine_pinch dice_reroll_vr card_flip
  table
  t "interact figurine (a fresh map)" $O/interact_fig.out python3 scripts/interact_test.py figurine
  ensure_table
  t "fixtests: sliders (hand check, VR settings), T6" $O/fixtests.out python3 scripts/chains/cand8/fixtests.py
  exc "playtest fixes"
}
B_n1() {
  python3 scripts/session.py stop > /dev/null 2>&1
  bash scripts/chains/pt17_n1.sh > $O/n1.out 2>&1
  say "N1 voice in the Steam runtime: $(grep -E 'recorded from|no voice|no headset' $O/n1.out | tail -1 | cut -c1-140) | $(grep 'container:' $O/n1.out | cut -c1-140)"
  pactl list sources short 2>/dev/null | grep -q hof2vr_wivrn_test && say "N1: the stand-in source is still loaded!" || say "N1: the stand-in source is gone"
  exc "N1"
}
B_limbo() {
  python3 scripts/limbo_test.py --snapshot first_fight --tag L8c > $O/limbo.out 2>&1; rc=$?
  say "limbo_test rc $rc | states: $(grep -oE 'GameState_[A-Za-z_]+' $O/limbo.out | uniq | tr '\n' ' ' | cut -c1-300)"
  grep -aE 'progress: fight_in done|gate: go' $O/limbo.out | tail -3 | cut -c1-200 | sed 's/^/    /' >> "$LOG"
  exc "limbo"
}
B_walk() {
  python3 scripts/session.py start --snapshot magician_mid >> logs/cand8_sessions.out 2>&1; say "session at the title: $(state | cut -c1-50)"
  t "walk: the title and the table's places" $O/walk.out timeout 1500 python3 scripts/chains/cand8/walk.py
  exc "walk (table)"
  ensure_table
  t "walk: the fights (thieves, soldiers, gnomes, boss)" $O/fightpics.out timeout 1800 bash scripts/chains/cand8/fightpics.sh
  say "pictures in ${CAND8_PICS:-captures/cand8}: $(ls ${CAND8_PICS:-captures/cand8} | wc -l)"
  exc "walk (fights)"
}

# ---- the chain ----
: > "$LOG"
if userin; then say "the user is in VR - not starting"; exit 9; fi
[ "$(sha build/bin/HoF2VR.dll)" = "$WANT" ] || { say "build/bin is not the candidate - not starting"; exit 8; }
scripts/reserve.sh start $$ >> "$LOG" 2>&1
watcher & WPID=$!
trap 'kill $WPID 2>/dev/null; [ "$(sha "$P")" = "$WANT" ] || { putdll build/bin/HoF2VR.dll; say "plugins put back: $(sha "$P" | cut -c1-8)"; }; scripts/reserve.sh stop >> "$LOG" 2>&1; rm -f $BPID $MARK' EXIT
trap 'exit 1' INT TERM
say "CAND8 start main $(git rev-parse --short HEAD) DLL ${WANT:0:8}; the player's cfg sha1 $(sha "$CFG" | cut -c1-12)"
for b in ${CAND8_BLOCKS:-B_scan B_pre B_fights1 B_fights2 B_fights3 B_tour B_tour_seated B_mine B_n1 B_limbo B_walk}; do
  run_block $b $b
  rc=$?
  say "$b done rc $rc"
  [ "$(sha build/bin/HoF2VR.dll)" = "$WANT" ] || { say "build/bin changed - stopping"; break; }
done
python3 scripts/session.py stop > /dev/null 2>&1
sleep 3
say "game stopped: $(pgrep -x 'Hand of Fate 2.' | tr '\n' ' ')(none = stopped)"
scripts/monado.sh stop >> "$LOG" 2>&1
t "savecheck after" $O/savecheck2.out bash scripts/savecheck.sh
[ "$(sha "$P")" = "$WANT" ] || { putdll build/bin/HoF2VR.dll; say "plugins put back: $(sha "$P" | cut -c1-8)"; }
say "CAND8 end; the player's cfg sha1 $(sha "$CFG" | cut -c1-12) (must be fd82b5a53cbc); plugins $(sha "$P" | cut -c1-8); build/bin $(sha build/bin/HoF2VR.dll | cut -c1-8)"
