#!/usr/bin/env bash
# Candidate 7's reruns (env 1 helper, 2026-10-16; chain_cand7.sh with R_ blocks): the exact DLL in build/bin (62b1451d, main 6f90e18 = main + combat's
# merge-ready #4 + its headset switches), under one reservation. Everything candidate 6's chain ran (logs/chain_rc6.log),
# merge-ready #4's tests (env 2's logs/mr4j.sh invocations; phys straight after stage1), the seated round's tests
# (seated_check, the full seated sweep at three heights, B1 seated) and switches_test with the Enemies tab.
# The user always wins: a watcher stops the running block, the game and Monado and releases the reservation as soon as
# status.json says "user": true; the chain waits until it is false, reserves again and reruns the block.
# From 18:15 on no block starts (the user may come into VR from about 18:30).
set -u
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
T=/tmp/claude-hof2
LOG=${CAND7_LOG:-$H/logs/chain_cand7.log}
WANT=62b1451defc8a8f24c1d6e2a6174e271a110c695
G="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2"
GAMELOG="$G/BepInEx/LogOutput.log"
CFG="$G/BepInEx/config/flat2vr.hof2vr.cfg"
ST=/run/user/1000/vr-session/status.json
MARK=$T/cand7_user_in
BPID=$T/cand7_block.pid
GEAR="BasicSword_noToken,BasicShield"
export HOF2VR_RESERVE_MAX_H=8
say() { echo "=== $* ($(date +%T))" | tee -a "$LOG"; }
sha() { sha1sum < "$1" | cut -d' ' -f1; }
treepids() { local c; echo "$1"; for c in $(pgrep -P "$1"); do treepids "$c"; done; }
killtree() { local all; all=$(treepids "$1"); kill -STOP $all 2>/dev/null; kill -TERM $all 2>/dev/null; kill -CONT $all 2>/dev/null; }
userseen() { grep -q '"user": true' "$ST" 2>/dev/null || [ ! -f "$ST" ] || [ $(( $(date +%s) - $(stat -c %Y "$ST") )) -gt 300 ]; }
userin() { userseen && { sleep 2; userseen; }; }
late() { [ "$(date +%H%M)" -ge 1815 ]; }
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
    late && return 0
    say "the user is out of VR: resuming"
    scripts/reserve.sh start $$ >> "$LOG" 2>&1
  fi
}
gpu() { local n; n=$(journalctl -k --since "@$1" --no-pager 2>/dev/null | grep -ciE 'amdgpu.*(page fault|timeout|wedged|reset)'); [ "$n" = 0 ] || say "GPU: $n amdgpu fault / timeout lines in the kernel log during $2: $(journalctl -k --since "@$1" --no-pager 2>/dev/null | grep -iE 'amdgpu.*(timeout|Process)' | head -2 | cut -c1-160 | tr '\n' ' ')"; }
run_block() {
  local name=$1 n=0 rc t0; shift
  while :; do
    waitout
    if late; then say "block $name: not started - 18:15 or later"; return 98; fi
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
table() { python3 scripts/session.py start --snapshot magician_mid --to table >> logs/cand7_sessions.out 2>&1; say "session at the table: $(state | cut -c1-50)"; }
menu() { python3 scripts/cmd.py --status 2>/dev/null | python3 -c 'import sys, json; print(json.load(sys.stdin).get("menu", ""))' 2>/dev/null; }
# at the table with the map ready (no encounter or activation left open: seated_check leaves one, and the fixtures' jump
# is refused until it is finished - candidate 7's first run, 11:04-11:14) - else a fresh session
ensure_table() { if attable && [ "$(menu)" = "Map" ]; then :; else say "ensure_table: $(state | cut -c1-40), menu '$(menu)' - a fresh session"; table; fi; }
leave() { python3 -c "import sys; sys.path.insert(0, 'scripts'); import indicator_shots; print('left:', indicator_shots.leave())" >> logs/cand7_leave.out 2>&1; }
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
B_scan() {
  t "uyf_scan" logs/cand7_uyf_scan.out python3 scripts/uyf_scan.py
  t "savecheck before" logs/cand7_savecheck1.out bash scripts/savecheck.sh
}
B_tour() {
  touch $T/cand7_t0
  python3 scripts/playtest.py > logs/cand7_tour.out 2>&1; tourres "tour standing rc $?" logs/cand7_tour.out
  keepshots cand7_standing $T/cand7_t0
  say "HRTF in the standing tour: $(grep -c 'Hrtf: ready' "$GAMELOG") ready lines; $(grep -m1 'Hrtf:' "$GAMELOG" | cut -c1-120)"
  say "HRTF install: data.unity3d $(sha256sum < "$G/Hand of Fate 2_Data/data.unity3d" | cut -c1-16) (dist/data-hrtf.unity3d $(sha256sum < dist/data-hrtf.unity3d | cut -c1-16)); Plugins/x86_64: $(ls "$G/Hand of Fate 2_Data/Plugins/x86_64" | tr '\n' ' ')"
  bash scripts/uninstall_check.sh > logs/cand7_uninstall.out 2>&1; say "uninstall_check rc $? | $(grep -E 'PASS|FAIL' logs/cand7_uninstall.out | tail -1 | cut -c1-120)"
}
B_tour_seated() {
  touch $T/cand7_t1
  python3 scripts/playtest.py --seated > logs/cand7_tour_seated.out 2>&1; tourres "tour seated rc $?" logs/cand7_tour_seated.out
  say "seated reach: $(grep 'seated_touch_within_reach' logs/cand7_tour_seated.out | tail -1 | cut -c10-160)"
  keepshots cand7_seated $T/cand7_t1
}
B_tour_hrtf() {
  HOF2VR_HRTF=1 python3 scripts/playtest.py > logs/cand7_tour_hrtf.out 2>&1; tourres "tour with the HRTF on rc $?" logs/cand7_tour_hrtf.out
  grep -E 'HRTF on for this test run|Hrtf: ready' "$GAMELOG" | head -2 | cut -c1-240 | sed 's/^/    /' >> "$LOG"
  say "HRTF on: $(grep -iE 'hrtf|steam audio|phonon' "$GAMELOG" | grep -ciE 'not used|paused') not-used/paused lines"
}
B_b1() {
  ensure_table
  t "b1 standing" logs/cand7_b1.out python3 scripts/b1_test.py
  t "b1 seated" logs/cand7_b1_seated.out python3 scripts/b1_test.py --seated
  t "seated_check" logs/cand7_seated_check.out python3 scripts/seated_check.py
  t "switches_test" logs/cand7_switches.out python3 scripts/switches_test.py
  exc "b1 / seated / switches"
}
B_switches() {
  t "switches_test --start (fresh session)" logs/cand7_switches2.out python3 scripts/switches_test.py --start
  exc "switches"
}
B_tour_seated_r() {
  python3 scripts/playtest.py --seated > logs/cand7_tour_seated2.out 2>&1; tourres "tour seated (again) rc $?" logs/cand7_tour_seated2.out
  say "seated reach: $(grep 'seated_touch_within_reach' logs/cand7_tour_seated2.out | tail -1 | cut -c10-160)"
}
# merge-ready #4 (env 2's MR4j invocations; phys straight after stage1, MR4l)
B_mr4_shadows() {
  ensure_table
  t "mr4 leadhold, combogaps x2 (shadows)" logs/cand7_mr4_shadows.out python3 scripts/audit_test.py --tag C7P --fixture shadows --equip $GEAR --stay --parts leadhold,combogaps,combogaps
  gaveup logs/cand7_mr4_shadows.out && t "mr4 shadows (again)" logs/cand7_mr4_shadows2.out python3 scripts/audit_test.py --tag C7P2 --fixture shadows --equip $GEAR --stay --parts leadhold,combogaps,combogaps
  leave
  t "mr4 stage2 combo x2, interruptcut (ogre)" logs/cand7_mr4_combo.out python3 scripts/stage2_test.py --tag C7Vc --fixture combo --equip $GEAR --stay --parts combo,combo,interruptcut
  leave
  exc "mr4 shadows / combo"
}
B_mr4_shield() {
  ensure_table
  t "mr4 shovewaiter, shove x3, approach, pauses (soldiers)" logs/cand7_mr4_shield.out python3 scripts/audit_test.py --tag C7Q --fixture shield --equip $GEAR --stay --parts shovewaiter,shove,shove,shove,approach,pauses
  gaveup logs/cand7_mr4_shield.out && t "mr4 soldiers (again)" logs/cand7_mr4_shield2.out python3 scripts/audit_test.py --tag C7Q2 --fixture shield --equip $GEAR --stay --parts shovewaiter,shove,shove,shove,approach,pauses
  leave
  exc "mr4 soldiers"
}
B_mr4_thieves() {
  ensure_table
  t "mr4 idlebonus, strafe, waiting, backdash, dash x3, flick, shortdash, exitdash (thieves)" logs/cand7_mr4_thieves.out python3 scripts/audit_test.py --tag C7R --fixture thieves --equip $GEAR --stay --parts idlebonus,strafe,waiting,backdash,dash,dash,dash,flick,shortdash,exitdash
  gaveup logs/cand7_mr4_thieves.out && t "mr4 thieves (again)" logs/cand7_mr4_thieves2.out python3 scripts/audit_test.py --tag C7R2 --fixture thieves --equip $GEAR --stay --parts idlebonus,strafe,waiting,backdash,dash,dash,dash,flick,shortdash,exitdash
  leave
  exc "mr4 thieves"
}
B_mr4_fight() {
  ensure_table
  t "mr4 stage1 block x3, offset (thieves)" logs/cand7_mr4_stage1.out python3 scripts/stage1_test.py --tag C7Ts1 --fixture thieves --equip $GEAR --stay --parts block,block,block,offset
  inafight || t "mr4 stage1 (again)" logs/cand7_mr4_stage1b.out python3 scripts/stage1_test.py --tag C7Ts1b --fixture thieves --equip $GEAR --stay --parts block,block,block,offset
  if inafight; then t "mr4 phys swing, big, fast (same fight)" logs/cand7_mr4_phys.out python3 scripts/phys_test.py --tag C7Up --no-fixture --stay --parts swing,big,fast; else say "mr4 phys: the fight is over"; fi
  if inafight; then t "mr4 stage2 tickets, timeout, crit (same fight)" logs/cand7_mr4_stage2.out python3 scripts/stage2_test.py --tag C7Vs2 --no-fixture --stay --parts tickets,timeout,crit; else say "mr4 stage2 tickets: the fight is over"; fi
  leave
  exc "mr4 one fight"
}
B_sweeps() {
  table
  t "sweep standing (--lean)" logs/cand7_sweep.out timeout 3600 python3 scripts/table_sweep.py --lean
  t "sweep seated (--seated --lean --reach)" logs/cand7_sweep_seated.out timeout 3600 python3 scripts/table_sweep.py --seated --lean --reach
  grep -E '==== [0-9]* events|==== dice marked' logs/cand7_sweep.out logs/cand7_sweep_seated.out | tail -4 | cut -c1-300 | sed 's/^/    /' >> "$LOG"
  exc "sweeps"
}
B_combat() {
  ensure_table
  t "phys_test" logs/cand7_phys.out python3 scripts/phys_test.py --tag C7P0
  below logs/cand7_phys.out 14 && { ensure_table; t "phys_test (again: below 14)" logs/cand7_phys2.out python3 scripts/phys_test.py --tag C7P02; }
  ensure_table
  t "stage1_test" logs/cand7_stage1.out python3 scripts/stage1_test.py --tag C7S1
  below logs/cand7_stage1.out 9 && { ensure_table; t "stage1_test (again: below 9)" logs/cand7_stage1b.out python3 scripts/stage1_test.py --tag C7S1b; }
  ensure_table
  t "stage2_test" logs/cand7_stage2.out python3 scripts/stage2_test.py --tag C7S2
  below logs/cand7_stage2.out 13 && { ensure_table; t "stage2_test (again: below 13)" logs/cand7_stage2b.out python3 scripts/stage2_test.py --tag C7S2b; }
  exc "phys / stage1 / stage2"
}
# merge-ready #3 (candidate 6's blocks; the shadows / combo block is merge-ready #4's above, a superset)
B_mr3_thieves() {
  ensure_table
  t "mr3 waiting (thieves)" logs/cand7_mr3_waiting.out python3 scripts/audit_test.py --tag C7W --fixture thieves --equip $GEAR --stay --parts waiting
  inafight || t "mr3 waiting (thieves, again)" logs/cand7_mr3_waiting2.out python3 scripts/audit_test.py --tag C7W2 --fixture thieves --equip $GEAR --stay --parts waiting
  t "mr3 bodylean, commit, leadhold" logs/cand7_mr3_lean.out python3 scripts/audit_test.py --tag C7L --no-fixture --stay --parts bodylean,commit,leadhold
  leave
  t "mr3 circle, glovehud, weaponsound, trail, worldtouch" logs/cand7_mr3_items.out python3 scripts/audit_test.py --tag C7J --fixture thieves --equip $GEAR --stay --parts circle,glovehud,weaponsound,trail,worldtouch
  inafight || t "mr3 items (again)" logs/cand7_mr3_items2.out python3 scripts/audit_test.py --tag C7J2 --fixture thieves --equip $GEAR --stay --parts circle,glovehud,weaponsound,trail,worldtouch
  t "move_test" logs/cand7_move.out python3 scripts/move_test.py --tag C7M --no-fixture
  leave
  t "mr3 approach (thieves)" logs/cand7_mr3_approach.out python3 scripts/audit_test.py --tag C7A --fixture thieves --equip $GEAR --stay --parts approach
  leave
  exc "mr3 thieves"
}
B_mr3_shield() {
  ensure_table
  t "mr3 stage2 shields (aggression..knock)" logs/cand7_mr3_shield.out python3 scripts/stage2_test.py --tag C7B --fixture shield --equip $GEAR --stay --parts aggression,pool,postattack,shieldup,blocktiming,deflect,knock
  t "mr3 bodylean after the shields" logs/cand7_mr3_shieldlean.out python3 scripts/audit_test.py --tag C7Bl --no-fixture --stay --parts bodylean
  leave
  exc "mr3 shields"
}
B_mr3_boss() {
  ensure_table
  t "neverreach, bossdeath (Black Knight)" logs/cand7_boss.out python3 scripts/audit_test.py --tag C7K --fixture boss_knight --equip $GEAR --parts neverreach,bossdeath
  gaveup logs/cand7_boss.out && t "boss (again)" logs/cand7_boss2.out python3 scripts/audit_test.py --tag C7K2 --fixture boss_knight --equip $GEAR --parts neverreach,bossdeath
  exc "boss"
}
B_audit_rest() {
  ensure_table
  t "audit (merge-ready #2's parts + alloc, diffflash)" logs/cand7_audit.out python3 scripts/audit_test.py --tag C7U --fixture thieves --equip $GEAR --parts commit,sidestep,dash,flick,shortdash,health,trail,smartsnap,super,diffflash,alloc
  exc "audit rest"
}
B_interact() {
  table
  t "interact card_flip figurine" logs/cand7_interact.out python3 scripts/interact_test.py card_flip figurine
  t "dealer_gaze --targets (fresh session)" logs/cand7_gaze_targets.out python3 scripts/dealer_gaze.py --start --targets
  exc "interact / gaze targets"
}
B_turn_audio() {
  table
  t "turn_test" logs/cand7_turn.out python3 scripts/turn_test.py
  t "audio_check" logs/cand7_audio.out python3 scripts/audio_check.py
  t "audio_check --attract" logs/cand7_audio_attract.out python3 scripts/audio_check.py --attract
  exc "turn / audio"
}
B_gaze() {
  table
  t "dealer_gaze" logs/cand7_gaze.out python3 scripts/dealer_gaze.py
  t "dealer_gaze --dice" logs/cand7_gaze_dice.out python3 scripts/dealer_gaze.py --dice
  exc "gaze"
}
B_hands() {
  table
  t "fingers_test" logs/cand7_fingers.out python3 scripts/fingers_test.py
  t "handfeel_test" logs/cand7_handfeel.out python3 scripts/handfeel_test.py
  grep -q '1 failed: speeds\|0 failed' logs/cand7_handfeel.out || t "handfeel_test (again)" logs/cand7_handfeel2.out python3 scripts/handfeel_test.py
  t "boards_test table" logs/cand7_boards_table.out python3 scripts/boards_test.py table
  t "boards_test menu" logs/cand7_boards_menu.out python3 scripts/boards_test.py menu
  t "boards_test encounter" logs/cand7_boards_encounter.out python3 scripts/boards_test.py encounter
  exc "hands / boards"
}
B_focus() { t "focus_test" logs/cand7_focus.out python3 scripts/focus_test.py; exc "focus"; }
B_fightvr() {
  python3 scripts/session.py start --snapshot first_fight --to table >> logs/cand7_sessions.out 2>&1; say "session (first_fight): $(state | cut -c1-50)"
  t "fight_vr_test" logs/cand7_fight_vr.out timeout 1500 python3 scripts/fight_vr_test.py --tag F7
  exc "fight_vr"
}
B_limbo() {
  python3 scripts/limbo_test.py --snapshot first_fight --tag L7 > logs/cand7_limbo.out 2>&1; rc=$?
  say "limbo_test rc $rc | states: $(grep -oE 'GameState_[A-Za-z_]+' logs/cand7_limbo.out | uniq | tr '\n' ' ' | cut -c1-300)"
  exc "limbo"
}
B_gc() {
  table
  scripts/testenv.sh quiet python3 scripts/gc_probe.py --label cand7 > logs/cand7_gc_probe.out 2>&1; res "gc_probe (quiet)" $? logs/cand7_gc_probe.out
  python3 scripts/session.py stop > /dev/null 2>&1
  python3 scripts/gc_soak.py --minutes 32 --boot --label cand7 > logs/cand7_gc_soak.out 2>&1; res "gc_soak 32 min" $? logs/cand7_gc_soak.out
  tail -n 25 logs/gc_soak.log | cut -c1-200 | sed 's/^/    /' >> "$LOG"
  exc "gc soak"
}

# ---- the reruns (candidate 7's flakes, each once; switches_test after its test fix ac2a7bd) ----
R_switches() {
  t "switches_test --start (again, the test's room behind each dash)" logs/cand7r_switches.out python3 scripts/switches_test.py --start
  exc "switches again"
}
R_combogaps() {
  ensure_table
  t "combogaps x2 (shadows, again)" logs/cand7r_combogaps.out python3 scripts/audit_test.py --tag C7rG --fixture shadows --equip $GEAR --stay --parts combogaps,combogaps
  gaveup logs/cand7r_combogaps.out && t "combogaps (again, the fixture)" logs/cand7r_combogaps2.out python3 scripts/audit_test.py --tag C7rG2 --fixture shadows --equip $GEAR --stay --parts combogaps,combogaps
  leave
  exc "combogaps again"
}
R_combo() {
  ensure_table
  t "stage2 combo (husk thief, again)" logs/cand7r_combo.out python3 scripts/stage2_test.py --tag C7rC --fixture combo --equip $GEAR --stay --parts combo
  leave
  exc "combo again"
}
R_thieves() {
  ensure_table
  t "waiting, dash (thieves, again)" logs/cand7r_thieves.out python3 scripts/audit_test.py --tag C7rR --fixture thieves --equip $GEAR --stay --parts waiting,dash
  gaveup logs/cand7r_thieves.out && t "waiting, dash (again, the fixture)" logs/cand7r_thieves2.out python3 scripts/audit_test.py --tag C7rR2 --fixture thieves --equip $GEAR --stay --parts waiting,dash
  if inafight; then t "move_test (again)" logs/cand7r_move.out python3 scripts/move_test.py --tag C7rM --no-fixture; else say "move_test: the fight is over"; fi
  leave
  exc "thieves again"
}
R_physafter() {
  ensure_table
  t "stage1 block x3, offset (thieves, again)" logs/cand7r_stage1.out python3 scripts/stage1_test.py --tag C7rTs1 --fixture thieves --equip $GEAR --stay --parts block,block,block,offset
  if inafight; then t "phys swing, big, fast after stage1 (again)" logs/cand7r_phys.out python3 scripts/phys_test.py --tag C7rUp --no-fixture --stay --parts swing,big,fast; else say "phys: the fight is over"; fi
  leave
  exc "phys after stage1 again"
}
R_stage2() {
  ensure_table
  t "stage2_test (again)" logs/cand7r_stage2.out python3 scripts/stage2_test.py --tag C7rS2
  exc "stage2 again"
}
R_focus() {
  table
  t "focus_test (again, from a fresh table: its fight began in ChallengeSetup)" logs/cand7r_focus.out python3 scripts/focus_test.py
  exc "focus again"
}
# ---- the chain ----
: > "$LOG"
if userin; then say "the user is in VR - not starting"; exit 9; fi
if late; then say "18:15 or later - not starting"; exit 9; fi
[ "$(sha build/bin/HoF2VR.dll)" = "$WANT" ] || { say "build/bin is not the candidate - not starting"; exit 8; }
scripts/reserve.sh start $$ >> "$LOG" 2>&1
watcher & WPID=$!
trap 'kill $WPID 2>/dev/null; scripts/reserve.sh stop >> "$LOG" 2>&1; rm -f $BPID $MARK' EXIT
trap 'exit 1' INT TERM
say "CAND7 start main $(git rev-parse --short HEAD) DLL ${WANT:0:8}; the player's cfg sha1 $(sha "$CFG" | cut -c1-12)"
for b in ${CAND7_BLOCKS:-B_scan B_tour B_tour_seated B_b1 B_mr4_shadows B_mr4_shield B_mr4_thieves B_mr4_fight B_sweeps \
         B_combat B_mr3_thieves B_mr3_shield B_mr3_boss B_audit_rest B_interact B_turn_audio B_gaze B_hands B_focus \
         B_fightvr B_limbo B_tour_hrtf B_gc}; do
  run_block $b $b
  rc=$?
  say "$b done rc $rc"
  [ $rc = 98 ] && break
  [ "$(sha build/bin/HoF2VR.dll)" = "$WANT" ] || { say "build/bin changed - stopping"; break; }
done
python3 scripts/session.py stop > /dev/null 2>&1
sleep 3
say "game stopped: $(pgrep -x 'Hand of Fate 2.' | tr '\n' ' ')(none = stopped)"
scripts/monado.sh stop >> "$LOG" 2>&1
t "savecheck after" logs/cand7_savecheck2.out bash scripts/savecheck.sh
say "CAND7 end; the player's cfg sha1 $(sha "$CFG" | cut -c1-12); build/bin $(sha build/bin/HoF2VR.dll | cut -c1-8)"
