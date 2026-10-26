#!/usr/bin/env bash
# env 2, playtest 4's blocking fight items (P4-12..P4-16, the fight-exit error; NOTES "Playtest 4 blocking fixes (env 2,
# 2026-10-19)"): p4b_test's parts in their fixtures with pictures (S thieves: shield, circle, edge, threat; B two assassins:
# assassin, green; H thieves without a shield: bladedmg; E thieves: exit; the 55 % rule of 2026-10-20: K BasicShield cover, edge,
# U Buckler cover, W BroadShield cover), then candidate 10's fight blocks as env 2 runs them (B_p3's
# p3_test parts M S R Q V L T, B_fights1 = chain_pf's A B C D F G P N in the default HUD, B_fights2 the same with the HUD on the
# body and the enemies' rings) on one DLL (build/bin), under env 2's reservation (lib.sh; NORESERVE=1: fair turns).
#   X=<blocks of p4b> P3=<p3 parts> BLOCKS=<fight blocks> ROUNDS=<1|2> NAME=<log name>
# The slot's turn rule (coordinator 2026-10-20 00:04: at most 25 minutes, then the others): run a few blocks per turn
# (e.g. X="S B" P3= ROUNDS=0, then X= P3="M S R" ROUNDS=0, ...); env 2's own runs of 2026-10-20 went through a step queue.
# Run unsandboxed: nohup setsid bash scripts/chains/p4/chain_p4b.sh > /dev/null 2>&1 &
set -u
NAME=${NAME:-p4b}
LOG=/home/bara/Projects/Flat2VR-env2/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env2/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env2/HandOfFate2/scripts/chains/r3/lib.sh
X=${X:-"S B H E"}
P3=${P3:-"M S R Q V L T"}
BLOCKS=${BLOCKS:-"A B C D F G P N"}
ROUNDS=${ROUNDS:-2}
COMBO=board_shell
CPICS=${CPICS:-/home/bara/Projects/flat2vr/HandOfFate2/captures/cover-2026-10-20}
combo() { if [ "$COMBO" = body_rings ]; then python3 scripts/cmd.py 'vrhud body' 5 > /dev/null 2>&1; python3 scripts/cmd.py 'vrstatus rings' 5 > /dev/null 2>&1; fi; }
et() { ensure_table; combo; }
begin
for b in $X; do
  case $b in
    S) et; t "p4b S thieves: shield,circle,edge,threat (+pics)" "$O/p4bS.out" python3 scripts/p4b_test.py --tag P4S --fixture thieves --stay --pics --parts shield,circle,edge,threat ;;
    B) et; t "p4b B assassins: assassin,green (+pics)" "$O/p4bB.out" python3 scripts/p4b_test.py --tag P4B --fixture behind --stay --pics --parts assassin,green ;;
    H) et; t "p4b H thieves, no shield: bladedmg" "$O/p4bH.out" python3 scripts/p4b_test.py --tag P4H --fixture thieves --equip HugeHammer --stay --parts bladedmg
       leave; savelog; guard; yield_slot; table ;;
    E) et; t "p4b E thieves: exit (the game's sign-out under the held exit)" "$O/p4bE.out" python3 scripts/p4b_test.py --tag P4E --fixture thieves --stay --parts exit
       savelog; guard; yield_slot; table ;;   # a fresh session after the reset
    # the 55 % rule (2026-10-20): the coverage table, edge-on, live blows - the basic shield, a buckler, a large shield
    K) et; t "p4b K thieves, BasicShield: cover,edge (+pics)" "$O/p4bK.out" env P4B_PICS=$CPICS python3 scripts/p4b_test.py --tag P4K --fixture thieves --stay --pics --parts cover,edge ;;
    U) et; t "p4b U thieves, Buckler: cover (+pics)" "$O/p4bU.out" env P4B_PICS=$CPICS python3 scripts/p4b_test.py --tag P4U --fixture thieves --equip BasicSword_noToken,Buckler --stay --pics --parts cover ;;
    W) et; t "p4b W thieves, BroadShield: cover (+pics)" "$O/p4bW.out" env P4B_PICS=$CPICS python3 scripts/p4b_test.py --tag P4W --fixture thieves --equip BasicSword_noToken,BroadShield --stay --pics --parts cover ;;
    # (the edge part alone: a rerun after its placement was held, 2026-10-20 16:20)
    Y) et; t "p4b Y thieves, BasicShield: edge" "$O/p4bY.out" python3 scripts/p4b_test.py --tag P4Y --fixture thieves --stay --parts edge ;;
  esac
