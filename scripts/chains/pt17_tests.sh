#!/usr/bin/env bash
# The playtest fixes' regression run (2026-10-17, env 1), from a running session: interact, boards, a table sweep
# subset, limbo, then N1's proof in the Steam runtime. Output: logs/pt17_tests.out
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
exec >> "$H/logs/pt17_tests.out" 2>&1
export XAUTHORITY=$(ls /run/user/1000/xauth_* | head -1)
echo "== $(date +%T) tests"
python3 scripts/session.py start --snapshot magician_mid --to table 2>&1 | tail -2; sleep 5
timeout 900 python3 scripts/interact_test.py figurine_pinch dice_reroll_vr dice_reroll touch_mark card_flip figurine | grep -E "PASS|FAIL|passed"
timeout 600 python3 scripts/boards_test.py table | grep -E "PASS|FAIL"
timeout 1800 python3 scripts/table_sweep.py --only "Gain a weapon,Choose 1 of 3,Gain gold,Gain life,End coins,Shop,Dialogue,Blessing,Dice" > logs/pt17_sweep.out 2>&1; grep -E "^====|violation" logs/pt17_sweep.out | tail -8
echo "== $(date +%T) limbo"
timeout 1200 python3 scripts/limbo_test.py --snapshot magician --no-fight --noshots 2>&1 | tail -12
python3 scripts/session.py stop > /dev/null 2>&1
echo "== $(date +%T) N1"
bash scripts/chains/pt17_n1.sh > /dev/null 2>&1; tail -25 logs/pt17_n1.out
scripts/savecheck.sh | tail -1
sha1sum "/home/bara/.local/share/Steam/steamapps/common/Hand of Fate 2/BepInEx/config/flat2vr.hof2vr.cfg" | cut -c1-12
echo "== $(date +%T) all done"
