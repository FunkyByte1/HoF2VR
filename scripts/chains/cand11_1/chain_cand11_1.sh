#!/usr/bin/env bash
# Candidate 11.1's chain (env 1, written 2026-10-20 evening): main = candidate 11 (d7f44c7) + p3combat bc6628f (env 2: the
# 55 % rule - a shield block when the shield's outline, projected along the blow, covers >= 55 % of the 30 cm circle; green ==
# "this will block"; the seam `timescale 0 hold`) + hud f51aff1 (env 3: the glove cuff cut to HL:A's proportions, the cuff
# items moved, [Table] PlayerSize 1.0 replacing ReachBoost 1.06, the `handsize` seam, gnome posts at the arena's edge), on the
# exact DLL in build/bin (WANT = its sha1, the first argument or CAND11_1_WANT), under one reservation. Candidate 11's chain
# (cand11/chain_cand11.sh) with env 2's cover blocks and env 3's hand-size steps added; the player's cfg, their Unity prefs and
# the DLC record must stay unchanged; savecheck before and after.
# Blocks (CAND11_1_BLOCKS picks; default all, in this order):
#   B_scan B_pre                 candidate 9's: the UYF scan, savecheck; N2 edge turn, N3 swarm boss
#   B_p4t B_p4title B_p4f        env 1's p4_test: at the table in a fresh session grip (first), calib, credits, settings,
#                                dialogues, sliders, encounter; at the title: title; fights: fight (the Board, the hand
#                                badges, the glow by day), night (glow and Board at night)
#   B_hands                      env 3's hands turn.sh steps "after size hub": handsize_test.py table,fit,fight (the glove's
#                                drawn size, pictures); size,encounter (PlayerSize 1.06 / 1.0, this run only); hub (the side
#                                table at both sizes) - pictures into $PICS/hands
#   B_p4b                        env 2's p4b_test (chain_p4b.sh's X="S B H E"): S thieves shield,circle,edge,threat; B two
#                                assassins assassin,green; H thieves without a shield bladedmg; E thieves exit - with pictures
#   B_cover                      env 2's 55 % rule (chain_p4b.sh's K U W): K BasicShield cover,edge; U Buckler cover; W
#                                BroadShield cover - with pictures into $PICS/cover
#   B_p4e3                       env 3's p4e3_test: crit, critpics, angles; posts, ground, cling - with pictures
#   B_blade                      (not in the default list) stage1 bladeblock with the sword and the two-handed hammer
#   B_hud B_perf B_p3            env 3's hud_test (flat, board, body, shell, rings, switch) and perf.sh (Shell / worn / Rings in
#                                the big fight); env 2's p3_test parts M S R Q V L T
#   B_fights1 (B_fights2)        chain_pf's fight blocks A B C D F G P N (+ move_test): round 1 Board + Shell (the default);
#                                round 2 Body + Rings not in the default list
#   B_interact B_cb B_mine       every interact group in a fresh session (+ figurine_drop, figurine_drop_beside), fixtests;
#                                the seated call to battle (seated x10 + the deal, standing x5); wheel_look, b1 standing /
#                                seated, seated_check, switches_test, audit_fixes_test
#   B_tour                       the standing tour (B_tour_seated, B_focus, B_side, B_n1, B_limbo: not in the default list)
#   B_walkt B_walkf              the picture walk with the head moving -> captures/cand11_1/: the title (turned left), the
#                                table's places, the campfire, the map room / side table (walk.py hub adventure); the fights:
#                                candidate 9's (thieves, soldiers, gnomes, bombs, a boss), then cand11/fightpics.sh: the
#                                shield on its circle (Board + Shell and Body + Rings), goblin archers' and lobs' ground
#                                arrows (gold with the shield, red without), a true night arena (hud_pics + both combos)
# The user always wins: a watcher stops the running block, the game and Monado and releases the reservation as soon as
# status.json says "user": true; the chain waits until it is false, reserves again and reruns the block (3 tries).
# A GPU fault (amdgpu reset / page fault / ring timeout in the kernel log) stops everything at once and ends the chain.
#   chain_cand11_1.sh --dry [WANT]   prints the blocks and their commands, runs nothing
#   Run unsandboxed: nohup setsid bash scripts/chains/cand11_1/chain_cand11_1.sh <WANT sha1> > /dev/null 2>&1 &
#   NORESERVE=1: no reservation (fair turns); PERF_OLD=<a DLL file>: B_perf also measures that DLL (perf.sh old) in the
#   same fight, then puts the candidate back. Logs logs/chain_cand11_1.log, logs/cand11_1/.
# Known test-side fails (NOTES "Candidate 11" and env 2's / env 3's sections): p4b threat "too few strikes"; p4b bladedmg and
# stage1 bladeblock (the scripted weapon block lands about 2 in 6); the tours' title_flicker and jumpmenu_chord;
# figurine_drop + figurine_drop_beside (combined: "the map takes no moves"); fights C, F, G, P sometimes; interact's dice
# task occasionally left running; hud shell_recover_look, rings_guard (cand10); handsize size / hub: their own bars (the
# stat cards' hand and the side table's pins FAR at both sizes, as before the change).
set -u
DRY=0
[ "${1:-}" = --dry ] && { DRY=1; shift; }
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
T=/tmp/claude-hof2
PICS=${CAND11_1_PICS:-$H/captures/cand11_1}
export CAND9_PICS=$PICS HUD_PICS=$PICS/hud   # (the cand9 walk scripts' and hud_pics.py's picture folders)
LOG=${CAND11_1_LOG:-$H/logs/chain_cand11_1.log}
O=${CAND11_1_O:-$H/logs/cand11_1}
WANT=${1:-${CAND11_1_WANT:-}}
G="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2"
GAMELOG="$G/BepInEx/LogOutput.log"
CFG="$G/BepInEx/config/flat2vr.hof2vr.cfg"
DLCF="$G/BepInEx/hof2vr_dlc.txt"
PREFS="$HOME/.config/unity3d/Defiant Development/Hand of Fate 2/prefs"
ST=/run/user/1000/vr-session/status.json
MARK=$T/cand11_1_user_in
BPID=$T/cand11_1_block.pid
GEAR="BasicSword_noToken,BasicShield"
P="$G/BepInEx/plugins/HoF2VR/HoF2VR.dll"
BLOCKS=${CAND11_1_BLOCKS:-B_scan B_pre B_p4t B_p4title B_p4f B_hands B_p4b B_cover B_p4e3 B_hud B_perf B_p3 B_fights1 B_interact B_cb B_mine B_tour B_walkt B_walkf}
# estimated minutes per block (candidate 9's reserved run logs/chain_cand9.log; env 1's pt3 runs logs/pt3/chain_reg*.log;
# env 2's p3i.log and env 3's hudr2 / hudv3b logs, less their fair-turn waits)
declare -A EST=([B_p3re]=12 [B_gnomes]=5 [B_dice]=10 [B_hands]=25 [B_cover]=25 [B_scan]=1 [B_pre]=6 [B_p4t]=10 [B_p4title]=3 [B_p4f]=10 [B_p4b]=25 [B_p4e3]=20 [B_cab]=20 [B_p4ass]=5 [B_blade]=12 [B_hud]=6 [B_perf]=6 [B_p3]=25 [B_fights1]=25 [B_fights2]=25 [B_focus]=5 [B_tour]=13
  [B_tour_seated]=13 [B_cb]=24 [B_mine]=45 [B_interact]=20 [B_side]=12 [B_n1]=1 [B_limbo]=5 [B_walkt]=36 [B_walkf]=50)
