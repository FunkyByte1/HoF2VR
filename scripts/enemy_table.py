#!/usr/bin/env python3
"""Build the enemy table (src/HoF2VR/Game/EnemyTableData.cs) from data/enemy_uyf.tsv (combat Stage 2 item 9).

  enemy_table.py [--check]

Each row maps an enemy model-name prefix to the Until You Fall enemy it fights like; the archetypes' numbers are UYF's
(work/uyf/reports/enemy_stats.tsv, Normal difficulty), written once below. The mod looks the table up per enemy (Poise:
guard, refill, multipliers, super armor, crit timings; Threats/Guard: the lead from the telegraph to the blow). --check
only prints the table. Decisions.md's pre-compute rule: worked out once, shipped as data.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hof2env  # noqa: E402

H = hof2env.H
SRC = H + "/data/enemy_uyf.tsv"
OUT = H + "/src/HoF2VR/Game/EnemyTableData.cs"

# UYF's archetypes (enemy_stats.tsv columns: Husk, Husk_Lesser, Knight, Knight_Elite, Spellsword, Spellsword_Elite,
# Captain, Captain_Elite, AetherHorror, Captain_Boss). Fields: guard, regen (/s), refill delay (s), hit multiplier, blocked
# multiplier, super armor, first crit (s), combo crit (s), min combo crit (s), recovery after a full combo (s), interrupt
# crit (s), premonition lead (s), attack speed.
ARCH = {
    "husk":            (50, 5, 5, 1.0, 1.5, 35, 7, 3, 1.5, 3.0, 1.25, 1.0, 1.0),
    "lesserhusk":      (5, 5, 5, 1.0, 1.5, 50, 7, 3, 1.5, 3.0, 1.25, 1.1, 0.75),
    "knight":          (100, 20, 5, 0.75, 1.0, 60, 7, 2, 0.5, 2.5, 1.25, 0.9, 0.95),
    "eliteknight":     (150, 35, 5, 1.0, 0.75, 100, 7, 2, 0.5, 2.0, 1.25, 0.75, 1.05),
    "spellsword":      (40, 10, 5, 1.0, 1.5, 30, 3, 2, 0.5, 2.5, 1.25, 0.9, 1.0),
    "elitespellsword": (65, 10, 5, 1.0, 1.15, 50, 3, 2, 0.5, 2.5, 1.25, 0.75, 1.1),
    "captain":         (125, 20, 5, 0.9, 1.3, 200, 7, 2, 0.5, 2.2, 0.9, 0.9, 0.9),
    "elitecaptain":    (175, 30, 5, 0.9, 1.3, 150, 7, 2, 0.5, 2.0, 0.5, 0.75, 1.05),   # super armor: its 200 clamped by Max 150
    "horror":          (150, 25, 10, 1.0, 1.0, 100, 7, 2.5, 1.25, 1.8, 1.25, 1.0, 0.75),
    "captainboss":     (150, 30, 5, 1.0, 1.3, 150, 7, 2, 0.5, 2.0, 1.0, 0.8, 0.9),
}


# UYF's movement collider radius per archetype, standing and seated (m): EnemyMover's CollisionRadius Standing / Seated
# (work/uyf/reports/instancers.txt; EnemyManager.SetPlayerSeated sets the enemy's movement capsule to one of them when the
# player's posture changes). Seated, only the captains (1.1 -> 1.0) and the Aether Horror (1.6 -> 1.45) shrink: they come a
# little closer to a seated player. Our mapping (SeatedRules): the shrink, not UYF's standing sizes.
RADIUS = {
    "husk":            (1.0, 1.0),
    "lesserhusk":      (1.0, 1.0),
    "knight":          (1.0, 1.0),
    "eliteknight":     (1.1, 1.1),
    "spellsword":      (1.0, 1.0),
    "elitespellsword": (1.0, 1.0),
    "captain":         (1.1, 1.0),
    "elitecaptain":    (1.1, 1.0),
    "horror":          (1.6, 1.45),
    "captainboss":     (1.1, 1.0),
}


# UYF's blocking per archetype (instancers.txt, variables.tsv): Block_AggressionThreshold - it blocks once more hits than
# this landed on it, blocked or not, in the last 3 s (_Out_HitsTakenHistory's default period); whether it raises its block
# 0.5 s after its own attack (Block_PostAttackBlockTimer is instanced for the captains only); Block_LoseTicketTimer - a
# block that lasts this long (s) costs it the turn. The block's super armor (a pool) is the Arch's SuperArmor.
BLOCK = {
    "husk":            (4, 0, 1.0),
    "lesserhusk":      (99, 0, 1.5),
    "knight":          (2.25, 0, 1.0),
    "eliteknight":     (2, 0, 1.0),
    "spellsword":      (2.7, 0, 0.75),
    "elitespellsword": (2.5, 0, 0.75),
    "captain":         (2, 1, 2.0),
    "elitecaptain":    (3, 1, 1.0),
    "horror":          (2, 0, 1.0),
    "captainboss":     (4.5, 1, 0.5),
}


# UYF's tickets per archetype (variables.tsv, the instanced ~TicketPosessionTimer and EngagementFitness values): how long it
# may hold the melee ticket (s), its fitness when in position (High) and when only capable (Moderate), and its bonus when its
# attack is off cooldown (AttackReadied). Archetypes without their own values take the base (100, 50, 10).
# UYF's waiting enemies per archetype (instancers.txt: OnDeckRange -> ~GuardedRange, InWaitingRange; variables.tsv: the
# archetype's own DistanceThresholdVariables and timers; item 8, audit E13 / E12): the on-deck enemy's distance (m), its band's
# edge and bullseye (m: it moves once past the edge, stops inside the bullseye); the others' (standby) distance, edge,
# bullseye; MeleeApproachTimeOut (s: the time to reach melee range before the Attack Ticket goes to another);
# TicketSwapDemotionTimer (s: how long an enemy ranks lower after losing its ticket).
STANDBY = {
    "husk":            (4, 0.3, 0.15, 6, 0.5, 0.3, 1.0, 2.0),
    "lesserhusk":      (4, 0.3, 0.15, 7, 0.3, 0.15, 1.0, 2.0),
    "knight":          (4, 0.3, 0.15, 6, 0.5, 0.3, 0.5, 1.0),
    "eliteknight":     (4, 0.3, 0.15, 6, 0.5, 0.3, 0.5, 1.0),
    "spellsword":      (8, 0.3, 0.15, 10, 0.5, 0.3, 0.25, 1.0),
    "elitespellsword": (8, 0.3, 0.15, 11, 0.5, 0.3, 0.25, 1.1),
    "captain":         (4, 0.3, 0.15, 6, 0.5, 0.3, 0.5, 2.0),
    "elitecaptain":    (4, 0.3, 0.15, 6, 0.5, 0.3, 0.5, 2.0),
    "horror":          (10, 0.3, 0.15, 15, 0.3, 0.15, 0.25, 1.0),
    "captainboss":     (7, 0.3, 0.15, 7, 0.3, 0.15, 3.0, 2.0),
}

TICKET = {
    "husk":            (7, 100, 50, 10),
    "lesserhusk":      (5, 100, 50, 10),
    "knight":          (8, 100, 60, 15),
    "eliteknight":     (4, 100, 50, 10),
    "spellsword":      (4, 110, 10, 10),
    "elitespellsword": (4, 100, 50, 10),
    "captain":         (6, 100, 50, 10),
    "elitecaptain":    (6, 100, 50, 10),
    "horror":          (4, 100, 60, 10),
    "captainboss":     (8, 100, 50, 10),
}


# UYF's difficulty (the coordinator's A, 2026-10-13: `[Combat] Difficulty` = Easy, Normal, Hard, Nightmare 1-3 with UYF's
# numbers per step). Normal = the archetype's own stats (enemy_stats.tsv); the other steps = UYF's
# <Archetype>_DifficultyOverride_<step>_Stats (work/uyf/reports/instancers.txt), the Nightmares cumulative (UYF's
# condition for them is "difficulty >= Nightmare N": Nightmare 2 = 1's overrides + 2's). The lesser husk takes the
# husk's. Fields per step: lead (PremonitionDuration, s), attack speed, attack cooldown short / medium / long (s), the
# wait after gaining the ticket (s), the ticket swap grace with 2 / 3 / 4 / 5+ enemies (s), guard, guard refill (/s),
# the off-screen premonition bonus (s).
FIELDS = ["lead", "speed", "cdS", "cdM", "cdL", "ticket", "g2", "g3", "g4", "g5", "guard", "regen", "off"]
STEPS = ["Easy", "Normal", "Hard", "Nightmare1", "Nightmare2", "Nightmare3"]
GRACE_KNIGHT, GRACE = (0.35, 0.3, 0.25, 0.2), (0.5, 0.4, 0.3, 0.25)
BASE = {   # lead, speed, cdS, cdM, cdL, ticket, guard, regen, off (enemy_stats.tsv); grace by archetype (uyf-reference §3.11)
    "husk":            (1.0, 1.0, 2, 2, 2, 0.5, 50, 5, 0.5),
    "lesserhusk":      (1.1, 0.75, 2, 2.5, 3, 0.5, 5, 5, 0.5),
    "knight":          (0.9, 0.95, 0.5, 0.75, 1, 0.25, 100, 20, 0.5),
    "eliteknight":     (0.75, 1.05, 0.25, 0.5, 1, 0.25, 150, 35, 0.25),
    "spellsword":      (0.9, 1.0, 2, 4, 5, 0.25, 40, 10, 0.25),
    "elitespellsword": (0.75, 1.1, 1, 2, 3, 0.25, 65, 10, 0.25),
    "captain":         (0.9, 0.9, 1.5, 2.5, 3.5, 0.25, 125, 20, 0.5),
    "elitecaptain":    (0.75, 1.05, 0.5, 1, 1.5, 0.25, 175, 30, 0.5),
    "horror":          (1.0, 0.75, 0.25, 0.25, 0.25, 0.25, 150, 25, 0.0),
    "captainboss":     (0.8, 0.9, 0.5, 1, 1.5, 0.0, 150, 30, 0.5),
}
OVR = {   # UYF's overrides per step (instancers.txt), by our archetype
    "husk": {
        "Easy": dict(lead=1.5, speed=0.9, g2=0.6, g3=0.55, g4=0.5, g5=0.45, regen=2.5, off=1.2),
        "Hard": dict(lead=0.8, speed=1.0, cdS=1, cdM=1, cdL=1, ticket=0.25, off=0.25),
        "Nightmare1": dict(lead=0.7, speed=1.1, cdS=0.5, cdM=0.75, cdL=1, ticket=0.25, guard=70, off=0.25),
        "Nightmare2": dict(lead=0.65, speed=1.125, off=0.25),
        "Nightmare3": dict(lead=0.6, speed=1.2, off=0.25)},
    "knight": {
        "Easy": dict(lead=1.2, speed=0.85, cdS=0.75, cdM=1, cdL=1.25, g2=0.6, g3=0.55, g4=0.5, g5=0.45, regen=7.5, off=1.2),
        "Hard": dict(lead=0.8, speed=1.0, g2=0.3, g3=0.25, g4=0.2, g5=0.15, off=0.25),
        "Nightmare1": dict(lead=0.7, speed=1.05, cdS=0.3, cdM=0.5, cdL=0.8, ticket=0.2, g2=0.25, g3=0.2, g4=0.15, g5=0.1, guard=125, off=0.25),
        "Nightmare2": dict(lead=0.65, speed=1.02, g2=0.3, g3=0.25, g4=0.2, g5=0.15, guard=125, off=0.25),
        "Nightmare3": dict(lead=0.6, speed=1.05, g2=0.25, g3=0.2, g4=0.15, g5=0.1, guard=150, off=0.25)},
    "eliteknight": {
        "Easy": dict(lead=0.9, speed=1.0, g2=0.55, g3=0.5, g4=0.45, g5=0.4, regen=15, off=0.5),
        "Hard": dict(lead=0.75, speed=1.15, g2=0.35, g3=0.3, g4=0.25, g5=0.2, off=0.25),
        "Nightmare1": dict(lead=0.75, speed=1.2, g2=0.3, g3=0.25, g4=0.2, g5=0.15, guard=200, off=0.25),
        "Nightmare2": dict(lead=0.65, speed=1.15, g2=0.3, g3=0.25, g4=0.2, g5=0.15, off=0.25),
        "Nightmare3": dict(lead=0.65, speed=1.15, g2=0.25, g3=0.2, g4=0.15, g5=0.1, guard=225, off=0.25)},
    "spellsword": {
        "Easy": dict(lead=1.2, speed=0.9, g2=0.55, g3=0.5, g4=0.45, g5=0.4, regen=5, off=0.7),
        "Hard": dict(lead=0.7, speed=1.05, off=0.25),
        "Nightmare1": dict(lead=0.7, speed=1.05, guard=65, off=0.25),
        "Nightmare2": dict(lead=0.75, speed=1.1, off=0.25),
        "Nightmare3": dict(lead=0.65, speed=1.1, guard=75, off=0.25)},
    "elitespellsword": {
        "Easy": dict(lead=0.85, speed=1.0, g2=0.6, g3=0.55, g4=0.5, g5=0.45, regen=5, off=0.7),
        "Hard": dict(g2=0.35, g3=0.3, g4=0.25, g5=0.2, off=0.25),
        "Nightmare1": dict(g2=0.3, g3=0.25, g4=0.15, g5=0.1, guard=80, off=0.25),
        "Nightmare2": dict(off=0.25),
        "Nightmare3": dict(off=0.25)},
    "captain": {
        "Easy": dict(lead=1.0, speed=0.85, g2=0.45, g3=0.5, g4=0.55, g5=0.6, guard=110, regen=7.5, off=1.2),
        "Hard": dict(lead=0.8, speed=1.0, g2=0.25, g3=0.2, g4=0.15, g5=0.1, off=0.25),
        "Nightmare1": dict(lead=0.8, speed=1.0, g2=0.25, g3=0.2, g4=0.15, g5=0.1, guard=150, off=0.25),
        "Nightmare2": dict(lead=0.68, speed=1.02, guard=175, off=0.25),
        "Nightmare3": dict(lead=0.65, speed=1.05, guard=200, off=0.25)},
    "elitecaptain": {
        "Easy": dict(lead=0.85, speed=0.9, regen=15, off=1.2),
        "Hard": dict(lead=0.75, speed=1.05, off=0.25),
        "Nightmare1": dict(lead=0.75, speed=1.05, guard=200, off=0.25),
        "Nightmare2": dict(lead=0.65, speed=1.07, guard=225, off=0.25),
        "Nightmare3": dict(lead=0.65, speed=1.1, guard=250, off=0.25)},
    "captainboss": {
        "Easy": dict(lead=0.9, speed=0.8, g2=0.4, g3=0.4, g4=0.4, g5=0.4, regen=15, off=1.2),
        "Hard": dict(lead=0.8, speed=1.0, off=0.25),
        "Nightmare1": dict(lead=0.8, speed=1.0, guard=175, off=0.25),
        "Nightmare2": dict(lead=0.7, speed=1.05, guard=200, off=0.25),
        "Nightmare3": dict(lead=0.7, speed=1.1, guard=215, off=0.25)},
    "horror": {
        "Easy": dict(lead=1.1, speed=0.72, regen=15, off=0.5),
        "Hard": dict(lead=0.85, speed=0.9, off=0.25),
        "Nightmare1": dict(lead=0.85, speed=0.9, guard=225, off=0.25),
        "Nightmare2": dict(off=0.25),
        "Nightmare3": dict(off=0.25)},
}
# the lesser husk: the husk's step overrides, its own guard (5) kept
OVR["lesserhusk"] = {k: {f: x for f, x in v.items() if f != "guard"} for k, v in OVR["husk"].items()}


def steps(arch):
    """The 6 steps' fields for one archetype."""
    b = BASE[arch]
    g = GRACE_KNIGHT if arch == "knight" else GRACE
    normal = dict(lead=b[0], speed=b[1], cdS=b[2], cdM=b[3], cdL=b[4], ticket=b[5], g2=g[0], g3=g[1], g4=g[2], g5=g[3], guard=b[6], regen=b[7], off=b[8])
    out = {}
    for st in STEPS:
        v = dict(normal)
        if st == "Normal":
            pass
        elif st.startswith("Nightmare"):
            for k in range(1, int(st[-1]) + 1):
                v.update(OVR[arch].get("Nightmare%d" % k, {}))
        else:
            v.update(OVR[arch].get(st, {}))
        out[st] = [v[f] for f in FIELDS]
    return out


