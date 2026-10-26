#!/usr/bin/env bash
# Candidate 10's chain (env 1, written 2026-10-19): main with branch `hud` merged (hud contains p3combat), on the exact DLL in
# build/bin (WANT = its sha1, the first argument or CAND10_WANT), under one reservation. Tests run Steam-less (test mode:
# SteamGuard, run.sh without SteamAppId); the chain checks that Steam's playtime and its "Game process added" lines for
# 456670 did not change (scripts/steamcheck.py watches every test game), and that the player's cfg, their Unity prefs and
# the DLC record are unchanged; savecheck before and after.
# Blocks (CAND10_BLOCKS picks; default all, in this order):
#   B_scan B_pre                 candidate 9's: the UYF scan, savecheck; N2 edge turn, N3 swarm boss
#   B_hud B_perf B_p3            env 3's hud_test (flat, board, body, shell, rings, switch) and perf.sh (Shell / worn / Rings in
#                                the big fight); env 2's p3_test parts M S R Q V L T (chain_p3c.sh's, + Q V L)
#   B_fights1 B_fights2          chain_pf's fight blocks A B C D F G P N (+ move_test): round 1 Board + Shell (the default),
#                                round 2 Body + Rings (the seams vrhud body / vrstatus rings, re-applied after every session)
#   B_focus B_tour B_tour_seated focus_test; the tours standing and seated
#   B_cb B_mine B_interact       env 1's: the seated call to battle (cb_seated_test seated x10 + the deal, standing x5);
#                                wheel_look, b1 standing / seated, seated_check, switches_test, audit_fixes_test; every
#                                interact group in a fresh session (+ figurine_drop, figurine_drop_beside), fixtests
#   B_side B_n1 B_limbo          the side table's P2 checks (still attract, pin, hubwalk, the opening's figurine_real and
#                                figurine_drop; side.py in the screens' order); N1's voice in the Steam runtime; limbo_test
#   B_walkt B_walkf              the picture walk with the head moving -> captures/cand10/: the title (turned left), the
#                                table's places, the campfire, the map room / side table (walk.py hub adventure); the fights:
#                                candidate 9's (thieves, soldiers, gnomes, bombs, a boss), then cand10/fightpics.sh: the
#                                shield on its circle (Board + Shell and Body + Rings), goblin archers' and lobs' ground
#                                arrows (gold with the shield, red without), a true night arena (hud_pics + both combos)
# The user always wins: a watcher stops the running block, the game and Monado and releases the reservation as soon as
# status.json says "user": true; the chain waits until it is false, reserves again and reruns the block (3 tries).
# A GPU fault (amdgpu reset / page fault / ring timeout in the kernel log) stops everything at once and ends the chain.
#   chain_cand10.sh --dry [WANT]   prints the blocks and their commands, runs nothing
#   Run unsandboxed: nohup setsid bash scripts/chains/cand10/chain_cand10.sh <WANT sha1> > /dev/null 2>&1 &
#   NORESERVE=1: no reservation (fair turns); PERF_OLD=<a DLL file>: B_perf also measures that DLL (perf.sh old) in the
#   same fight, then puts the candidate back. Logs logs/chain_cand10.log, logs/cand10/.
# Known test-side fails before this candidate (NOTES): the tours' title_flicker (the test GPU profile) and the seated tour's
# jumpmenu_chord; F linecut "no clean setup in 5 tries"; p3 M lined over a bright snow sky (green reads cyan); figurine_drop's
# 'beside' in the map its 'on' moved in (hence figurine_drop_beside on a fresh map); b1 seated's one 43-66 ms frame in the
# campfire's first frames; P2-8 pin's first tap ~6 s after arriving; hubwalk's swipe in SelectMode (side.py's order passes).
set -u
DRY=0
[ "${1:-}" = --dry ] && { DRY=1; shift; }
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
T=/tmp/claude-hof2
PICS=${CAND10_PICS:-$H/captures/cand10}
export CAND9_PICS=$PICS HUD_PICS=$PICS/hud   # (the cand9 walk scripts' and hud_pics.py's picture folders)
LOG=${CAND10_LOG:-$H/logs/chain_cand10.log}
O=${CAND10_O:-$H/logs/cand10}
WANT=${1:-${CAND10_WANT:-}}
G="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2"
GAMELOG="$G/BepInEx/LogOutput.log"
CFG="$G/BepInEx/config/flat2vr.hof2vr.cfg"
DLCF="$G/BepInEx/hof2vr_dlc.txt"
PREFS="$HOME/.config/unity3d/Defiant Development/Hand of Fate 2/prefs"
GPLOG="$HOME/.local/share/Steam/logs/gameprocess_log.txt"
ST=/run/user/1000/vr-session/status.json
MARK=$T/cand10_user_in
BPID=$T/cand10_block.pid
GEAR="BasicSword_noToken,BasicShield"
P="$G/BepInEx/plugins/HoF2VR/HoF2VR.dll"
BLOCKS=${CAND10_BLOCKS:-B_scan B_pre B_hud B_perf B_p3 B_fights1 B_fights2 B_focus B_tour B_tour_seated B_cb B_mine B_interact B_side B_n1 B_limbo B_walkt B_walkf}
# estimated minutes per block (candidate 9's reserved run logs/chain_cand9.log; env 1's pt3 runs logs/pt3/chain_reg*.log;
# env 2's p3i.log and env 3's hudr2 / hudv3b logs, less their fair-turn waits)
declare -A EST=([B_scan]=1 [B_pre]=6 [B_hud]=6 [B_perf]=6 [B_p3]=25 [B_fights1]=25 [B_fights2]=25 [B_focus]=5 [B_tour]=13
  [B_tour_seated]=13 [B_cb]=24 [B_mine]=45 [B_interact]=20 [B_side]=12 [B_n1]=1 [B_limbo]=5 [B_walkt]=36 [B_walkf]=50)