# what only the playtest-4 branches and the two 11.1 branches bring (the chain refuses to start without it)
HUDONLY="scripts/p4_test.py scripts/p4b_test.py scripts/p4e3_test.py src/HoF2VR/Game/GnomePosts.cs src/HoF2VR/Game/ShieldAxis.cs src/HoF2VR/Game/HudMirror.cs scripts/chains/hud/perf.sh scripts/handsize_test.py src/HoF2VR/Diag/HandSize.cs"
# (and the two 11.1 branches: p3combat's 55 % rule - p4b_test's cover part and its `coverat` seam; hud's PlayerSize)
newonly() { grep -q 'coverat' scripts/p4b_test.py && grep -q '"Table", "PlayerSize"' src/HoF2VR/Settings.cs; }
export XAUTHORITY=${XAUTHORITY:-$(ls -t /run/user/1000/xauth_* 2>/dev/null | head -1)}
export HOF2VR_RESERVE_MAX_H=8
NORESERVE=${NORESERVE:-0}

# ---- helpers (candidate 9's) ----
say() { echo "=== $* ($(date +%T))" | tee -a "$LOG"; }
sha() { sha1sum < "$1" 2>/dev/null | cut -d' ' -f1; }
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
      [ "$NORESERVE" = 1 ] || scripts/reserve.sh stop > /dev/null 2>&1
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
    [ "$NORESERVE" = 1 ] || scripts/reserve.sh start $$ >> "$LOG" 2>&1
  fi
}
GPUPAT='amdgpu.*(page fault|timeout|wedged|reset|VRAM is lost)|GPU reset|ring .* timeout'
gpu() {
  local n; n=$(journalctl -k --since "@$1" --no-pager 2>/dev/null | grep -ciE "$GPUPAT")
  [ "$n" = 0 ] && return 0
  say "GPU FAULT: $n lines in the kernel log during $2: $(journalctl -k --since "@$1" --no-pager 2>/dev/null | grep -iE "$GPUPAT" | head -3 | cut -c1-200 | tr '\n' ' ')"
  say "GPU FAULT: stopping all testing (the game, Monado) and ending the chain - tell the coordinator, reboot before VR"
  scripts/stop.sh > /dev/null 2>&1; scripts/monado.sh stop > /dev/null 2>&1
  touch $T/cand11_1_gpu_fault
  return 3
}
run_block() {
  local name=$1 n=0 rc t0; shift
  while :; do
    waitout
    rm -f $MARK
    say "block $name (est ~${EST[$name]:-?} min)"
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
    say "block $name took $(( ($(date +%s) - t0) / 60 )) min"
    return $rc
  done
}
state() { python3 scripts/cmd.py 'state?' 5 2>/dev/null; }
menu() { python3 scripts/cmd.py --status 2>/dev/null | python3 -c 'import sys, json; print(json.load(sys.stdin).get("menu", ""))' 2>/dev/null; }
inafight() { state | grep -q Level_Play; }
atsetup() { state | grep -q ChallengeSetup; }
attable() { state | grep -q GameState_Map && [ "$(menu)" = "Map" ]; }
x() { "$@" >> "$O/misc.out" 2>&1; }                       # a command whose output only goes to misc.out
xa() { local o=$1; shift; "$@" >> "$o" 2>&1; }            # a command appending to its own file
xo() { local o=$1; shift; "$@" > "$o" 2>&1; }             # a command writing its own file (its rc returned)
stopgame() { savelog; scripts/stop.sh > /dev/null 2>&1; }
c() { x python3 scripts/cmd.py "$@"; }                    # a seam
savelog() { cp "$GAMELOG" "$O/LogOutput-$(date +%H%M%S).log" 2>/dev/null; }
# the HUD combo: board_shell = the player's settings (Board + Shell by DefaultsVersion 4), body_rings = the seams' override
# (per game process: re-applied after every session and before every test that ensure_table prepares)
COMBO=board_shell
applycombo() {
  case $COMBO in
    body_rings) echo "$(python3 scripts/cmd.py 'vrhud body' 5 2>&1) / $(python3 scripts/cmd.py 'vrstatus rings' 5 2>&1)" ;;
    *) echo "$(python3 scripts/cmd.py 'vrhud setting' 5 2>&1) / $(python3 scripts/cmd.py 'vrstatus setting' 5 2>&1)" ;;
  esac
}
combo() { COMBO=$1; say "HUD combo $COMBO: $(applycombo | cut -c1-80)"; }
table() { savelog; xa $O/sessions.out python3 scripts/session.py start --snapshot magician_mid --to table; say "session at the table: $(state | cut -c1-50) | $(applycombo | cut -c1-60)"; }
# a session at the caravan (the magician save: GameState_Attract)
hub() { savelog; xa $O/sessions.out python3 scripts/session.py start --snapshot magician --to table; say "session at the caravan: $(state | cut -c1-50) / $(menu)"; }
title() { savelog; xa $O/sessions.out python3 scripts/session.py start --snapshot magician_mid; say "session at the title: $(state | cut -c1-50)"; }
leave() { xa $O/leave.out python3 -c "import sys; sys.path.insert(0, 'scripts'); import indicator_shots; print('left:', indicator_shots.leave())"; }
# at the table with the map ready (no encounter or activation left open: seated_check leaves one, and the fixtures' jump
# is refused until it is finished) - a fight left running (--stay) is left first (env 2/3's lib.sh), else a fresh session
ensure_table() {
  if inafight; then leave; sleep 3; fi
  if attable; then
    if [ "$COMBO" = board_shell ]; then x applycombo; else say "  combo: $(applycombo | cut -c1-60)"; fi
  else say "ensure_table: $(state | cut -c1-40), menu '$(menu)' - a fresh session"; table; fi
}
# press A at the caravan until the challenge map is up
to_challenge_map() {
  xa $O/sessions.out python3 -c "
import sys, time; sys.path.insert(0, 'scripts'); import cmd as C, p2fix as P
for i in range(3):
    time.sleep(2); C.send('press A 6', 5)
    if P.wait_menu('ChallengeMap', 25): break
time.sleep(5)"
  say "at the challenge map: $(state | cut -c1-40) / $(menu)"
}
score() { tail -1 "$1" | grep -oE '[0-9]+/[0-9]+' | head -1; }
fails() { grep -aE '^..:..:.. FAIL' "$1" | cut -c1-300 | sed 's/^/    /' >> "$LOG"; }
pick() { grep -aE -e "$1" "$2" 2>/dev/null | cut -c${4:-1}-${3:-260} | sed 's/^/    /' >> "$LOG"; }   # PATTERN FILE [END] [START]
res() { local name=$1 rc=$2 out=$3; say "$name rc $rc | $(tail -1 "$out" | cut -c1-160)"; fails "$out"; }
t() { local name=$1 out=$2; shift 2; "$@" > "$out" 2>&1; res "$name" $? "$out"; }
exc() { say "$1: the game log's exceptions $(grep -c Exception "$GAMELOG"), [steam] reached $(grep -c '\[steam\] reached' "$GAMELOG"); plugins DLL $(sha "$P" | cut -c1-8)"; }
tourres() { say "$1 | $(grep '====' "$2" | tail -1 | cut -c1-40) | FAILs $(grep -c '^..:..:.. FAIL' "$2")"; fails "$2"; exc "$1"; }
keepshots() { mkdir -p testdata/shots/$1; find testdata/shots -maxdepth 1 -name 'PT_*' -newer "$2" -exec cp -p {} testdata/shots/$1/ \; ; say "shots kept: testdata/shots/$1 ($(ls testdata/shots/$1 | wc -l))"; }
p2res() { say "$1 | $(grep -E 'RESULT' "$2" | cut -c10-200 | tr '\n' ' ' | cut -c1-400)"; grep -E 'RESULT .* FAIL' "$2" | cut -c1-300 | sed 's/^/    /' >> "$LOG"; }
walkres() { pick 'HEADWALK' "$1" 400 10; }
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
untouched() { echo "cfg $(sha "$CFG" | cut -c1-12) | Unity prefs $(sha "$PREFS" | cut -c1-12) | DLC record $(sha "$DLCF" | cut -c1-12)"; }

