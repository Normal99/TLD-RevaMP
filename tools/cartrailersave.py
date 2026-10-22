#!/usr/bin/env python3
"""The Car Trailer example mod (src/Mods/CarTrailer) through saving and loading, single player: a car trailer spawned,
its wheels counted; saved (`mp save`, the autosave) and that save loaded twice in the same game, the wheels counted
each time; then far away and back (the game unloads the items there and places them again). Also the prefab holder
after each load (a new prefab per scene load is a leak).
  TLD_TEST_MODS=CarTrailer tools/cartrailersave.py [--no-build] [--keep]"""
import os
os.environ.setdefault("TLD_TEST_MODS", "CarTrailer")
import sys, time, pathlib, re
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def call(b, what):
    try: return str(b("call CarTrailer.Probe." + what)["value"])
    except Exception as e: return "ERROR " + str(e)[:300]

def counts(s):
    m = re.search(r"wheel slots (\d+), parts in them (\d+), most in one (\d+)", s)
    return tuple(int(x) for x in m.groups()) if m else None

with game.ProtectedSession():
    if "--no-build" not in sys.argv: game.build()
    else: game.local_mods()
    game.quit_game(); game.launch(); game.newgame()
    b = Bridge(); time.sleep(3)
    ids0 = call(b, "Ids"); print(" ids:", ids0)
    print(" spawn:", b("devspawn 9200")); time.sleep(6)
    w0 = call(b, "Wheels"); print(" spawned:", w0)
    c0 = counts(w0)
    verdict("spawned with one wheel per slot", c0 is not None and c0[2] == 1 and c0[1] == c0[0], w0)
    print(" save:", b("mp save")); time.sleep(3)
    for n in (1, 2):
        b = game.loadsave("autosave1"); time.sleep(5)
        w = call(b, "Wheels"); ids = call(b, "Ids"); print(f" load {n}:", w, "|", ids)
        c = counts(w)
        verdict(f"load {n}: one trailer, one wheel per slot", w.count(" | ") == 0 and c is not None and c == c0, w)
        if n == 2:
            h0 = int(ids0.split("holder children ")[1]); h = int(ids.split("holder children ")[1])
            verdict("no new prefab per scene load", h == h0, f"holder children {h0} -> {h}")
        print(" save again:", b("mp save")); time.sleep(3)
    p = b("gpos"); print(" at", p)
    b(f"tpground {p['gx'] + 3000} {p['gz']}"); time.sleep(15)
    print(" far:", call(b, "Wheels"))
    b(f"tpground {p['gx'] + 4} {p['gz'] + 4}"); time.sleep(15)
    w = call(b, "Wheels"); print(" back:", w)
    verdict("back from far away: one wheel per slot", counts(w) == c0, w)
    if "--keep" not in sys.argv: game.quit_game()

print(f"{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed")
