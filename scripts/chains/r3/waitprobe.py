"""Round 3 probe: the on-deck waiter's state, distance and speed every 0.25 s for N s (waiting on); what stopped the waiters."""
import re, sys, time, subprocess
sys.path.insert(0, "/home/bara/Projects/Flat2VR-env2/HandOfFate2/scripts")
import cmd  # noqa: E402
secs = float(sys.argv[1]) if len(sys.argv) > 1 else 45
ROW = re.compile(r"\| (\S+)#(-?\d+) (\w+) (-?[\d.]+) m band ([\d.]+)-([\d.]+) for ([\d.]+) s( act [^|]*)?")
cmd.send("vrknock off", 5)
cmd.send("vrwaiting on", 5)
subprocess.run(["python3", "/home/bara/Projects/Flat2VR-env2/HandOfFate2/scripts/xrsim.py", "head 0 1.75 0 0 0 0; hand r 0.25 0.9 -0.1 0 0 0; hand l -0.25 0.9 -0.1 0 0 0"], capture_output=True)
cmd.send("waiting? reset", 5)
t0 = time.time()
seq = []   # (t, who, dist, lo, hi, age, state, in, v, to)
while time.time() - t0 < secs:
    t = time.time() - t0
    s = cmd.send("waiting?", 5)
    for m in ROW.finditer(s):
        if m.group(3) != "ondeck":
            continue
        a = m.group(8) or ""
        sm = re.search(r"st (\w+) blk (\d+) in ([\d.]+) v ([\d.]+)(?: to ([\d.]+))?", a)
        seq.append((t, m.group(2), float(m.group(4)), float(m.group(5)), float(m.group(6)), float(m.group(7)),
                    sm.group(1) if sm else a.strip()[:30], sm.group(3) if sm else "", sm.group(4) if sm else "", (sm.group(5) or "") if sm else ""))
    time.sleep(0.25)
end = cmd.send("waiting?", 5)
cmd.send("vrwaiting setting", 5)
cmd.send("vrknock setting", 5)
m = re.search(r"stops \{(.*?)\} lastStops \{(.*?)\}", end)
print("stops:", m.group(1) if m else "?")
print("lastStops:", (m.group(2) if m else "?").replace(" ; ", "\n  "))
print("header:", re.sub(r" stops \{.*", "", re.sub(r" \|.*", "", end))[:700])
# the on-deck timeline, runs of the same enemy and state
runs = []
for r in seq:
    k = (r[1], r[6])
    if runs and runs[-1][0] == k and r[0] - runs[-1][2] < 0.6:
        runs[-1][2] = r[0]; runs[-1][4] = r[2]; runs[-1][5] = max(runs[-1][5], float(r[8] or 0)); runs[-1][6] = r[9] or runs[-1][6]
    else:
        runs.append([k, r[0], r[0], r[2], r[2], float(r[8] or 0), r[9], r[5], r[3], r[4]])
for k, a, b, d0, d1, vmax, to, age, lo, hi in runs:
    flag = " NEAR" if d1 < lo - 0.4 or d0 < lo - 0.4 else (" FAR" if d1 > hi + 0.4 or d0 > hi + 0.4 else "")
    print("%5.1f-%5.1f #%s %-10s %.2f->%.2f vmax %.2f%s role age %.1f%s" % (a, b, k[0][-4:], k[1], d0, d1, vmax, (" to " + to) if to else "", age, flag))