# ---- the blocks ----
B_scan() {
  t "uyf_scan" $O/uyf_scan.out python3 scripts/uyf_scan.py
  t "savecheck before" $O/savecheck1.out bash scripts/savecheck.sh
}
B_pre() {
  table
  t "pf pre: N2 edgeturn, N3 swarmboss (F8 probe)" $O/pre.out bash scripts/chains/cand9/pre.sh
  pick '(PASS|FAIL) ' $O/pre.out
  leave
  exc "pre"
}
# ---- playtest 4 (2026-10-19): the three envs' tests, pictures into $PICS/p4, $PICS/p4b, $PICS/p4e3 ----
# env 1's p4_test (NOTES "Playtest 4 fixes (env 1, 2026-10-19)"; /tmp/claude-hof2/p4_run.sh's steps): the grip first in a fresh
# session (P4-5: no trigger touch yet), then the menus at the table; the title's settings; a fight and a night fight
B_p4t() {
  table
  t "p4_test table: grip,calib,credits,settings,dialogues,sliders,encounter" $O/p4_table.out python3 scripts/p4_test.py --parts grip,calib,credits,settings,dialogues,sliders,encounter --pics $PICS/p4
  exc "p4_test table"
}
B_p4title() {
  title
  t "p4_test title" $O/p4_title.out python3 scripts/p4_test.py --parts title --pics $PICS/p4
  exc "p4_test title"
}
B_p4f() {
  table
  t "p4_test fight: Board before/now (the artefact dial), hand badges + prompt, glow by day" $O/p4_fight.out python3 scripts/p4_test.py --parts fight --pics $PICS/p4
  exc "p4_test fight"
  leave; table
  t "p4_test night: the glow and the Board at night" $O/p4_night.out python3 scripts/p4_test.py --parts night --pics $PICS/p4
  leave
  exc "p4_test night"
}
# env 3's glove cut to HL:A's size (scripts/chains/hands/turn.sh's steps "after size hub"; NOTES "The glove cut to HL:A's
# size + the player's size at the table (env 3, 2026-10-20)"): the glove's drawn size and pictures at the table, the hand
# check's fit page and in a fight; the player's size 1.06 / 1.0 at the table (the `playersize` seam, this run only, never
# saved) with the encounter's card; the same at the caravan's side table
B_hands() {
  table
  t "handsize after: table,fit,fight (+pics)" $O/hands_after.out python3 scripts/handsize_test.py --tag after --parts table,fit,fight --pics $PICS/hands
  leave
  exc "handsize after"
  table
  t "handsize size,encounter (PlayerSize 1.06 / 1.0)" $O/hands_size.out python3 scripts/handsize_test.py --tag after --parts size,encounter --pics $PICS/hands
  exc "handsize size"
  hub
  t "handsize hub (the side table at 1.06 / 1.0)" $O/hands_hub.out python3 scripts/handsize_test.py --tag after --parts hub --pics $PICS/hands
  exc "handsize hub"
  table   # (back at the table at the cfg's size: a fresh session)
}
# env 2's p4b_test (scripts/chains/p4/chain_p4b.sh's X="S B H E"; NOTES "Playtest 4 blocking fixes (env 2, 2026-10-19)")
B_p4b() {
  combo board_shell
  ensure_table; t "p4b S thieves: shield,circle,edge,threat (+pics)" $O/p4bS.out env P4B_PICS=$PICS/p4b python3 scripts/p4b_test.py --tag C111P4S --fixture thieves --stay --pics --parts shield,circle,edge,threat
  ensure_table; t "p4b B assassins: assassin,green (+pics)" $O/p4bB.out env P4B_PICS=$PICS/p4b python3 scripts/p4b_test.py --tag C111P4B --fixture behind --stay --pics --parts assassin,green
  ensure_table; t "p4b H thieves, no shield: bladedmg" $O/p4bH.out python3 scripts/p4b_test.py --tag C111P4H --fixture thieves --equip HugeHammer --stay --parts bladedmg
  leave; table   # (the two-handed hammer stays for the session: a fresh one)
  t "p4b E thieves: exit (the game's sign-out under the held exit)" $O/p4bE.out python3 scripts/p4b_test.py --tag C111P4E --fixture thieves --stay --parts exit
  table          # (a fresh session after the reset)
  exc "p4b"
}
# env 2's 55 % rule (scripts/chains/p4/chain_p4b.sh's K U W; NOTES "The 55 % rule (env 2, 2026-10-20)"): the coverage table
# frozen at a wind-up (`timescale 0 hold`), the shield turned toward edge-on, live blows; green == blocked == the rule per
# record - the basic shield (and edge), a buckler, a broad shield
B_cover() {
  combo board_shell
  ensure_table; t "p4b K thieves, BasicShield: cover,edge (+pics)" $O/p4bK.out env P4B_PICS=$PICS/cover python3 scripts/p4b_test.py --tag C111P4K --fixture thieves --stay --pics --parts cover,edge
  ensure_table; t "p4b U thieves, Buckler: cover (+pics)" $O/p4bU.out env P4B_PICS=$PICS/cover python3 scripts/p4b_test.py --tag C111P4U --fixture thieves --equip BasicSword_noToken,Buckler --stay --pics --parts cover
  leave; table   # (the buckler stays for the session: a fresh one)
  t "p4b W thieves, BroadShield: cover (+pics)" $O/p4bW.out env P4B_PICS=$PICS/cover python3 scripts/p4b_test.py --tag C111P4W --fixture thieves --equip BasicSword_noToken,BroadShield --stay --pics --parts cover
  leave; table   # (the broad shield: a fresh session)
  exc "p4b cover"
}
# the assassins' landings again with a picture of each (the first run's one picture was a landing where the ground ended)
B_p4ass() {
  combo board_shell
  ensure_table; t "p4b assassins: assassin (a picture per landing)" $O/p4bA.out env P4B_PICS=$PICS/p4b python3 scripts/p4b_test.py --tag C111P4A --fixture behind --stay --pics --parts assassin
  leave
  exc "p4b assassin"
}
# env 3's p4e3_test (scripts/chains/p4e3/chain.sh's T; NOTES "Playtest 4 crit and gnome fixes (env 3, 2026-10-19)"): the crit
# cuts from every stand, pictures of four, the angles; the gnomes' posts, a ground gnome hit, the clinging gnome (0.92 m)
B_p4e3() {
  combo board_shell
  ensure_table
  # (two runs: the skeletons' game log is saved before the gnome fixture's boot overwrites it - env 3's trap)
  t "p4e3 crit,critpics,angles (pictures)" $O/p4e3_crit.out env PICS_OUT=$PICS/p4e3 python3 scripts/p4e3_test.py --tag C111P4E3c --stay --pics --parts crit,critpics,angles
  savelog
  t "p4e3 posts,ground,cling (pictures)" $O/p4e3_gnomes.out env PICS_OUT=$PICS/p4e3 python3 scripts/p4e3_test.py --tag C111P4E3g --stay --pics --parts posts,ground,cling
  leave
  exc "p4e3"
}
# blocking with the weapon (candidate 11: p4b H bladedmg's two scripted weapon blocks with the two-handed hammer were hit, in env 2's
# run too; no chain had run stage1's bladeblock since): the sword along the bar (thieves), the hammer along the bar; BLADE_OLD=<a
# DLL file>: the same with that DLL (candidate 10's release) in a fresh session, then the candidate put back
B_blade() {
  combo board_shell
  ensure_table; t "stage1 bladeblock x3, the sword (thieves)" $O/blade_sword.out python3 scripts/stage1_test.py --tag C111BS --fixture thieves --equip $GEAR --stay --parts bladeblock,bladeblock,bladeblock
  ensure_table; t "stage1 bladeblock x3, the two-handed hammer (thieves)" $O/blade_hammer.out python3 scripts/stage1_test.py --tag C111BH --fixture thieves --equip HugeHammer --stay --parts bladeblock,bladeblock,bladeblock
  leave
  exc "blade"
  if [ -n "${BLADE_OLD:-}" ] && [ -f "$BLADE_OLD" ]; then
    stopgame
    putdll "$BLADE_OLD"
    xa $O/sessions.out python3 scripts/session.py start --snapshot magician_mid --to table --no-install
    say "BLADE_OLD at the table: $(sha "$P" | cut -c1-8) ($BLADE_OLD)"
    t "BLADE_OLD: stage1 bladeblock x3, the sword" $O/blade_sword_old.out python3 scripts/stage1_test.py --tag C111BSo --fixture thieves --equip $GEAR --stay --parts bladeblock,bladeblock,bladeblock
    leave; sleep 3
    t "BLADE_OLD: stage1 bladeblock x3, the hammer" $O/blade_hammer_old.out python3 scripts/stage1_test.py --tag C111BHo --fixture thieves --equip HugeHammer --stay --parts bladeblock,bladeblock,bladeblock
    leave
    exc "blade (old DLL)"
    stopgame; putdll build/bin/HoF2VR.dll
    say "the candidate put back: $(sha "$P" | cut -c1-8)"
  fi
}
# the shield block A/B (candidate 11's r1 C: stage1 block x3 1/3 - two blows with the shield on the bar refused by the facing
# rule, others with the shield 0.5-1 m off the bar at the impact): the candidate as is; with the old shield reach (`ringframe d
# 0.5`: the reach back to ~0.26 m, P4-13 undone for the judge); with P4-15's rule off (`vrthreat off`); CAB_OLD=<dll>: the
# old DLL (candidate 10's release); all of these place the shield once (S1_SHIELD_REGUARD=0, as before); then C and G with the
# shield kept on the moving bar (the re-guard)
B_cab() {
  combo board_shell
  ensure_table; t "C block x3 (the candidate)" $O/cab_new.out env S1_SHIELD_REGUARD=0 python3 scripts/stage1_test.py --tag C111CAn --fixture thieves --equip $GEAR --stay --parts block,block,block
  ensure_table
  c "ringframe d 0.5" 5; say "  ringframe: $(python3 scripts/cmd.py 'ringframe' 5 2>&1 | cut -c1-120)"
  t "C block x3 (the old shield reach: ringframe d 0.5)" $O/cab_reach.out env S1_SHIELD_REGUARD=0 python3 scripts/stage1_test.py --tag C111CAr --fixture thieves --equip $GEAR --stay --parts block,block,block
  ensure_table
  c "ringframe d 0.3" 5; c "vrthreat off" 5; say "  vrthreat: $(python3 scripts/cmd.py 'vrthreat' 5 2>&1 | cut -c1-120)"
  t "C block x3 (P4-15's threat rule off)" $O/cab_threat.out env S1_SHIELD_REGUARD=0 python3 scripts/stage1_test.py --tag C111CAt --fixture thieves --equip $GEAR --stay --parts block,block,block
  leave; c "vrthreat on" 5
  # the test's shield kept on the moving bar (stage1's re-guard 0.25 s before the blow, as for the blade): C and G
  t "C block x3 (the shield kept on the bar: S1 re-guard)" $O/cab_reguard.out python3 scripts/stage1_test.py --tag C111CAg --fixture thieves --equip $GEAR --stay --parts block,block,block
  ensure_table
  t "G redbar,redlean (the shield kept on the bar)" $O/cab_g.out python3 scripts/stage1_test.py --tag C111CAG --fixture dodge --equip $GEAR --stay --parts redbar,redlean
  leave
  exc "cab"
  if [ -n "${CAB_OLD:-}" ] && [ -f "$CAB_OLD" ]; then
    stopgame
    putdll "$CAB_OLD"
    xa $O/sessions.out python3 scripts/session.py start --snapshot magician_mid --to table --no-install
    say "CAB_OLD at the table: $(sha "$P" | cut -c1-8) ($CAB_OLD)"
    t "C block x3 (CAB_OLD)" $O/cab_old.out env S1_SHIELD_REGUARD=0 python3 scripts/stage1_test.py --tag C111CAo --fixture thieves --equip $GEAR --stay --parts block,block,block
    leave; sleep 2
    t "C block x3 (CAB_OLD, again)" $O/cab_old2.out env S1_SHIELD_REGUARD=0 python3 scripts/stage1_test.py --tag C111CAo2 --fixture thieves --equip $GEAR --stay --parts block,block,block
    leave
    exc "cab (old DLL)"
    stopgame; putdll build/bin/HoF2VR.dll
    say "the candidate put back: $(sha "$P" | cut -c1-8)"
  fi
}
# env 3's HUD tests (scripts/chains/hud/chain_hud.sh, v3b.sh): hud_test's every part (it switches Board / Body and Shell / Rings
# itself, the settings page's switches live mid-fight), then perf.sh hud (the big fight: Shell, every shell worn to 40 %,
# Rings; 900 frames x2 each)
B_hud() {
  combo board_shell
  ensure_table
  t "hud_test thieves: flat,board,body,shell,rings,switch" $O/hud.out python3 scripts/hud_test.py --tag C111H --fixture thieves --equip $GEAR --stay --parts flat,board,body,shell,rings,switch
  exc "hud_test"
}
B_perf() {
  combo board_shell
  ensure_table
  t "perf big: this build (shell, worn, rings)" $O/perf.out bash scripts/chains/hud/perf.sh hud
  pick 'perf 900 perf ' $O/perf.out 230 10
  if [ -n "${PERF_OLD:-}" ] && [ -f "$PERF_OLD" ]; then
    leave; stopgame
    putdll "$PERF_OLD"
    xa $O/sessions.out python3 scripts/session.py start --snapshot magician_mid --to table --no-install
    say "PERF_OLD at the table: $(sha "$P" | cut -c1-8) ($PERF_OLD)"
    t "perf big: PERF_OLD's DLL" $O/perf_old.out bash scripts/chains/hud/perf.sh old
    pick 'perf 900 perf ' $O/perf_old.out 230 10
    stopgame; putdll build/bin/HoF2VR.dll
    say "the candidate put back: $(sha "$P" | cut -c1-8)"
  fi
  exc "perf"
}
# env 2's playtest-3 fight parts (scripts/chains/p3/chain_p3c.sh: M S R T, and Q V L); T last (its two-handed hammer stays
# for the session: a fresh one after it)
B_p3() {
  combo board_shell
  ensure_table; t "p3 M mix: tiers,refill,damage,reverse,lined,glow,stick,shield" $O/p3M.out python3 scripts/p3_test.py --tag C111M --fixture mix --equip $GEAR --stay --parts tiers,refill,damage,reverse,lined,glow,stick,shield
  ensure_table; t "p3 S soldiers: mirror,ring" $O/p3S.out python3 scripts/p3_test.py --tag C111S --fixture shield --equip $GEAR --stay --parts mirror,ring
  ensure_table; t "p3 R thieves: ring" $O/p3R.out python3 scripts/p3_test.py --tag C111R --fixture thieves --equip $GEAR --stay --parts ring
  ensure_table; t "p3 Q thieves: postbreak,pause" $O/p3Q.out python3 scripts/p3_test.py --tag C111Q --fixture thieves --equip $GEAR --stay --parts postbreak,pause
  ensure_table; t "p3 V dodge2: leanarrow" $O/p3V.out python3 scripts/p3_test.py --tag C111V --fixture dodge2 --equip $GEAR --stay --parts leanarrow
  ensure_table; t "p3 L lobs with a shield: groundgold" $O/p3L.out python3 scripts/p3_test.py --tag C111L --fixture lobs --equip $GEAR --stay --parts groundgold
  ensure_table; t "p3 T tutorial skeletons + throwers, no shield: pips,ground" $O/p3T.out python3 scripts/p3_test.py --tag C111T --fixture tut --equip HugeHammer --stay --parts pips,ground
  leave; table
  exc "p3"
}
# chain_pf's blocks (as candidate 9 and env 2/3's chains run them), one round in the given HUD combo
B_fights() {
  local i=$1
  combo $2
  ensure_table; t "r$i A shadows leadhold,combogaps" $O/A$i.out python3 scripts/audit_test.py --tag C111${i}A --fixture shadows --equip $GEAR --stay --parts leadhold,combogaps
  ensure_table; t "r$i B thieves waiting,waiting,dash,strafe,health" $O/B$i.out python3 scripts/audit_test.py --tag C111${i}B --fixture thieves --equip $GEAR --stay --parts waiting,waiting,dash,strafe,health
  ensure_table; t "r$i C thieves stage1 block x3" $O/C$i.out python3 scripts/stage1_test.py --tag C111${i}C --fixture thieves --equip $GEAR --stay --parts block,block,block
  if inafight; then t "r$i C move_test (same fight)" $O/M$i.out python3 scripts/move_test.py --tag C111${i}M --no-fixture; else say "r$i move_test: the fight is over"; fi
  ensure_table; t "r$i D combo, interruptcut (combo fixture)" $O/D$i.out python3 scripts/stage2_test.py --tag C111${i}D --fixture combo --equip $GEAR --stay --parts combo,interruptcut
  ensure_table; t "r$i F thieves linecut,crit,wrong,stickuse" $O/F$i.out python3 scripts/stage2_test.py --tag C111${i}F --fixture thieves --equip $GEAR --stay --parts linecut,crit,wrong,stickuse
  ensure_table; t "r$i G dodge fixture redbar,redlean" $O/G$i.out python3 scripts/stage1_test.py --tag C111${i}G --fixture dodge --equip $GEAR --stay --parts redbar,redlean
  ensure_table; t "r$i P lobs knock,drop,dodge,offview" $O/P$i.out python3 scripts/bomb_test.py --tag C111${i}P --stay --parts knock,drop,dodge,offview
  ensure_table; t "r$i N gnomes watch,cut,dash,cling" $O/N$i.out python3 scripts/gnome_test.py --tag C111${i}N --stay --parts watch,cut,dash,cling
  leave
  exc "fights round $i ($2)"
}
B_fights1() { B_fights 1 board_shell; }
B_fights2() { B_fights 2 body_rings; }
B_focus() {
  savelog
  t "E focus_test (autoplay's walk from a fresh table)" $O/E.out python3 scripts/focus_test.py
  exc "focus"
}
B_tour() {
  x touch $T/cand11_1_t0
  savelog
  xo $O/tour.out python3 scripts/playtest.py; tourres "tour standing rc $?" $O/tour.out
  say "tour standing PROXIMITY lines: $(grep -c PROXIMITY "$GAMELOG" 2>/dev/null)"
  keepshots cand11_1_standing $T/cand11_1_t0
}
B_tour_seated() {
  x touch $T/cand11_1_t1
  savelog
  xo $O/tour_seated.out python3 scripts/playtest.py --seated; tourres "tour seated rc $?" $O/tour_seated.out
  say "seated reach: $(grep 'seated_touch_within_reach' $O/tour_seated.out 2>/dev/null | tail -1 | cut -c10-160)"
  say "tour seated PROXIMITY lines: $(grep -c PROXIMITY "$GAMELOG" 2>/dev/null)"
  keepshots cand11_1_seated $T/cand11_1_t1
}
# the seated call to battle (NOTES "The seated call to battle"): repeated seated x10 and a fresh map's deal with the head down,
# standing x5 (each its own session; `prox reset` before each call to battle)
B_cb() {
  savelog
  t "cb_seated_test --seated x10 --deal" $O/cb_seated.out python3 scripts/cb_seated_test.py --start --reps 10 --pics 2 --deal
  t "cb_seated_test --standing x5" $O/cb_standing.out python3 scripts/cb_seated_test.py --start --standing --reps 5 --pics 1
  exc "cb"
}
# candidate 9's B_mine (its table half) with the bug hunt's additions (the wheel: finding 5; audit_fixes_test: 7, 10, 11, 12)
B_mine() {
  combo board_shell
  ensure_table
  t "wheel_look --no-hub" $O/wheel.out python3 scripts/wheel_look.py --no-hub
  t "b1 --start standing" $O/b1.out python3 scripts/b1_test.py --start
  t "b1 --start --seated" $O/b1_seated.out python3 scripts/b1_test.py --start --seated
  t "seated_check" $O/seated_check.out python3 scripts/seated_check.py
  t "switches_test --start (a fresh session)" $O/switches.out python3 scripts/switches_test.py --start
  exc "wheel / b1 / seated / switches"
  table
  t "audit_fixes_test (bug hunt 7, 10, 11, 12)" $O/audit_fixes.out python3 scripts/audit_fixes_test.py
  exc "audit fixes"
}
# every interact group in a fresh session at the table (run F of 10-19 00:32: seated_check's open encounter failed them);
# figurine_drop's 'beside' half fails in the map its 'on' half moved in (a fight opens: test), so beside again on a fresh map
B_interact() {
  table; t "interact figurine_pinch dice_reroll_vr card_flip" $O/interact.out python3 scripts/interact_test.py figurine_pinch dice_reroll_vr card_flip
  table; t "interact touch_mark dice_multi (P2-3: taps from above)" $O/interact_dice.out python3 scripts/interact_test.py touch_mark dice_multi
  table; t "interact figurine (a fresh map: pick up, set down)" $O/interact_fig.out python3 scripts/interact_test.py figurine
  table; t "interact figurine_real (P2-2, on the map)" $O/interact_figreal.out python3 scripts/interact_test.py figurine_real
  table; t "interact figurine_drop (playtest 3, mark 9: on, beside)" $O/interact_figdrop.out python3 scripts/interact_test.py figurine_drop
  table; t "interact figurine_drop_beside (a fresh map)" $O/interact_figdrop_beside.out python3 scripts/interact_test.py figurine_drop_beside
  ensure_table
  t "fixtests: sliders (hand check, VR settings), T6" $O/fixtests.out python3 scripts/chains/cand8/fixtests.py
  exc "interact / fixtests"
}
# the side table's P2 checks (NOTES "Playtest 2 fixes (env 1)"), then again in the screens' order (side.py)
B_side() {
  hub
  c "timescale 0" 5
  xo $O/p2_still.out python3 scripts/p2fix.py still attract_frozen; p2res "P2-1 still attract (the game frozen: held cards and the head)" $O/p2_still.out
  c "timescale 1" 5
  to_challenge_map
  xo $O/p2_pin.out python3 scripts/p2fix.py pin P2-8_pin_c10; p2res "P2-8 pin (a fingertip onto a pin opens its deck builder)" $O/p2_pin.out
  xo $O/p2_hubwalk.out timeout 600 python3 scripts/p2fix.py hubwalk P2hub_c10; p2res "P2-4..P2-8 hubwalk (rc $?; the swipe test, the opening)" $O/p2_hubwalk.out
  pick 'no deck builder|no challenge setup|setup:|after Recommend|no inventory' $O/p2_hubwalk.out 240
  xa $O/p2_opening.out python3 scripts/chains/cand9/opening.py
  say "challenge opening: $(tail -1 $O/p2_opening.out 2>/dev/null | cut -c1-200)"
  if atsetup; then
    t "interact figurine_real (P2-2, at the challenge's opening)" $O/interact_figreal_setup.out python3 scripts/interact_test.py figurine_real
    t "interact figurine_drop (mark 9, at the challenge's opening)" $O/interact_figdrop_setup.out python3 scripts/interact_test.py figurine_drop
  else say "figurine_real / figurine_drop at the challenge's opening: not reached ($(state | cut -c1-40) / $(menu))"; fi
  exc "P2 side table"
  hub
  t "side table again: P2-8 pin, P2-5 swipe in Build, P2-2 figurine_real at the opening" $O/side.out timeout 900 python3 scripts/chains/cand9/side.py
  pick 'RESULT|PASS|FAIL|passed|->|opening:' $O/side.out 260 10
  exc "side table"
}
n1src() { pactl list sources short 2>/dev/null | grep -q hof2vr_wivrn_test && say "N1: the stand-in source is still loaded!" || say "N1: the stand-in source is gone"; }
B_n1() {
  savelog
  x python3 scripts/session.py stop
  xo $O/n1.out bash scripts/chains/pt17_n1.sh
  say "N1 voice in the Steam runtime: $(grep -E 'recorded from|no voice|no headset' $O/n1.out 2>/dev/null | tail -1 | cut -c1-140) | $(grep 'container:' $O/n1.out 2>/dev/null | cut -c1-140)"
  n1src
  exc "N1"
}
B_limbo() {
  local rc
  savelog
  xo $O/limbo.out python3 scripts/limbo_test.py --snapshot first_fight --tag L111; rc=$?
  say "limbo_test rc $rc | states: $(grep -oE 'GameState_[A-Za-z_]+' $O/limbo.out 2>/dev/null | uniq | tr '\n' ' ' | cut -c1-300)"
  pick 'progress: fight_in done|gate: go' $O/limbo.out 200
  exc "limbo"
}
# the picture walk with the head moving (headwalk.py: at rest, turned 30 deg each way, leaned 20 cm forward and right, back at
# rest; the mod's draw-time moves and its objects checked to stay put) - out of fights
B_walkt() {
  title
  t "looks: the title, turned left" $O/looks_title.out timeout 200 bash scripts/chains/cand9/looks.sh title
  pick 'turned left' $O/looks_title.out 600
  t "walk: the title and the table's places" $O/walk.out timeout 2400 python3 scripts/chains/cand9/walk.py table
  walkres $O/walk.out
  exc "walk (table)"
  table
  t "campfire before a fight (the head walk at READY; the straight way accepted)" $O/campfire.out timeout 400 python3 scripts/chains/cand9/campfire.py
  walkres $O/campfire.out
  hub
  t "walk: the map room / side table (challenge map, deck builder; the Adventure tab)" $O/walk_hub.out timeout 900 python3 scripts/chains/cand9/walk.py hub adventure
  walkres $O/walk_hub.out
  exc "walk (side table)"
}
# ... and the fights: candidate 9's five (Board + Shell), then the new ones (cand11/fightpics.sh), HUD pictures' sheet
B_walkf() {
  combo board_shell
  ensure_table
  t "walk: the fights (thieves, soldiers, gnomes, bombs, boss)" $O/fightpics.out env FIGHTPICS_O=$O timeout 2700 bash scripts/chains/cand9/fightpics.sh
  walkres $O/fightpics.out
  pick 'FAIL|gnome fight with' $O/fightpics.out 200
  exc "walk (cand9's fights)"
  local f
  for f in shield archers lobs_red lobs_gold night; do
    ensure_table
    t "walk: fight $f" $O/fightpics_$f.out env FIGHTPICS_O=$O timeout 900 bash scripts/chains/cand11/fightpics.sh $f
    walkres $O/fightpics_$f.out
    pick '(PASS|FAIL) |combo |hud_pics:' $O/fightpics_$f.out 220
    [ $f = lobs_red ] && { leave; table; }   # (the two-handed hammer: a fresh session)
  done
  xa $O/hud_sheet.out python3 scripts/hud_pics.py sheet
  say "HUD sheet: $(tail -1 $O/hud_sheet.out 2>/dev/null | cut -c1-160)"
  say "pictures in $PICS: $(find "$PICS" -name '*.jpg' 2>/dev/null | wc -l)"
  leave
  exc "walk (fights)"
}

