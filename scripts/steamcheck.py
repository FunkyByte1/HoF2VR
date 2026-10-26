#!/usr/bin/env python3
"""Does a test run reach the user's Steam client? (2026-10-18, the user: test runs must not count toward their Steam playtime.)
Watches env 1's test game processes (HOF2VR_TEST=1, run from the Steam install) until STOP-file or the time is up, and for each
one reads - read only, never writing any Steam file - Steam's gameprocess_log.txt ('Game process added ... AppID 456670 ...
ProcID <pid>') and 456670's Playtime in localconfig.vdf when it starts and when it ends.
  steamcheck.py SECONDS   -> one line per test process and a summary (PASS: no Steam registration, the playtime unchanged)
"""
import os
import re
import sys
import time

STEAM = os.path.expanduser("~/.local/share/Steam")
GAME = os.path.join(STEAM, "steamapps/common/Hand of Fate 2")
GPLOG = os.path.join(STEAM, "logs/gameprocess_log.txt")
LOCALCFG = os.path.join(STEAM, "userdata/STEAM_ACCOUNT_ID/config/localconfig.vdf")


def playtime():
    try:
        with open(LOCALCFG, errors="replace") as f:
            txt = f.read()
        i = txt.find('"apps"')
        while i >= 0:
            j = txt.find('"456670"', i)
            if j < 0:
                return None
            k = txt.find("{", j)
            if k >= 0 and txt[j + 8:k].strip() == "":
                m = re.search(r'"Playtime"\s+"(\d+)"', txt[k:k + 2000])
                return int(m.group(1)) if m else None
            i = j + 8
    except OSError:
        return None
    return None


def gp_lines():
    try:
        with open(GPLOG, errors="replace") as f:
            return f.readlines()
    except OSError:
        return []


def test_games():
    out = set()
    for pid in os.listdir("/proc"):
        if not pid.isdigit():
            continue
        try:
            if os.path.basename(os.readlink("/proc/%s/exe" % pid)) != "Hand of Fate 2.x86_64":
                continue
            if not os.readlink("/proc/%s/exe" % pid).startswith(GAME):
                continue
            with open("/proc/%s/environ" % pid, "rb") as f:
                env = f.read().split(b"\0")
            if b"HOF2VR_TEST=1" in env:
                out.add(int(pid))
        except OSError:
            continue
    return out


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 900
    t0 = time.time()
    seen = {}
    n0 = len(gp_lines())
    print(time.strftime("%H:%M:%S ") + "start: Playtime %s min, gameprocess_log %d lines" % (playtime(), n0), flush=True)
    fails = 0
    while time.time() - t0 < secs:
        now = test_games()
        for p in now - set(seen):
            seen[p] = {"start": time.time(), "pt0": playtime()}
            print(time.strftime("%H:%M:%S ") + "test game %d started; Playtime %s min" % (p, seen[p]["pt0"]), flush=True)
        for p in list(seen):
            if p in now or "end" in seen[p]:
                continue
            time.sleep(8)   # (Steam writes the playtime when a game ends)
            seen[p]["end"] = time.time()
            pt1 = playtime()
            hits = [l.strip() for l in gp_lines() if "ProcID %d" % p in l]
            ok = not hits and pt1 == seen[p]["pt0"]
            fails += 0 if ok else 1
            print(time.strftime("%H:%M:%S ") + "test game %d ended after %.0f s; Playtime %s -> %s min; Steam's lines for it: %d%s -> %s" % (
                p, seen[p]["end"] - seen[p]["start"], seen[p]["pt0"], pt1, len(hits), (" (" + hits[0][:120] + ")") if hits else "", "PASS" if ok else "FAIL"), flush=True)
        time.sleep(2)
    print(time.strftime("%H:%M:%S ") + "summary: %d test games watched, %d FAIL; Playtime now %s min" % (len(seen), fails, playtime()), flush=True)


if __name__ == "__main__":
    main()