def rows():
    out = []
    for line in open(SRC):
        line = line.rstrip("\n")
        if not line.strip() or line.startswith("#"):
            continue
        parts = line.split("\t")
        if len(parts) < 2 or parts[1] not in ARCH:
            sys.exit("bad row: %r" % line)
        out.append((parts[0], parts[1], parts[2] if len(parts) > 2 else ""))
    # the longest prefix first: SkeletonKnight before Skeleton
    out.sort(key=lambda r: -len(r[0]))
    return out


def f(x):
    s = ("%.3f" % x).rstrip("0").rstrip(".")
    return s + "f"


def main():
    rs = rows()
    if "--check" in sys.argv:
        for p, a, n in rs:
            print("%-22s %-16s %s" % (p, a, n))
        print("%-16s %-11s %s" % ("archetype", "step", " ".join("%6s" % x for x in FIELDS)))
        for name in ARCH:
            for k, v in steps(name).items():
                print("%-16s %-11s %s" % (name, k, " ".join("%6.2f" % x for x in v)))
        return 0
    lines = ["// Generated by scripts/enemy_table.py from data/enemy_uyf.tsv - do not edit by hand.",
             "namespace HoF2VR",
             "{",
             "    internal static class EnemyTableData",
             "    {",
             "        /// UYF's archetypes: guard, regen, delay, hit mult, blocked mult, super armor, first crit, combo crit, min combo",
             "        /// crit, recovery after a full combo, interrupt crit, lead, attack speed (enemy_stats.tsv, Normal).",
             "        public static readonly EnemyTable.Arch[] Archetypes =",
             "        {"]
    for name, v in ARCH.items():
        lines.append('            new EnemyTable.Arch("%s", %s),' % (name, ", ".join(f(x) for x in v)))
    lines += ["        };", "",
              "        /// Model-name prefixes (longest first) -> archetype.",
              "        public static readonly string[][] Families =",
              "        {"]
    for p, a, n in rs:
        lines.append('            new[] { "%s", "%s" },   // %s' % (p, a, n.replace("*/", "")))
    lines += ["        };", "",
              "        /// UYF's difficulty per archetype (the order of Archetypes), per step (Easy, Normal, Hard, Nightmare 1-3): lead,",
              "        /// attack speed, cooldown short / medium / long, ticket wait, swap grace 2 / 3 / 4 / 5+ enemies, guard, refill,",
              "        /// off-screen bonus.",
              "        public static readonly float[][][] Steps =",
              "        {"]
    for name in ARCH:
        st = steps(name)
        lines.append("            new[]   // " + name)
        lines.append("            {")
        for k in STEPS:
            lines.append("                new[] { %s },   // %s" % (", ".join(f(x) for x in st[k]), k))
        lines.append("            },")
    lines += ["        };", "",
              "        /// UYF's movement collider radius per archetype (the order of Archetypes): standing, seated (m).",
              "        public static readonly float[][] Radius =",
              "        {"]
    for name in ARCH:
        lines.append("            new[] { %s },   // %s" % (", ".join(f(x) for x in RADIUS[name]), name))
    lines += ["        };", "",
              "        /// UYF's blocking per archetype (the order of Archetypes): the hits in 3 s it takes before it blocks, 1 = it",
              "        /// blocks 0.5 s after its own attack, the seconds a block may last before it costs the turn.",
              "        public static readonly float[][] Block =",
              "        {"]
    for name in ARCH:
        lines.append("            new[] { %s },   // %s" % (", ".join(f(x) for x in BLOCK[name]), name))
    lines += ["        };", "",
              "        /// UYF's tickets per archetype (the order of Archetypes): possession (s), fitness High, Moderate, AttackReadied.",
              "        public static readonly float[][] Ticket =",
              "        {"]
    for name in ARCH:
        lines.append("            new[] { %s },   // %s" % (", ".join(f(x) for x in TICKET[name]), name))
    lines += ["        };", "",
              "        /// UYF's waiting enemies per archetype (the order of Archetypes): on deck - distance, edge, bullseye (m); standby -",
              "        /// distance, edge, bullseye (m); the approach time-out (s); the priority drop after losing the ticket (s).",
              "        public static readonly float[][] Standby =",
              "        {"]
    for name in ARCH:
        lines.append("            new[] { %s },   // %s" % (", ".join(f(x) for x in STANDBY[name]), name))
    lines += ["        };", "    }", "}", ""]
    open(OUT, "w").write("\n".join(lines))
    print("wrote %s: %d archetypes, %d families" % (OUT, len(ARCH), len(rs)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
