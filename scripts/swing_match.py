#!/usr/bin/env python3
"""Combat Stage 1: does the block bar sit where the enemy's blow really passes? (the match test)

  swing_match.py [TSV ...] [--min 0.85] [--type-min 0.6] [--tag NAME]

Reads swing traces (Game/SwingTrace.cs via scripts/swing_probe.py or stage1_test.py; default testdata/probes/V*_*.tsv)
and counts, per enemy type and overall, the blows whose bar matched the blade: in the blade's last 0.25 s up to its
impact (and 0.05 s after) it came within 0.15 m of the bar in the bar's plane (within 0.4 m in depth) and, for blows
across that plane, the bar lay square to the blade's motion within 25 deg. "now" = the bar as placed since Stage 1 (the
swing atlas), "before" = the bar as placed before (Guard.LegacyBar) - on the same blows. Unblockable blows show an arrow,
not a bar: they are counted apart (their arrow does not depend on the bar's place). PASS when the blockable blows match
at least --min overall and every type with >= 3 blockable blows at least --type-min. Exit 1 on FAIL.
"""
import csv
import glob
import os
import statistics
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hof2env  # noqa: E402

H = hof2env.H


def main(a):
    mn = float(a[a.index("--min") + 1]) if "--min" in a else 0.85
    tmn = float(a[a.index("--type-min") + 1]) if "--type-min" in a else 0.6
    paths = [x for i, x in enumerate(a) if not x.startswith("--") and (i == 0 or a[i - 1] not in ("--min", "--type-min", "--tag"))]
    if not paths:
        paths = sorted(glob.glob(H + "/testdata/probes/V*_*.tsv"))
    rows = []
    for p in paths:
        with open(p) as f:
            rows += [r for r in csv.DictReader(f, delimiter="\t") if r.get("match") in ("0", "1")]
    if not rows:
        print("no traced blows in", paths)
        return 1
    # a blow whose blade never comes to the player (flat reach > 0.6 m from the guard point: the templar's first hit lands
    # at 1.8 m) can't have a bar "where it passes": counted apart, as the atlas does (Guard.ReachLimit)
    def reach(r):
        a, b, hd = [float(x) for x in r["root_a"].split(",")], [float(x) for x in r["root_b"].split(",")], float(r["hero_dist"])
        dx, dz = b[0] - a[0], b[2] - a[2]
        ll = dx * dx + dz * dz
        t = 0.0 if ll < 1e-9 else max(0.0, min(1.0, ((0.0 - a[0]) * dx + (hd - 0.35 - a[2]) * dz) / ll))
        return ((a[0] + dx * t) ** 2 + (a[2] + dz * t - (hd - 0.35)) ** 2) ** 0.5
    far = [r for r in rows if r.get("root_a") and reach(r) > 0.6]
    rows = [r for r in rows if r not in far]
    block = [r for r in rows if not r["colour"].startswith("Red")]
    red = [r for r in rows if r["colour"].startswith("Red")]

    def line(rs):
        if not rs:
            return "-"
        now = sum(r["match"] == "1" for r in rs)
        old = sum(r["old_match"] == "1" for r in rs)
        ip = statistics.median(float(r["in_plane"]) for r in rs)
        oip = statistics.median(float(r["old_in_plane"]) for r in rs)
        ang = [float(r["angle_off"]) for r in rs if r["angle_off"] != "thrust"]
        oang = [float(r["old_angle_off"]) for r in rs if r["old_angle_off"] != "thrust"]
        return "now %d/%d (%.0f %%), in-plane median %.2f m, angle median %s | before %d/%d (%.0f %%), in-plane median %.2f m, angle median %s" % (
            now, len(rs), 100.0 * now / len(rs), ip, ("%.0f deg" % statistics.median(ang)) if ang else "-",
            old, len(rs), 100.0 * old / len(rs), oip, ("%.0f deg" % statistics.median(oang)) if oang else "-")

    print("files: " + ", ".join(os.path.basename(p) for p in paths))
    print("blockable blows: " + line(block))
    print("unblockable blows (arrows; bar for reference): " + line(red))
    print("blows that never come to the player (flat reach > 0.6 m; UYF's palette bar): %d (%s)" % (
        len(far), ", ".join(sorted(set("%s %s@%s" % (r["enemy"].replace("Model", ""), r["clip"][:24], r["clip_event"]) for r in far)))))
    ok = True
    types = sorted(set(r["enemy"] for r in rows))
    for t in types:
        tb = [r for r in block if r["enemy"] == t]
        tr = [r for r in red if r["enemy"] == t]
        flag = ""
        if len(tb) >= 3:
            share = sum(r["match"] == "1" for r in tb) / len(tb)
            if share < tmn:
                ok = False
                flag = "  <-- below %.0f %%" % (100 * tmn)
        print("  %-30s blockable %s%s%s" % (t, line(tb), flag, ("  | red %d" % len(tr)) if tr else ""))
    if block:
        share = sum(r["match"] == "1" for r in block) / len(block)
        if share < mn:
            ok = False
    print(("PASS" if ok else "FAIL") + " match: blockable blows >= %.0f %% overall, each type >= %.0f %%" % (100 * mn, 100 * tmn))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
