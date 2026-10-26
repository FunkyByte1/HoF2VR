#!/usr/bin/env python3
"""Where the table's garbage comes from (2026-10-13, the table's GC stalls part 2; the user: "if you can solve it, please do").

  alloc_probe.py [--start] [--tag NAME] [--secs 45]

At the dungeon map (session.py start --snapshot magician_mid --to table with --start): the update profiler in its
allocation mode (`profile alloc`, Diag/UpdateProfiler.cs: every Update / LateUpdate / FixedUpdate of the game and the mod,
plus the mod's render-time hooks added by name) attributes the managed heap's growth to the method running when the heap
took a new block (statistical). Two windows: the player looking round the map (idle), then the table's everyday events
(gains and losses: a card zoomed, flying to its stat card). Each window's report goes to the game's log (`alloc window ...`)
and its summary to logs/alloc_probe.log.
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import playtest as pt  # noqa: E402

H = pt.H
pt.LOG = H + "/logs/alloc_probe.log"
log, sim, send, st = pt.log, pt.sim, pt.send, pt.st
# the mod's render-time and per-frame hooks that are not Update / LateUpdate (camera callbacks, onBeforeRender, the Harmony
# patches on the game's hot methods)
HOOKS = ("VRRig.BeforeRender VRRig.PreCull VRRig.PostRender VRRig.LatePose KeepOut.PreCull ChoiceZoom.PreCull ChoiceZoom.PostRender "
         "Metronome.PreCull LoadSmooth.PreCull LoadSmooth.BeforeRender HandFeel.BeforeRender PerfMonitor.BeforeRender PerfMonitor.FirstCull "
         "CullingGuard.PreCull Proximity.Measure Diag.OnAnyPostRender TableView.Postfix TableView.UIAnchorPrefix TableView.UIAnchorPostfix "
         "Places.AnchorPrefix Places.AnchorPostfix Places.FixedCameraPostfix TableHud.AfterAnchor PoseProbe.BeforeRender PoseProbe.PreCull "
         "LumaProbe.OnPost OverlayMarker.OnPreCull TrackerHook.OnPreCull "
         # the hands' per-frame path, one level down (the fight window of 23:40: VRHands.LateUpdate, the late pose and
         # HandFeel.BeforeRender ~150 KB/s each)
         "VRHands.UpdateHands VRHands.Ghost VRHands.Show HandFeel.Late HandFeel.ProbeFrame HandFeel.Update VRInput.Update VRInput.LateHands "
         "TableCardGrab.Late FigurineGrab.Late HandWorld.Get FingerSampler.Get Health.Update TouchInput.TipOf")


def look_round(secs):
    t_end = time.time() + secs
    poses = [(0, -25, 1500), (-35, -15, 1800), (30, -10, 2000), (0, -40, 1500), (0, -5, 1200)]
    i, last = 0, (0, -5)
    while time.time() < t_end:
        y, p, ms = poses[i % len(poses)]
        sim("turn %d %d %d %d %d" % (last[0], y, last[1], p, ms))
        last = (y, p)
        i += 1
        time.sleep(0.3)


def window(name, fn, secs):
    send("profile alloc", 60)
    time.sleep(1.0)
    t0 = time.time()
    fn(secs)
    while time.time() - t0 < secs:
        time.sleep(0.5)
    r = send("profile allocs 30", 20)
    log("%-8s %s" % (name, r))
    return r


def events(secs):
    t_end = time.time() + secs
    for label in ("Gain_gold", "Lose_food", "Gain_life", "Lose_gold", "Gain_food", "Max_health_up"):
        if time.time() > t_end:
            break
        send("scen here " + label)
        time.sleep(7)
        for _ in range(6):
            s = st()
            if s.get("menu") == "Map":
                break
            if "nav:Continue" in s.get("boards", ""):
                send("ptouch nav:Continue")
            time.sleep(1.0)


def main():
    a = sys.argv[1:]
    secs = float(a[a.index("--secs") + 1]) if "--secs" in a else 45
    tag = a[a.index("--tag") + 1] if "--tag" in a else time.strftime("%H%M")
    open(pt.LOG, "a").close()
    log("==== alloc probe %s" % tag)
    if "--start" in a:
        subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"], capture_output=True, text=True, timeout=4200)
    sim("seat 0; " + pt.REST + "; head 0 1.75 0 0 -5 0")
    send("cfg Scenarios true")
    send("gate auto 1.5")
    # at the dungeon map (a fight left open by an earlier test: won, back to the table)
    import table_sweep as ts
    if "Level_" in pt.state():
        send("god on")
        send("killall")
        time.sleep(3)
        if "card (" in st().get("fightExit", ""):
            send("exitcard touch")
        else:
            send("jump win")
        pt.wait_state(["GameState_Map"], 90)
    ts.map_ready(90, act=True)
    log("at %s, menu %s" % (pt.state(), st().get("menu")))
    log("updates: " + send("profile on", 120)[:200])   # every Update / LateUpdate first, then the hooks by name
    r = send("profile add " + HOOKS, 60)
    log("hooks: " + r[:300])
    window("idle", look_round, secs)
    window("events", events, secs)
    send("profile off")


if __name__ == "__main__":
    main()