# ---- candidate 11.1's reruns after its test fixes (CAND11_1_BLOCKS="B_cover B_p4e3 B_p3re B_gnomes B_dice", a second log):
# p3 S and R (ring: the on-circle blow placed under the hold too), Q (postbreak: a refill cut by a block is measured again);
# the gnomes' cut (no single 30 s wait while a gnome clings); interact's first group twice and the dice group (the dice
# re-roll's tap that marked nothing, 3/6 in the first run)
B_p3re() {
  combo board_shell
  ensure_table; t "p3 S soldiers: mirror,ring" $O/p3S.out python3 scripts/p3_test.py --tag C111S --fixture shield --equip $GEAR --stay --parts mirror,ring
  ensure_table; t "p3 R thieves: ring" $O/p3R.out python3 scripts/p3_test.py --tag C111R --fixture thieves --equip $GEAR --stay --parts ring
  ensure_table; t "p3 Q thieves: postbreak,pause" $O/p3Q.out python3 scripts/p3_test.py --tag C111Q --fixture thieves --equip $GEAR --stay --parts postbreak,pause
  leave
  exc "p3 rerun"
}
B_gnomes() {
  combo board_shell
  ensure_table; t "r1 N gnomes watch,cut,dash,cling" $O/N1.out python3 scripts/gnome_test.py --tag C1111N --stay --parts watch,cut,dash,cling
  leave
  exc "gnomes"
}
B_dice() {
  table; t "interact figurine_pinch dice_reroll_vr card_flip" $O/interact.out python3 scripts/interact_test.py figurine_pinch dice_reroll_vr card_flip
  table; t "interact figurine_pinch dice_reroll_vr card_flip (again)" $O/interact2.out python3 scripts/interact_test.py figurine_pinch dice_reroll_vr card_flip
  table; t "interact touch_mark dice_multi" $O/interact_dice.out python3 scripts/interact_test.py touch_mark dice_multi
  exc "interact dice"
}

