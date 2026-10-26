#!/usr/bin/env python3
"""Step 0 of docs/hrtf-plan.md: every AudioSource the game ships with "Spatialize" or "Spatialize Post Effects" ticked.

  hrtf_scan.py <game data folder> <out.tsv> [--jobs N] [--vrguard DIR]

Reads (never writes) every UnityFS file under <data>/StreamingAssets/AssetBundles and <data>/data.unity3d with UnityPy
and writes one row per AudioSource that has either flag set (expected: none), then a summary (comment lines).
A source shipped with the box ticked would use the spatializer the moment the data patch names one, before the mod's
policy sees it (Hrtf.cs sets the flag explicitly on every source it sees, so this list is for knowing, not for fixing).

CPU work, so it is gentle: nice 19, pinned to CPUs 12-15, N worker processes (default 4), and before each file every
worker waits while work/narration/vrguard.py says so (the user in VR, a missing or stale status.json, or a game worker's
quiet flag). Run it with shared/tools-venv/bin/python -I (UnityPy).
"""
import argparse
import multiprocessing as mp
import os
import sys
import time

import UnityPy

SKIP_SUFFIX = (".manifest",)
_guard = None


def files_to_scan(data):
    out = []
    root = os.path.join(data, "StreamingAssets", "AssetBundles")
    for dp, dn, fn in os.walk(root):
        for f in fn:
            p = os.path.join(dp, f)
            if f.endswith(SKIP_SUFFIX):
                continue
            with open(p, "rb") as fh:
                if fh.read(8) != b"UnityFS\0":
                    continue
            out.append(p)
    out.append(os.path.join(data, "data.unity3d"))
    out.sort(key=lambda p: -os.path.getsize(p))   # biggest first: the pool ends evenly
    return out


def init_worker(guard_dir):
    global _guard
    if guard_dir:
        sys.path.insert(0, guard_dir)
        import vrguard
        _guard = vrguard


def scan_one(args):
    path, data = args
    if _guard is not None:
        _guard.wait_clear(log=lambda m: print(m, flush=True), what="the spatialize scan")
    rel = os.path.relpath(path, data)
    rows, n, err = [], 0, ""
    t0 = time.time()
    try:
        env = UnityPy.load(path)
        for obj in env.objects:
            if obj.type.name != "AudioSource":
                continue
            n += 1
            try:
                t = obj.read_typetree()
            except Exception as e:  # noqa: BLE001 - a broken object must not end the scan
                err += "%s/%d: %s; " % (obj.assets_file.name, obj.path_id, type(e).__name__)
                continue
            if "Spatialize" not in t or "SpatializePostEffects" not in t:   # a wrong field name must not read as "not ticked"
                err += "%s/%d: no Spatialize field (keys %s); " % (obj.assets_file.name, obj.path_id, ",".join(sorted(t)[:12]))
                continue
            sp = int(bool(t.get("Spatialize", 0)))
            spp = int(bool(t.get("SpatializePostEffects", 0)))
            if not (sp or spp):
                continue
            go = "-"
            try:
                g = obj.assets_file.objects.get(t["m_GameObject"]["m_PathID"]) if t["m_GameObject"]["m_FileID"] == 0 else None
                if g is not None:
                    go = g.read_typetree().get("m_Name", "-")
            except Exception:  # noqa: BLE001
                pass
            rows.append("\t".join(str(x) for x in (rel, obj.assets_file.name, obj.path_id, go, sp, spp, int(bool(t.get("m_Enabled", 1))),
                                                    int(bool(t.get("m_PlayOnAwake", 0))))))
    except Exception as e:  # noqa: BLE001
        err += "load: %s %s" % (type(e).__name__, e)
    return rel, n, rows, err, time.time() - t0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("data")
    ap.add_argument("out")
    ap.add_argument("--jobs", type=int, default=4)
    ap.add_argument("--vrguard", default="")
    a = ap.parse_args()
    try:
        os.nice(19)
    except OSError:
        pass
    try:
        os.sched_setaffinity(0, {12, 13, 14, 15})
    except OSError:
        pass
    files = files_to_scan(a.data)
    print("scanning %d files (%.1f GB) with %d workers, nice %d, CPUs %s" % (
        len(files), sum(os.path.getsize(p) for p in files) / 1e9, a.jobs, os.nice(0), sorted(os.sched_getaffinity(0))), flush=True)
    t0 = time.time()
    total = flagged = sp = spp = 0
    errors, rows, done = [], [], 0
    with mp.get_context("fork").Pool(a.jobs, initializer=init_worker, initargs=(a.vrguard,)) as pool:
        for rel, n, r, err, secs in pool.imap_unordered(scan_one, [(p, a.data) for p in files]):
            done += 1
            total += n
            rows += r
            if err:
                errors.append(rel + ": " + err)
            if done % 50 == 0 or done == len(files):
                print("  %d/%d files, %d AudioSources, %d flagged, %.0f s" % (done, len(files), total, len(rows), time.time() - t0), flush=True)
    for r in rows:
        f = r.split("\t")
        sp += int(f[4])
        spp += int(f[5])
    with open(a.out, "w") as fh:
        fh.write("# file\tserialized_file\tpath_id\tgameobject\tSpatialize\tSpatializePostEffects\tenabled\tplayOnAwake\n")
        for r in sorted(rows):
            fh.write(r + "\n")
        fh.write("# scanned %s: %d files, %d AudioSources, %d with Spatialize, %d with SpatializePostEffects, %d errors, %.0f s\n" % (
            time.strftime("%Y-%m-%d %H:%M"), len(files), total, sp, spp, len(errors), time.time() - t0))
        for e in errors:
            fh.write("# error " + e + "\n")
    print("done: %d files, %d AudioSources, %d with Spatialize, %d with SpatializePostEffects, %d errors -> %s" % (
        len(files), total, sp, spp, len(errors), a.out), flush=True)


if __name__ == "__main__":
    main()
