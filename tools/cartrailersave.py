#!/usr/bin/env python3
"""The Car Trailer example mod (src/Mods/CarTrailer) through saving and loading, single player: a car trailer spawned
(the full one, 9201), its wheels counted; saved (`mp save`, the autosave) and that save loaded twice in the same game,
the wheels counted each time; then far away and back (the game unloads the items there and places them again). Also
the prefab holder after each load (a new prefab per scene load is a leak). Then a save as the mod made them before the
empty trailer (Probe.Legacy: a load put the saved wheels on top of the trailer's own) loaded with the fix: the extra
wheels go, and the save holds as many items as before they came.
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
    m = re.search(r"wheel slots (\d+), parts in them (\d+), most in one (\d+), attached items (\d+)", s)
    return tuple(int(x) for x in m.groups()) if m else None

def load(n):
    b = game.loadsave("autosave1"); time.sleep(6)
    return b

with game.ProtectedSession():
    if "--no-build" not in sys.argv: game.build()
    else: game.local_mods()
    game.quit_game(); game.launch(); game.newgame()
    b = Bridge(); time.sleep(3)
    ids0 = call(b, "Ids"); print(" ids:", ids0)
    verdict("the full trailer saves as the empty one", "cartrailer 9200, full 9200" in ids0, ids0)
    print(" spawn:", b("devspawn 9201")); time.sleep(6)
    w0 = call(b, "Wheels"); print(" spawned:", w0); p0 = call(b, "Parts").split(": ", 1)[-1]; print(" parts:", p0)
    c0 = counts(w0)
    verdict("spawned with one wheel per slot", c0 is not None and c0[2] == 1 and c0[1] == c0[0], w0)
    s0 = b("mp save"); print(" save:", s0); time.sleep(3)
    for n in (1, 2):
        b = load(n)
        w = call(b, "Wheels"); ids = call(b, "Ids"); print(f" load {n}:", w, "|", ids)
        verdict(f"load {n}: one trailer, one wheel per slot", w.count(" | ") == 0 and counts(w) == c0, w)
        if n == 2:
            h0 = int(ids0.split("holder children ")[1]); h = int(ids.split("holder children ")[1])
            verdict("no new prefab per scene load", h == h0, f"holder children {h0} -> {h}")
        s = b("mp save"); print(" save again:", s); time.sleep(3)
    clean_items = s["items"]
    p = b("gpos"); print(" at", p)
    b(f"tpground {p['gx'] + 3000} {p['gz']}"); time.sleep(15)
    print(" far:", call(b, "Wheels"))
    b(f"tpground {p['gx'] + 4} {p['gz'] + 4}"); time.sleep(15)
    w = call(b, "Wheels"); print(" back:", w)
    verdict("back from far away: one wheel per slot", counts(w) == c0, w)
    s = b("mp save"); print(" save:", s); time.sleep(3)
    clean_items = s["items"]

    # an old save: loaded as before the fix twice (a set of wheels added each time), saved like that
    print(" legacy:", call(b, "Legacy 1"))
    b = load(3); w = call(b, "Wheels"); print(" old-style load:", w); print(" parts:", call(b, "Parts"))
    verdict("old-style load stacks wheels (the bug, reproduced)", counts(w) is not None and counts(w)[2] >= 2, w)
    s = b("mp save"); print(" old-style save:", s); time.sleep(3)
    print(" legacy:", call(b, "Legacy 0"))
    b = load(4); time.sleep(4)
    w = call(b, "Wheels"); print(" fixed load of the old save:", w); p1 = call(b, "Parts").split(": ", 1)[-1]; print(" parts:", p1)
    verdict("an old save loads with one wheel per slot", counts(w) == c0, w)
    s = b("mp save"); print(" save:", s); time.sleep(3)
    verdict("the same parts as when it spawned", p1 == p0, p1)
    # the save grows by about one record per load with or without the trailer (the clean loads above): extras gone
    verdict("the extra wheels are gone from the save", s["items"] <= clean_items + 2, f"items {clean_items} -> {s['items']}")
    b = load(5); w = call(b, "Wheels"); print(" and loaded again:", w)
    verdict("and stays that way", counts(w) == c0, w)
    errs = b("errors"); print(" errors:", str(errs)[:600])
    if "--keep" not in sys.argv: game.quit_game()

print(f"{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed")