# ---- --dry: the blocks and their commands, nothing run ----
if [ $DRY = 1 ]; then
  # every block's command goes through these (no redirection of its own in a block: a dry run never writes a file)
  say() { :; }
  x() { echo "    \$ $*"; }
  xa() { local o=$1; shift; echo "    \$ $* >> ${o#$H/}" | cut -c1-220; }
  xo() { local o=$1; shift; echo "    \$ $* > ${o#$H/}"; }
  t() { local name=$1 out=$2; shift 2; echo "  - $name"; echo "    \$ $* > ${out#$H/}"; }
  c() { echo "    \$ python3 scripts/cmd.py '$1'"; }
  python3() { echo "    \$ python3 $*" | cut -c1-200; }   # (safety nets: nothing runs)
  bash() { echo "    \$ bash $*"; }
  stopgame() { echo "    \$ scripts/stop.sh"; }
  n1src() { echo "    \$ pactl list sources short   (the stand-in source gone?)"; }
  state() { echo "(state)"; }
  menu() { echo "(menu)"; }
  inafight() { return 0; }
  atsetup() { return 0; }
  attable() { return 0; }
  applycombo() { echo "(the combo's seams: $COMBO)"; }
  combo() { COMBO=$1; echo "  = HUD combo $1 ($([ "$1" = body_rings ] && echo 'vrhud body, vrstatus rings' || echo 'vrhud setting, vrstatus setting = Board + Shell'))"; }
  table() { echo "    \$ session.py start --snapshot magician_mid --to table   (+ the combo's seams)"; }
  hub() { echo "    \$ session.py start --snapshot magician --to table   (the caravan)"; }
  title() { echo "    \$ session.py start --snapshot magician_mid   (the title)"; }
  leave() { echo "    \$ indicator_shots.leave()"; }
  ensure_table() { echo "    \$ ensure_table   (leave a fight; a fresh session unless at the map; the combo's seams)"; }
  to_challenge_map() { echo "    \$ press A until the challenge map (p2fix.wait_menu)"; }
  pick() { :; }; fails() { :; }; res() { :; }; exc() { :; }; savelog() { :; }; tourres() { :; }; p2res() { :; }; walkres() { :; }
  keepshots() { echo "    \$ keep testdata/shots/PT_* -> testdata/shots/$1"; }
  putdll() { echo "    \$ install $1 -> plugins/HoF2VR/HoF2VR.dll"; }
  echo "CAND11.1 --dry: WANT ${WANT:-(not given: the DLL sha1 is argument 1 or CAND11_1_WANT)}; main $(git rev-parse --short HEAD 2>/dev/null)"
  for f in $HUDONLY; do [ -e "$f" ] || echo "  (not on this tree yet - comes with a playtest-4 branch: $f)"; done
  newonly || echo "  (the 11.1 branches' cover / PlayerSize are not on this tree yet)"
  echo "logs $LOG, $O/; pictures $PICS (HUD $HUD_PICS); reservation $([ "$NORESERVE" = 1 ] && echo 'none (NORESERVE=1)' || echo 'scripts/reserve.sh start <chain pid>, HOF2VR_RESERVE_MAX_H 8')"
  echo "before: build/bin = WANT, the user not in VR, the three branches' P4 scripts present; savecheck; the player's cfg, Unity"
  echo "  prefs and DLC record hashed"
  echo "every block: the user's VR (the watcher), build/bin and the installed DLL = WANT (dllcheck), the kernel log for GPU faults"
  tot=0
  for b in $BLOCKS; do
    echo; echo "## $b (est ~${EST[$b]:-?} min)"
    COMBO=board_shell
    $b
    tot=$((tot + ${EST[$b]:-0}))
  done
  echo
  echo "## end: the game and Monado stopped; savecheck after; the installed DLL = WANT; exceptions in the saved game logs;"
  echo "   the cfg / Unity prefs / DLC record hashes as at the start"
  echo "estimated total: ~$tot min (~$((tot / 60)) h $((tot % 60)) min) without interruptions"
  exit 0
