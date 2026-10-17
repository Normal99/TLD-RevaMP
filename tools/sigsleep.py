#!/usr/bin/env python3
"""Bug 8 (the car frozen by distance: 250 m away, between the game's freeze (200 m) and store (300 m) distances)
 (2026-10-03, "engine state can get unsynced"): a car at rest sends no states, so an engine/ignition change made
while it rests never reached the others. Desktop only, owner side: the host sits in its car, waits until the car has
come to rest (no states going out), then switches the ignition (and back): each change must send states at once
(Entities.CarSignalKey: three in a row).
  tools/sigsleep.py [--no-build] [--car Car06Full]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

CAR = sys.argv[sys.argv.index("--car") + 1] if "--car" in sys.argv else "Car06Full"
T = "TLDRevamp.Net.Entities"
VERDICTS = []

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def ents(b): return {e["net"]: e for e in b("mp entities")["entities"]}

def seat_idx(b):
    seats = b("seatraw")["seats"]
    target = next(s["i"] for s in seats if s["main"] == "null" and s["d0"])
    return next(s["i"] for s in seats if s["main"] == f"#{target}")

def at_rest(b, net, quiet=3.0, timeout=40):
    """seqOut unchanged for `quiet` seconds"""
    t0 = time.time(); last = ents(b)[net]["seqOut"]; since = time.time()
    while time.time() - t0 < timeout:
        time.sleep(0.5)
        s = ents(b)[net]["seqOut"]
        if s != last: last, since = s, time.time()
        elif time.time() - since >= quiet: return last
    e = ents(b)[net]
    print(f"  not at rest: why [awake,pos,rot,stored] {e['why']} vel {e['vel']} pos {e['pos']} sentRest {e['sentRest']}", flush=True)
    return None

if game.running(): game.quit_game()
if "--no-build" not in sys.argv: game.build()
else: game.deploy_local()
game.launch()
with game.ProtectedSession():
    game.newgame()
    b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True")
    god = b("get kaposztaleves.s.settings.god").get("value"); b("set kaposztaleves.s.settings.god True")
    try:
        p = b("gpos"); b(f"tpground {p['gx'] + 15} {p['gz']}"); time.sleep(3)
        b("mp host 27070")
        before = set(ents(b))
        print("  car:", b(f"devspawn {CAR}"), flush=True); time.sleep(8)
        net = next(n for n, e in ents(b).items() if n not in before and e["car"])
        cp = ents(b)[net]["pos"]; b(f"tpground {cp[0] + 3} {cp[2] + 3}"); time.sleep(2)
        print("  sits:", b(f"sitseat {seat_idx(b)} redirect"), flush=True)
        b(f"set {T}.TestHandbrake 1"); time.sleep(2); b(f"set {T}.TestHandbrake -1")
        print("  gets out:", b("driveout"), flush=True)
        # a car's body stays awake parked (sigsleep run 3: 1,859 'awake' states at 0 m/s) and the owner keeps sending
        # it; a car goes quiet when the game freezes it (itemFreezeDist, 200 m from every player; stored at 300 m)
        b(f"tpground {cp[0] + 250} {cp[2]}"); time.sleep(3)
        for step, val in (("ON", 1), ("OFF", 0)):
            s0 = at_rest(b, net)
            if s0 is None: verdict(f"IGNITION {step}: CAR CAME TO REST", False, "states never stopped"); continue
            print(f"  set:", b(f"mp carset {net} ignition {val}"), flush=True); time.sleep(1.0)
            s1 = ents(b)[net]["seqOut"]
            sig = b(f"mp carsig {net}")
            print(f"  ignition {step}: seqOut {s0} -> {s1}, car {sig}", flush=True)
            verdict(f"IGNITION {step} AT REST: STATES SENT AT ONCE", s1 - s0 >= 3 and sig["ignition"] == bool(val), f"{s1 - s0} states")
    finally:
        for k, v in (("TestIgnition", -1), ("TestHandbrake", -1)):
            try: b(f"set {T}.{k} {v}")
            except Exception as ex: print("restore", k, ex)
        try: b("driveout"); b("mp stop")
        except Exception: pass
        b(f"set kaposztaleves.s.settings.god {god if god is not None else False}")
game.quit_game()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
