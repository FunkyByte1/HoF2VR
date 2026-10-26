#!/usr/bin/env python3
"""Bring this environment's running test session into a live fight (combat work, docs/combat-progress.md).

  combat_fixture.py [--god] [--timeout S] [--board-shot NAME]
  combat_fixture.py --fixture shield|archer|snare|behind|armour|mixed|thieves|lobs|big|boss_shadow|... [--god] [--boot]
  combat_fixture.py --jump FIGHT [--with SPEC] [--god] [--boot]      (FIGHT = boss:NAME for a boss or special encounter)

From the table (session.py start --snapshot first_fight --to table): autoplay until a fight loads, then the waiting
space's gate (A) and the game's enemy showcase (A) until GameState_Level_Play. Already in a fight: nothing to do.
--god keeps the hero alive (test seam `god`). Prints the final state; exit 1 if no fight was reached.

Fixtures (combat Phase 1B): from a map save whose hero cards are dealt (snapshot magician_mid; --boot restores it and
boots to the map first), `fightwith SPEC` sets the next fight's enemies (monster cards, Diag/CombatSeams.cs) and DevJump
starts FIGHT (`jump fight`); the encounter's card is confirmed with A, then the gate and the showcase as above. SPEC is
"<card or AI name>[*n],...". Named fixtures: see FIXTURES.
"""
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402

import hof2env  # noqa: E402

H = hof2env.H


def send(c, t=8):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def state():
    return send("state?", 6)


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


FIXTURES = {
    # shields: ActionBlock soldiers and a phalanx (ActionPhalanxBlock)
    "shield": ("EL_Ambush_Empire", "EmpireSoldierREF*2,EmpirePhalanxREF*1"),
    # archers: goblin archers (ranged) with a shield warrior in front
    "archer": ("EL_Ambush_Goblins", "GoblinArcherREF*2,GoblinWarriorREF*1"),
    # snares: Norse bolas throwers (their bolas snare the hero: ActionSnared; no monster card in this game has the
    # leaping ActionSnareGoblin - the goblin nabbers steal and flee) with a goblin warrior
    "snare": ("EL_Ambush_Goblins", "NorseBolasREF*2,GoblinWarriorREF*1"),
    # attackers that land behind the player: thieves' assassins (ActionAssassinateAI)
    "behind": ("EL_Ambush_Thral", "ThievesAssassinREF*2"),
    # armour: Norse tanks (Protection: quick hits break it) and a corrupted tank (red slams)
    "armour": ("EL_Ambush_Thral", "NorseTankREF*2,CorruptedTankREF*1"),
    # every kind at once (short smoke)
    "mixed": ("EL_Ambush_Thral", "ThievesAssassinREF*1,GoblinNabberREF*1,GoblinArcherREF*1,EmpirePhalanxREF*1,CorruptedTankREF*1"),
    # plain counterable melee (thieves: counter, riposte, finisher)
    "thieves": ("EL_Ambush_Thral", "ThievesThugREF*3"),
    # multi-blow attackers (combat Phase 2's combo bars): an ogre - the only common enemy with ActionMeleeComboAI (an offline
    # scan of the ai_* bundles, 2026-10-12: also the bosses Ariadne and Estrella) - and a thief (thrusts, knockdowns)
    "combo": ("EL_Ambush_Thral", "OgreGenericREF*1,ThievesThugREF*1"),
    # Phase 2 in one fight: a thrusting thief, an archer, a shield bearer, a berserker (combos), a molotov thrower
    "p2mix": ("EL_Ambush_Thral", "ThievesThugREF*1,GoblinArcherREF*1,EmpireSoldierREF*1,NorseBerserkerREF*1,ThievesMolotovThrowerREF*1"),
    # Phase 2, second fight: an ogre (combos), shield bearers (the flank), a thrusting thief, an archer and a bolas thrower
    "p2b": ("EL_Ambush_Thral", "OgreGenericREF*1,EmpireSoldierREF*1,ThievesThugREF*1,GoblinArcherREF*1,NorseBolasREF*1"),
    # Phase 2, the final check in one fight: a thief (thrusts), an archer and a goblin lobber (shots and lobs), a shield
    # bearer (the flank), an ogre (combos)
    "p2final": ("EL_Ambush_Thral", "ThievesThugREF*1,GoblinArcherREF*1,GoblinLobberREF*1,EmpireSoldierREF*1,OgreGenericREF*1"),
    # the shield against shots: an archer and a goblin lobber (arrows and lobbed bombs), a shield bearer (the flank)
    "shots": ("EL_Ambush_Thral", "GoblinArcherREF*1,GoblinLobberREF*1,EmpireSoldierREF*1"),
    # lobbed bottles (the shield glances them off): molotov throwers (Phase 0: they throw from ~6.6 m; the goblin lobbers
    # rarely throw)
    "lobs": ("EL_Ambush_Thral", "ThievesMolotovThrowerREF*2"),
    # runs of blows in one clip (audit E3): the shadow warriors' three-blow flurry, the elder's two (ActionTemplarMeleeAI)
    "shadows": ("EL_Ambush_Thral", "ShadowWarriorREF*2,ShadowElderREF*1"),
    # boss AIs in an ordinary arena (most boss encounters open with the Dealer's story and choices first): one at a time
    "boss_shadow": ("EL_Ambush_Thral", "ShadowElderREF_boss*1"),
    "boss_knight": ("EL_Ambush_Thral", "SpecialBlackKnight_BossREF*1"),
    "boss_lovers": ("EL_Ambush_Thral", "Wraith_Lovers_bossREF*1"),
    "boss_shaman": ("EL_Ambush_Thral", "NorseShamanBossREF*1"),
    "boss_assassin": ("EL_Ambush_Thral", "ThievesAssassinBossREF*1"),
    "boss_general": ("EL_Ambush_Thral", "EmpireGeneralTowerBossREF*1"),
    # the biggest fight for the performance check: ten enemies of five kinds
    "big": ("EL_Ambush_Thral", "ThievesThugREF*3,EmpireSoldierREF*2,GoblinArcherREF*2,NorseWarriorREF*2,CorruptedMinionREF*1"),
    # thrown items (the shield against shots): a molotov thrower and a bolas thrower
    "throwers": ("EL_Ambush_Thral", "ThievesMolotovThrowerREF*1,NorseBolasREF*1"),
}


