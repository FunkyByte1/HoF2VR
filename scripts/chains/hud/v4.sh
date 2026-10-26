#!/usr/bin/env bash
# env 3 (2026-10-19, the HUD audit fixes and the shield's frame ring): pictures -> captures/hud-built-2026-10-18/v4/ (the shell
# recovering, a kill with Shell and with Rings during the death fade, On you with a combo goal; the shield held in its frame
# ring with BasicShield, the Buckler and left-handed -> v4/p3/), hud_test (with `dead`), p3 M and S, the perf (the big
# fixture) on this build and on 89ee74e3 (logs/HoF2VR-89ee74e3.dll), the labelled sheet.
# Pick: PICS=0/1 SHIELD="basic buckler lefthanded" TESTS=0/1 PERF=0/1.
# Run unsandboxed: NAME=hudv4 nohup setsid bash scripts/chains/hud/v4.sh > /dev/null 2>&1 &
set -u
NAME=${NAME:-hudv4}
LOG=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME.log
O=/home/bara/Projects/Flat2VR-env3/HandOfFate2/logs/$NAME
. /home/bara/Projects/Flat2VR-env3/HandOfFate2/scripts/chains/hud/lib.sh
export HUD_PICS=${HUD_PICS:-/home/bara/Projects/flat2vr/HandOfFate2/captures/hud-built-2026-10-18/v4}
mkdir -p "$HUD_PICS/p3"
begin
if [ "${PICS:-1}" = 1 ]; then
  ensure_table; t "pics v4: recover, kill (shell, rings), bodycombo (snow, soldiers + thugs)" "$O/pics.out" python3 scripts/hud_pics.py v4 --place "snow arena, soldiers" --jump EL_Ambush_Thral --with "EmpireSoldierREF*2,ThievesThugREF*2" --parts recover,kill,bodycombo
  savelog
fi
for v in ${SHIELD-basic buckler lefthanded}; do
  ensure_table
  case $v in buckler) SH=Buckler ;; *) SH=BasicShield ;; esac
  [ "$v" = lefthanded ] && python3 scripts/cmd.py "cfg LeftHanded true" >> "$O/shield_$v.out" 2>&1
  python3 scripts/combat_fixture.py --jump EL_Ambush_Empire --with "EmpireSoldierREF*1" --god --physical --equip "BasicSword_noToken,$SH" >> "$O/shield_$v.out" 2>&1
  t "p3 pics shieldon ($v: $SH)" "$O/shield_$v.out2" env LINES_OUT=$HUD_PICS/p3 SHOT_TAG=_$v python3 scripts/p3_pics.py shieldon
  [ "$v" = lefthanded ] && python3 scripts/cmd.py "cfg LeftHanded false" >> "$O/shield_$v.out" 2>&1
  savelog
done
# the shield pictures' labels for the sheet (lines_pics writes none)
python3 - >> "$O/labels.out" 2>&1 <<'PY'
import os, sys
sys.path.insert(0, "scripts")
sys.argv = ["hud_pics.py"]
import hud_pics as P
for v, what in (("basic", "Shield, 0.40 m"), ("buckler", "Buckler, 0.32 m"), ("lefthanded", "Shield, left-handed")):
    for n, title, text in (("m3w_ring_waiting", "The ring waits for the shield (%s)", "A slim ring at the blow's spot in the look's colour, sized to frame your shield."),
                           ("m4_shield_on_circle_green_scripted", "Shield in its ring: green (%s)", "Held on the circle, the shield sits inside a green ring drawn over it: it blocks."),
                           ("m4b_shield_on_circle_low_green_scripted", "Shield a little low, still in (%s)", "Still counts as on the circle; the attacker in view above the shield.")):
        p = "p3/%s_%s" % (n, v)
        if os.path.exists(P.OUT + "/" + p + ".jpg"):
            P.label(p, title % what, text)
            print("labelled", p)
PY
if [ "${TESTS:-1}" = 1 ]; then
  ensure_table; t "hud thieves: flat,board,body,shell,rings,switch,dead" "$O/hud.out" python3 scripts/hud_test.py --tag HUD4 --fixture thieves --equip $GEAR --stay --parts flat,board,body,shell,rings,switch,dead
  savelog
  ensure_table; t "p3 M mix: tiers,refill,damage,reverse,lined,glow,stick,shield" "$O/p3M.out" python3 scripts/p3_test.py --tag V4M --fixture mix --equip $GEAR --stay --parts tiers,refill,damage,reverse,lined,glow,stick,shield
  ensure_table; t "p3 S soldiers: mirror,ring" "$O/p3S.out" python3 scripts/p3_test.py --tag V4S --fixture shield --equip $GEAR --stay --parts mirror,ring
  savelog
fi
if [ "${PERF:-1}" = 1 ]; then
  ensure_table; t "perf big: this build (shell, worn, rings)" "$O/perf.out" bash scripts/chains/hud/perf.sh hud
  OLD=$H/logs/HoF2VR-89ee74e3.dll
  if [ -f "$OLD" ]; then
    guard; yield_slot; savelog; scripts/stop.sh > /dev/null 2>&1
    cp "$OLD" "$G3/BepInEx/plugins/HoF2VR/HoF2VR.dll"
    python3 scripts/session.py start --snapshot magician_mid --to table --no-install >> "$O/sessions.out" 2>&1
    SLOT_SINCE=$(date +%s)
    say "89ee74e3 at the table: $(sha1sum < "$G3/BepInEx/plugins/HoF2VR/HoF2VR.dll" | cut -c1-8)"
    t "perf big: 89ee74e3 (shell, worn, rings)" "$O/perf_old.out" bash scripts/chains/hud/perf.sh hud
    savelog; scripts/stop.sh > /dev/null 2>&1
    bash scripts/install.sh >> "$O/install.out" 2>&1   # a test install into env 3's game (never --release)
    say "this build reinstalled: $(sha1sum < "$G3/BepInEx/plugins/HoF2VR/HoF2VR.dll" | cut -c1-8)"
  fi
fi
SHEET_TITLE="Hand of Fate 2 VR - the HUD audit fixes and the shield's frame ring (v4, 2026-10-19), both eyes" python3 scripts/hud_pics.py sheet >> "$O/sheet.out" 2>&1
say "sheet: $(tail -1 "$O/sheet.out")"
finish