fi

# ---- the chain ----
[ -n "$WANT" ] || { echo "usage: chain_cand11_1.sh [--dry] <the candidate DLL's sha1> (or CAND11_1_WANT)"; exit 2; }
mkdir -p $T "$PICS" "$HUD_PICS" "$O"
: > "$LOG"
rm -f $T/cand11_1_gpu_fault
if userin; then say "the user is in VR - not starting"; exit 9; fi
[ "$(sha build/bin/HoF2VR.dll)" = "$WANT" ] || { say "build/bin is not the candidate - not starting"; exit 8; }
for f in $HUDONLY; do [ -e "$f" ] || { say "$f is missing: a playtest-4 branch is not merged in this tree - not starting"; exit 7; }; done
newonly || { say "the 11.1 branches (p3combat's cover, hud's PlayerSize) are not merged in this tree - not starting"; exit 7; }
[ "$NORESERVE" = 1 ] || scripts/reserve.sh start $$ >> "$LOG" 2>&1
watcher & WPID=$!
trap 'kill $WPID 2>/dev/null; [ "$(sha "$P")" = "$WANT" ] || { putdll build/bin/HoF2VR.dll; say "plugins put back: $(sha "$P" | cut -c1-8)"; }; [ "$NORESERVE" = 1 ] || scripts/reserve.sh stop >> "$LOG" 2>&1; rm -f $BPID $MARK' EXIT
trap 'exit 1' INT TERM
U0=$(untouched)
say "CAND11.1 start main $(git rev-parse --short HEAD) DLL ${WANT:0:8}; $U0; sched $(ps -o cls= -p $$)"
for b in $BLOCKS; do
  dllcheck || break
  run_block $b $b
  rc=$?
  say "$b done rc $rc"
  [ -f $T/cand11_1_gpu_fault ] && { say "the chain ends: GPU fault"; break; }
done
savelog
python3 scripts/session.py stop > /dev/null 2>&1
sleep 3
say "game stopped: $(pgrep -x 'Hand of Fate 2.' | tr '\n' ' ')(none = stopped)"
scripts/monado.sh stop >> "$LOG" 2>&1
t "savecheck after" $O/savecheck2.out bash scripts/savecheck.sh
[ "$(sha "$P")" = "$WANT" ] || { putdll build/bin/HoF2VR.dll; say "plugins put back: $(sha "$P" | cut -c1-8)"; }
say "exceptions in the saved game logs: $(cat $O/LogOutput-*.log 2>/dev/null | grep -c Exception) (files $(ls $O/LogOutput-*.log 2>/dev/null | wc -l))"
U1=$(untouched)
[ "$U1" = "$U0" ] || say "CHANGED: $U0 -> $U1"
say "CAND11.1 end; $U1 (must be $U0); plugins $(sha "$P" | cut -c1-8); build/bin $(sha build/bin/HoF2VR.dll | cut -c1-8)"