done
savelog
for b in $P3; do
  case $b in
    M) et; t "p3 M mix: tiers,refill,damage,reverse,lined,glow,stick,shield" "$O/p3M.out" python3 scripts/p3_test.py --tag P4M --fixture mix --equip $GEAR --stay --parts tiers,refill,damage,reverse,lined,glow,stick,shield ;;
    S) et; t "p3 S soldiers: mirror,ring" "$O/p3S.out" python3 scripts/p3_test.py --tag P4S3 --fixture shield --equip $GEAR --stay --parts mirror,ring ;;
    R) et; t "p3 R thieves: ring" "$O/p3R.out" python3 scripts/p3_test.py --tag P4R --fixture thieves --equip $GEAR --stay --parts ring ;;
    Q) et; t "p3 Q thieves: postbreak,pause" "$O/p3Q.out" python3 scripts/p3_test.py --tag P4Q --fixture thieves --equip $GEAR --stay --parts postbreak,pause ;;
    V) et; t "p3 V dodge2: leanarrow" "$O/p3V.out" python3 scripts/p3_test.py --tag P4V --fixture dodge2 --equip $GEAR --stay --parts leanarrow ;;
    L) et; t "p3 L lobs with a shield: groundgold" "$O/p3L.out" python3 scripts/p3_test.py --tag P4L --fixture lobs --equip $GEAR --stay --parts groundgold ;;
    T) et; t "p3 T tutorial skeletons + throwers, no shield: pips,ground" "$O/p3T.out" python3 scripts/p3_test.py --tag P4T --fixture tut --equip HugeHammer --stay --parts pips,ground
       leave; savelog; guard; yield_slot; table ;;
  esac
done
savelog
for i in $(seq 1 "$ROUNDS"); do
  [ "$i" = 2 ] && COMBO=body_rings || COMBO=board_shell
  say "round $i: HUD combo $COMBO"
  for b in $BLOCKS; do
    case $b in
      A) et; t "r$i A shadows leadhold,combogaps" "$O/A$i.out" python3 scripts/audit_test.py --tag P4${i}A --fixture shadows --equip $GEAR --stay --parts leadhold,combogaps ;;
      B) et; t "r$i B thieves waiting,waiting,dash,strafe,health" "$O/B$i.out" python3 scripts/audit_test.py --tag P4${i}B --fixture thieves --equip $GEAR --stay --parts waiting,waiting,dash,strafe,health ;;
      C) et; t "r$i C thieves stage1 block x3" "$O/C$i.out" python3 scripts/stage1_test.py --tag P4${i}C --fixture thieves --equip $GEAR --stay --parts block,block,block
         if state | grep -q Level_Play; then t "r$i C move_test (same fight)" "$O/M$i.out" python3 scripts/move_test.py --tag P4${i}M --no-fixture; else say "r$i move_test: the fight is over"; fi ;;
      D) et; t "r$i D combo, interruptcut (combo fixture)" "$O/D$i.out" python3 scripts/stage2_test.py --tag P4${i}D --fixture combo --equip $GEAR --stay --parts combo,interruptcut ;;
      F) et; t "r$i F thieves linecut,crit,wrong,stickuse" "$O/F$i.out" python3 scripts/stage2_test.py --tag P4${i}F --fixture thieves --equip $GEAR --stay --parts linecut,crit,wrong,stickuse ;;
      G) et; t "r$i G dodge fixture redbar,redlean" "$O/G$i.out" python3 scripts/stage1_test.py --tag P4${i}G --fixture dodge --equip $GEAR --stay --parts redbar,redlean ;;
      P) et; t "r$i P lobs knock,drop,dodge,offview" "$O/P$i.out" python3 scripts/bomb_test.py --tag P4${i}P --stay --parts knock,drop,dodge,offview ;;
      N) et; t "r$i N gnomes watch,cut,dash,cling" "$O/N$i.out" python3 scripts/gnome_test.py --tag P4${i}N --stay --parts watch,cut,dash,cling ;;
    esac
  done
  savelog
done
finish
