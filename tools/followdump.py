#!/usr/bin/env python3
"""What the game's arrayfollow scripts move on a car01 (copycost v0.57.85: arrayfollow.Update 55 ms/s on the laptop with
16 cars, ~76 µs a call — a few transform writes shouldn't cost that). Single player on the desktop: a car01 next to the
player, then `followdump` and the script's own cost with that car parked.
  tools/followdump.py [--no-build]"""
import sys, time, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import ensure_both
from roadkit import CAR01

ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    seeded.restart_same_seed(); b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True")
    rp = b("roadpoint")
    print("spawn:", b(f"spawncarg {CAR01} 0 {rp['gx'] + 6} {rp['gz']} 0"), flush=True); time.sleep(4)
    d = b("followdump 40")
    print("arrayfollows in the scene:", d["arrayfollows"])
    for a in d["sample"]:
        print(f"\n{a['root']} / {a['on']} inv={a['inv']}")
        for p in a["pairs"]: print("   ", json.dumps(p))
    b("profile start arrayfollow.Update"); time.sleep(10)
    pr = b("profile stop 5")
    for m in pr.get("byTime", []): print(f"\n{m['m']}: {m['msPerSec']} ms/s, {m['callsPerSec']} calls/s, max {m.get('maxMs')} ms")
