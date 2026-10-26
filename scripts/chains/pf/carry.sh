#!/usr/bin/env bash
# F2's carry-over check (2026-10-17): a COPY of the player's cfg in env 2's game copy, booted with HOF2VR_CFG_SAVE=1 (a
# test run that saves its file; refused in the Steam install), then a second boot with EnemyHealth set back to 1 by hand
# (DefaultsVersion 2: it must stay 1 - "once"). env 2's own cfg is put back at the end. -> logs/pf/carry.out, carry.diff
set -u
H=/home/bara/Projects/Flat2VR-env2/HandOfFate2
cd "$H" || exit 1
. "$H/scripts/chains/r3/env.sh"
CFG="/home/bara/Games/hof2-env2/Hand of Fate 2/BepInEx/config/flat2vr.hof2vr.cfg"
USER_CFG=/home/bara/Projects/flat2vr/HandOfFate2/backups/user-session-20261017-0927/flat2vr.hof2vr.cfg
O=$H/logs/pf
cp -p "$CFG" "$O/env2-cfg.bak"
cp "$USER_CFG" "$O/user-cfg.copy"
cp "$O/user-cfg.copy" "$CFG"
echo "user cfg copy sha1 $(sha1sum < "$USER_CFG" | cut -c1-12)"
HOF2VR_CFG_SAVE=1 python3 scripts/session.py start --snapshot magician_mid --to title
sleep 3
scripts/stop.sh
diff "$O/user-cfg.copy" "$CFG" > "$O/carry.diff"
grep -nE "^(EnemyHealth|DefaultsVersion) =" "$CFG"
# once: the player sets 1.0 again by hand
sed -i 's/^EnemyHealth = .*/EnemyHealth = 1/' "$CFG"
HOF2VR_CFG_SAVE=1 python3 scripts/session.py start --snapshot magician_mid --to title
sleep 3
python3 scripts/cmd.py "sget Settings EnemyHealth" || true
scripts/stop.sh
echo "after the second boot:"; grep -nE "^(EnemyHealth|DefaultsVersion) =" "$CFG"
cp -p "$O/env2-cfg.bak" "$CFG"
echo "env 2 cfg restored: $(sha1sum < "$CFG" | cut -c1-12) (backup $(sha1sum < "$O/env2-cfg.bak" | cut -c1-12))"
echo "user cfg after: $(sha1sum < "$USER_CFG" | cut -c1-12)"
