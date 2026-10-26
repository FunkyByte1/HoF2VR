#!/usr/bin/env python3
"""The seated reach measurements as a doc (2026-10-13, seated mode; as docs/reach-2026-10-11.txt for standing).

  reach_doc.py OUT.txt PREFIX [PREFIX ...]

Reads the reach dumps testdata/reach/<PREFIX>*.txt (written by the mod's `reach` command, Diag/ReachProbe.cs - e.g. the
seated sweep's SW_* dumps and the seated tour's PT_* ones) and writes OUT: a summary table (per dump: the touch targets
OK / with a lean / out of reach / in the lap, the farthest touch target by shoulder distance) and then every touch target
line of every dump.
"""
import glob
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hof2env  # noqa: E402

LINE = re.compile(r"^\s+(.+?)\s+\(-?[\d.]+,-?[\d.]+,-?[\d.]+\) d=([\d.]+) m \(ahead (-?[\d.]+), below (-?[\d.]+), right (-?[\d.]+)\) \w+.*?\| seated (\S+) \(shoulder ([\d.]+) m\).*\[touch\]")


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    out, prefixes = sys.argv[1], sys.argv[2:]
    files = []
    for p in prefixes:
        files += sorted(glob.glob(os.path.join(hof2env.TESTDATA, "reach", p + "*.txt")), key=os.path.getmtime)
    rows, detail = [], []
    tot = [0, 0, 0, 0, 0]
    for f in files:
        name = os.path.basename(f)[:-4]
        with open(f, errors="replace") as fh:
            txt = fh.read()
        player = re.search(r"\nplayer: (.*)", txt)
        posture = re.search(r"\(seated: ([^;]*);", txt)
        targets = []
        for line in txt.splitlines():
            m = LINE.match(line)
            # (dumps before 2026-10-14 00:00 marked every token as a touch target; a token is touched only at the end coins)
            if m and m.group(1).strip().startswith("token") and "coins" not in name.lower() and "Token" not in txt.split("\n", 1)[0]:
                continue
            if m:
                targets.append((m.group(1).strip(), float(m.group(2)), float(m.group(3)), float(m.group(4)), float(m.group(5)), m.group(6), float(m.group(7))))
        if not targets:
            continue
        n = {k: sum(1 for t in targets if t[5].startswith(k)) for k in ("OK", "LEAN", "FAR")}
        lap = sum(1 for t in targets if t[5].endswith("LAP"))
        far = max(targets, key=lambda t: t[6])
        rows.append((name, len(targets), n["OK"], n["LEAN"], n["FAR"], lap, far))
        detail.append("\n################ %s (%s)\n%s" % (name, posture.group(1) if posture else "-", ("player: " + player.group(1)) if player else ""))
        for t in sorted(targets, key=lambda t: -t[6]):
            detail.append("  %-44s shoulder %.2f m  %-9s (eyes %.2f m: ahead %.2f, below %.2f, right %.2f)" % (t[0][:44], t[6], t[5], t[1], t[2], t[3], t[4]))
    with open(out, "w") as fh:
        fh.write("# Hand of Fate 2 VR - seated reach, measured (seated mode, 2026-10-13/14)\n#\n")
        fh.write("# From the mod's `reach` dumps (src/HoF2VR/Diag/ReachProbe.cs) of seated test runs: the simulated eyes at a seated\n")
        fh.write("# 1.05 / 1.20 / 1.35 m (xrsim `seat`), the game seated (the table's top 0.75 m above the real floor). Every thing the\n")
        fh.write("# player touches at that moment (map places, the figurine, dice, cards to pick, plates of every board, tokens, the\n")
        fh.write("# deck builder's rows) rated for a seated player who can't step: from the shoulder nearer it (0.20 m below the eyes,\n")
        fh.write("# 0.05 m behind, 0.18 m to the side) the fingertip reaches 0.62 m (OK); leaning forward from the hips (the shoulders\n")
        fh.write("# 0.20 m forward, 0.05 m down) the same arm (LEAN); beyond that FAR; LAP = over the thighs, more than 0.50 m below\n")
        fh.write("# the eyes. Standing reach for comparison: docs/reach-2026-10-11.txt.\n\n")
        fh.write("%-44s %7s %4s %5s %4s %4s  %s\n" % ("dump", "targets", "OK", "LEAN", "FAR", "LAP", "farthest (shoulder distance)"))
        for r in rows:
            fh.write("%-44s %7d %4d %5d %4d %4d  %s %.2f m (%s)\n" % (r[0][:44], r[1], r[2], r[3], r[4], r[5], r[6][0][:38], r[6][6], r[6][5]))
        tot = [sum(r[i] for r in rows) for i in range(1, 6)]
        fh.write("%-44s %7d %4d %5d %4d %4d\n" % ("ALL", *tot))
        fh.write("\n".join(detail) + "\n")
    print("%d dumps, %d targets: OK %d, LEAN %d, FAR %d, LAP %d -> %s" % (len(rows), tot[0], tot[1], tot[2], tot[3], tot[4], out) if rows else "no dumps with touch targets")


if __name__ == "__main__":
    main()
