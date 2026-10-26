#!/usr/bin/env python3
"""The Dealer's call to battle with KeepOut's trace on (P2-1 follow-up, 2026-10-18): from the dungeon map, `jump fight
EL_Ambush_Goblins`, the head at rest; traces the encounter card and (4, 2)'s card (kotrace) until the fight card is offered,
then touches it (A) and waits for the fight. Usage: battle_trace.py [TRACE] [--stay]"""
import os, sys, time
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
import cmd as C  # noqa: E402
import pt17  # noqa: E402

trace = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("--") else "Ambush"
pt17.sim(pt17.REST + "; head 0 1.66 0 0 -25 0")
print("gate:", C.send("gate auto 0", 5))
print("trace:", C.send("kotrace %s 1500" % trace, 5))
print("jump:", C.send("jump fight EL_Ambush_Goblins", 10))
time.sleep(3.0)
print("the encounter's option (A):", C.send("press A 6", 5))
t0 = time.time()
while time.time() - t0 < 30 and not C.status().get("fightCard", "").startswith("offered"):
    time.sleep(0.5)
print("fightCard:", C.status().get("fightCard", "")[:120])
print("prox:", C.status().get("proximity", "")[:300])
if "--stay" not in sys.argv:
    C.send("press A 6", 5)   # the fight card: touched (A) - its portal opens in the hand, then the waiting space
    time.sleep(8)
    print("state:", C.send("state?", 5))
