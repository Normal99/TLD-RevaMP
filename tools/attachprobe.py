#!/usr/bin/env python3
"""Items attached WITHOUT parenting (attachablescript.Update puts them on their `point` every frame): where each point
hangs, and whether a floating-origin shift takes it along. farworld run 10: on the desktop the spawn building's
Attachable01–06 + Kolbasz kept their Unity coordinates through a 200 km teleport (global position jumped 202 km).
Fresh seeded world on both machines, single player: list at spawn; teleport 200 km (tpg … hold: same-frame shift) and
back; list again with each item's global position before/after.
  tools/attachprobe.py [--no-build]"""
import sys, time, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        b = seeded_both(L)
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        time.sleep(10)
        for n, s in (("desktop", b), ("laptop", L)):
            a = s("attachpoints")
            print(f"\n=== {n}: {a['count']} attached without parenting", flush=True)
            for it in a["items"]: print("   ", it, flush=True)
        for n, s in (("desktop", b), ("laptop", L)):
            g0 = s("gpos")
            s("set mainscript.s.player.godModeTillGrounded True")
            print(f"{n}: tpg 199055 -36 hold", s("tpg 199055 -36 hold"), flush=True)
            a = s("attachpoints")   # at once: the game streams items 300 m+ away out within ~1 s
            print(f"  {n} after the teleport (origin {s('get mainscript.s.visszarakva')}):", flush=True)
            for it in a["items"]: print("   ", it, flush=True)
            s("set mainscript.s.player.godModeTillGrounded True")
            s(f"tpg {g0['gx']} {g0['gz']} hold"); s("tprelease")
            time.sleep(8)
finally:
    L.close()
