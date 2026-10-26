#!/usr/bin/env python3
"""Send a command to the running mod (test mode) and print its result.

  cmd.py "<command>" [timeout_s]        e.g. cmd.py "shot table1"   cmd.py cams
  cmd.py --status                       print status.json
The mod polls testdata/cmd/command.txt every 0.1 s and appends "<seq> <cmd> => <result>" to
testdata/cmd/results.log.
"""
import json
import fcntl
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)

DIR = hof2env.TESTDATA
CMD = os.path.join(DIR, "cmd")


def results():
    try:
        with open(os.path.join(CMD, "results.log")) as f:
            return f.read().splitlines()
    except FileNotFoundError:
        return []


def send(command, timeout=15.0):
    before = len(results())
    # several senders at once (a test and its autoplay helper): one lock, and a command not read yet is kept (appended
    # to), never replaced; each sender has its own temporary file (a shared one made os.replace fail in the other)
    path = os.path.join(CMD, "command.txt")
    tmp = path + ".tmp.%d" % os.getpid()
    with open(os.path.join(CMD, ".send.lock"), "w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        pending = ""
        try:
            with open(path) as f:
                pending = f.read()
        except FileNotFoundError:
            pass
        if pending and not pending.endswith("\n"):
            pending += "\n"
        with open(tmp, "w") as f:
            f.write(pending + command + "\n")
        os.replace(tmp, path)
    deadline = time.time() + timeout
    while time.time() < deadline:
        lines = results()
        for line in lines[before:]:
            if (" " + command + " =>") in line:
                return line.split(" => ", 1)[1]
        time.sleep(0.05)
    raise SystemExit("no answer to %r within %ss (game not running or not in test mode?)" % (command, timeout))


def status():
    with open(os.path.join(DIR, "status.json")) as f:
        return json.load(f)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    if sys.argv[1] == "--status":
        print(json.dumps(status(), indent=1))
    else:
        print(send(sys.argv[1], float(sys.argv[2]) if len(sys.argv) > 2 else 15.0))