# what only branch hud brings (the chain refuses to start without it)
HUDONLY="scripts/hud_test.py scripts/hud_pics.py scripts/p3_test.py scripts/p3_pics.py scripts/lines_pics.py scripts/chains/hud/perf.sh"
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
  touch $T/cand10_gpu_fault
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
# Steam (read only): 456670's Playtime in localconfig.vdf, and Steam's "Game process added" lines for it
playtime() { python3 -c "import sys; sys.path.insert(0, 'scripts'); import steamcheck as S; print(S.playtime())" 2>/dev/null; }
gpadds() { local n; n=$(grep -c 'Game process added.*AppID 456670' "$GPLOG" 2>/dev/null); echo "${n:-0}"; }
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
# env 3's HUD tests (scripts/chains/hud/chain_hud.sh, v3b.sh): hud_test's every part (it switches Board / Body and Shell / Rings
# itself, the settings page's switches live mid-fight), then perf.sh hud (the big fight: Shell, every shell worn to 40 %,
# Rings; 900 frames x2 each)
B_hud() {
  combo board_shell
  ensure_table
  t "hud_test thieves: flat,board,body,shell,rings,switch" $O/hud.out python3 scripts/hud_test.py --tag C10H --fixture thieves --equip $GEAR --stay --parts flat,board,body,shell,rings,switch
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
  ensure_table; t "p3 M mix: tiers,refill,damage,reverse,lined,glow,stick,shield" $O/p3M.out python3 scripts/p3_test.py --tag C10M --fixture mix --equip $GEAR --stay --parts tiers,refill,damage,reverse,lined,glow,stick,shield
  ensure_table; t "p3 S soldiers: mirror,ring" $O/p3S.out python3 scripts/p3_test.py --tag C10S --fixture shield --equip $GEAR --stay --parts mirror,ring
  ensure_table; t "p3 R thieves: ring" $O/p3R.out python3 scripts/p3_test.py --tag C10R --fixture thieves --equip $GEAR --stay --parts ring
  ensure_table; t "p3 Q thieves: postbreak,pause" $O/p3Q.out python3 scripts/p3_test.py --tag C10Q --fixture thieves --equip $GEAR --stay --parts postbreak,pause
  ensure_table; t "p3 V dodge2: leanarrow" $O/p3V.out python3 scripts/p3_test.py --tag C10V --fixture dodge2 --equip $GEAR --stay --parts leanarrow
  ensure_table; t "p3 L lobs with a shield: groundgold" $O/p3L.out python3 scripts/p3_test.py --tag C10L --fixture lobs --equip $GEAR --stay --parts groundgold
  ensure_table; t "p3 T tutorial skeletons + throwers, no shield: pips,ground" $O/p3T.out python3 scripts/p3_test.py --tag C10T --fixture tut --equip HugeHammer --stay --parts pips,ground
  leave; table
  exc "p3"
}
# chain_pf's blocks (as candidate 9 and env 2/3's chains run them), one round in the given HUD combo
B_fights() {
  local i=$1
  combo $2
  ensure_table; t "r$i A shadows leadhold,combogaps" $O/A$i.out python3 scripts/audit_test.py --tag C10${i}A --fixture shadows --equip $GEAR --stay --parts leadhold,combogaps
  ensure_table; t "r$i B thieves waiting,waiting,dash,strafe,health" $O/B$i.out python3 scripts/audit_test.py --tag C10${i}B --fixture thieves --equip $GEAR --stay --parts waiting,waiting,dash,strafe,health
  ensure_table; t "r$i C thieves stage1 block x3" $O/C$i.out python3 scripts/stage1_test.py --tag C10${i}C --fixture thieves --equip $GEAR --stay --parts block,block,block
  if inafight; then t "r$i C move_test (same fight)" $O/M$i.out python3 scripts/move_test.py --tag C10${i}M --no-fixture; else say "r$i move_test: the fight is over"; fi
  ensure_table; t "r$i D combo, interruptcut (combo fixture)" $O/D$i.out python3 scripts/stage2_test.py --tag C10${i}D --fixture combo --equip $GEAR --stay --parts combo,interruptcut
  ensure_table; t "r$i F thieves linecut,crit,wrong,stickuse" $O/F$i.out python3 scripts/stage2_test.py --tag C10${i}F --fixture thieves --equip $GEAR --stay --parts linecut,crit,wrong,stickuse
  ensure_table; t "r$i G dodge fixture redbar,redlean" $O/G$i.out python3 scripts/stage1_test.py --tag C10${i}G --fixture dodge --equip $GEAR --stay --parts redbar,redlean
  ensure_table; t "r$i P lobs knock,drop,dodge,offview" $O/P$i.out python3 scripts/bomb_test.py --tag C10${i}P --stay --parts knock,drop,dodge,offview
  ensure_table; t "r$i N gnomes watch,cut,dash,cling" $O/N$i.out python3 scripts/gnome_test.py --tag C10${i}N --stay --parts watch,cut,dash,cling
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
  x touch $T/cand10_t0
  savelog
  xo $O/tour.out python3 scripts/playtest.py; tourres "tour standing rc $?" $O/tour.out
  say "tour standing PROXIMITY lines: $(grep -c PROXIMITY "$GAMELOG" 2>/dev/null)"
  keepshots cand10_standing $T/cand10_t0
}
B_tour_seated() {
  x touch $T/cand10_t1
  savelog
  xo $O/tour_seated.out python3 scripts/playtest.py --seated; tourres "tour seated rc $?" $O/tour_seated.out
  say "seated reach: $(grep 'seated_touch_within_reach' $O/tour_seated.out 2>/dev/null | tail -1 | cut -c10-160)"
  say "tour seated PROXIMITY lines: $(grep -c PROXIMITY "$GAMELOG" 2>/dev/null)"
  keepshots cand10_seated $T/cand10_t1
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
  xo $O/limbo.out python3 scripts/limbo_test.py --snapshot first_fight --tag L10; rc=$?
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
# ... and the fights: candidate 9's five (Board + Shell), then the new ones (cand10/fightpics.sh), HUD pictures' sheet
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
    t "walk: fight $f" $O/fightpics_$f.out env FIGHTPICS_O=$O timeout 900 bash scripts/chains/cand10/fightpics.sh $f
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
  echo "CAND10 --dry: WANT ${WANT:-(not given: the DLL sha1 is argument 1 or CAND10_WANT)}; main $(git rev-parse --short HEAD 2>/dev/null)"
  for f in $HUDONLY; do [ -e "$f" ] || echo "  (not on this tree yet - comes with branch hud: $f)"; done
  echo "logs $LOG, $O/; pictures $PICS (HUD $HUD_PICS); reservation $([ "$NORESERVE" = 1 ] && echo 'none (NORESERVE=1)' || echo 'scripts/reserve.sh start <chain pid>, HOF2VR_RESERVE_MAX_H 8')"
  echo "before: build/bin = WANT, the user not in VR, branch hud's scripts present; savecheck; the player's cfg, Unity prefs and"
  echo "  DLC record hashed; Steam's 456670 Playtime and 'Game process added' lines counted; steamcheck.py watching every test game"
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
  echo "   the cfg / Unity prefs / DLC record hashes as at the start; Steam's Playtime and 'Game process added' lines as at the"
  echo "   start, steamcheck.py's verdict per test game"
  echo "estimated total: ~$tot min (~$((tot / 60)) h $((tot % 60)) min) without interruptions"
  exit 0
fi

# ---- the chain ----
[ -n "$WANT" ] || { echo "usage: chain_cand10.sh [--dry] <the candidate DLL's sha1> (or CAND10_WANT)"; exit 2; }
mkdir -p $T "$PICS" "$HUD_PICS" "$O"
: > "$LOG"
rm -f $T/cand10_gpu_fault
if userin; then say "the user is in VR - not starting"; exit 9; fi
[ "$(sha build/bin/HoF2VR.dll)" = "$WANT" ] || { say "build/bin is not the candidate - not starting"; exit 8; }
for f in $HUDONLY; do [ -e "$f" ] || { say "$f is missing: branch hud is not merged in this tree - not starting"; exit 7; }; done
[ "$NORESERVE" = 1 ] || scripts/reserve.sh start $$ >> "$LOG" 2>&1
watcher & WPID=$!
python3 scripts/steamcheck.py 86400 > $O/steamcheck.out 2>&1 & SCPID=$!
trap 'kill $WPID $SCPID 2>/dev/null; [ "$(sha "$P")" = "$WANT" ] || { putdll build/bin/HoF2VR.dll; say "plugins put back: $(sha "$P" | cut -c1-8)"; }; [ "$NORESERVE" = 1 ] || scripts/reserve.sh stop >> "$LOG" 2>&1; rm -f $BPID $MARK' EXIT
trap 'exit 1' INT TERM
U0=$(untouched); PT0=$(playtime); GP0=$(gpadds)
say "CAND10 start main $(git rev-parse --short HEAD) DLL ${WANT:0:8}; $U0; Steam: 456670 Playtime $PT0 min, 'Game process added' lines $GP0; sched $(ps -o cls= -p $$)"
for b in $BLOCKS; do
  dllcheck || break
  run_block $b $b
  rc=$?
  say "$b done rc $rc"
  [ -f $T/cand10_gpu_fault ] && { say "the chain ends: GPU fault"; break; }
done
savelog
python3 scripts/session.py stop > /dev/null 2>&1
sleep 3
say "game stopped: $(pgrep -x 'Hand of Fate 2.' | tr '\n' ' ')(none = stopped)"
scripts/monado.sh stop >> "$LOG" 2>&1
t "savecheck after" $O/savecheck2.out bash scripts/savecheck.sh
[ "$(sha "$P")" = "$WANT" ] || { putdll build/bin/HoF2VR.dll; say "plugins put back: $(sha "$P" | cut -c1-8)"; }
say "exceptions in the saved game logs: $(cat $O/LogOutput-*.log 2>/dev/null | grep -c Exception) (files $(ls $O/LogOutput-*.log 2>/dev/null | wc -l))"
sleep 15   # (steamcheck.py reads Steam's files 8 s after a test game ends)
kill $SCPID 2>/dev/null
PT1=$(playtime); GP1=$(gpadds); U1=$(untouched)
say "Steam: Playtime $PT0 -> $PT1 min, 'Game process added' lines for 456670 $GP0 -> $GP1; steamcheck: $(grep -c 'started;' $O/steamcheck.out) test games, $(grep -c -- '-> PASS' $O/steamcheck.out) PASS, $(grep -c -- '-> FAIL' $O/steamcheck.out) FAIL"
pick '-> FAIL' $O/steamcheck.out 260
{ [ "$PT1" = "$PT0" ] && [ "$GP1" = "$GP0" ]; } || say "STEAM CHANGED during the chain (the user's own launches count here too): see $O/steamcheck.out"
[ "$U1" = "$U0" ] || say "CHANGED: $U0 -> $U1"
say "CAND10 end; $U1 (must be $U0); plugins $(sha "$P" | cut -c1-8); build/bin $(sha build/bin/HoF2VR.dll | cut -c1-8)"