def boot():
    r = subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"],
                       capture_output=True, text=True)
    print((r.stdout or "").strip().splitlines()[-1:] or r.stderr[-300:])
    return "GameState_Map" in state()


def jump(fight, spec, timeout=150):
    """From the map: the next fight's enemies, the jump, the encounter card's A; True once the fight loads."""
    if spec:
        print("fightwith:", send("fightwith " + spec))
    t0 = time.time()
    r = ""
    while time.time() - t0 < timeout:
        # a boss or other special encounter (DevJump lists "Combat" fights only): the `jumpboss` seam
        r = send(("jumpboss " + fight[5:]) if fight.startswith("boss:") else ("jump fight " + fight))
        if r.startswith("started"):
            break
        time.sleep(2.0)
    print("jump:", r)
    if not r.startswith("started"):
        return False
    t0 = time.time()
    while time.time() - t0 < timeout:
        s = state()
        if "Level_In" in s or "Level_Play" in s:
            return True
        try:
            menu = cmd.status().get("menu") or ""
        except Exception:
            menu = ""
        if menu in ("Encounter", "Dialogue", "Activation") or menu.startswith("Encounter"):
            send("press A 6")
        time.sleep(2.5)
    return False


def main(a):
    if "--fixture" in a or "--jump" in a:
        if "--fixture" in a:
            fight, spec = FIXTURES[a[a.index("--fixture") + 1]]
        else:
            fight, spec = a[a.index("--jump") + 1], (a[a.index("--with") + 1] if "--with" in a else "")
        if "--boot" in a or "GameState_Map" not in state():
            if not boot():
                print("fight: no map (" + state() + ")")
                return 1
        if "--physical" in a:
            # Physical mode before the waiting space (its controls card shows the Physical rules)
            print("mode:", send("vrcombat physical"))
        if "--equip" in a:
            # cards to equip on the map first (an artefact, a weapon with an ability: combat Phase 2)
            for card in a[a.index("--equip") + 1].split(","):
                print("equip:", send("equip " + card))
                time.sleep(4.0)
        if not jump(fight, spec):
            print("fight: the jump did not start (" + state() + ")")
            return 1
    timeout = float(a[a.index("--timeout") + 1]) if "--timeout" in a else 900
    s = state()
    if "Level_Play" not in s:
        send("gate auto 0")
        auto = None
        if "Level_In" not in s:
            auto = subprocess.Popen(["python3", H + "/scripts/autoplay.py", str(int(timeout / 60) + 1)],
                                    stdout=open(H + "/logs/combat_fixture-autoplay.out", "w"), stderr=subprocess.STDOUT)
        t0 = time.time()
        try:
            while time.time() - t0 < timeout:
                s = state()
                if "Level_In" in s or "Level_Play" in s:
                    break
                time.sleep(1.0)
        finally:
            if auto is not None:
                auto.terminate()
                auto.wait(10)
        sim("head 0 1.75 0 0 0 0; hand r 0.25 1.0 -0.25 0 -60 0; hand l -0.25 1.0 -0.25 0 -60 0")
        t1 = time.time()
        shot_done = []
        while time.time() - t1 < 90 and "Level_Play" not in state():
            g = send("gate", 4)
            try:
                menu = cmd.status().get("menu") or ""
            except Exception:
                menu = ""
            if g.startswith("gate waiting") and "--board-shot" in a and not shot_done:
                time.sleep(1.5)
                send("shot " + a[a.index("--board-shot") + 1], 15)
                time.sleep(2.0)
                shot_done.append(1)
            if g.startswith("gate waiting") or "Showcase" in menu:
                sim("btn r a 150")
                time.sleep(1.5)
            time.sleep(0.5)
    s = state()
    if "--god" in a and "Level_Play" in s:
        send("god")
    print("fight:", s)
    return 0 if "Level_Play" in s else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
