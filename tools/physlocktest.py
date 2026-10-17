#!/usr/bin/env python3
"""The host's physics-lock setting rules (v0.64.11, Entities.PhysLockSync). Two machines over the relay.
Each machine used its own setting, so whether a container held an item depended on who owned it. The host's setting
is made to differ from the laptop's (the host's is restored at the end; the laptop's is never touched).
  0 before the session: each machine uses its own setting
  1 after joining: the laptop's containers use the host's setting (the game's own CanStore, on a car both machines
    have), its own setting unchanged
  2 the host changes its setting: the laptop follows
  3 after the session: the laptop uses its own setting again
  tools/physlocktest.py [--no-build] [--relay iad|off] [--car Car06Full]"""
import sys, os, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
CAR = sys.argv[sys.argv.index("--car") + 1] if "--car" in sys.argv else "Car06Full"
VERDICTS = []
S = "settingsscript.s.S.IPhysicsLock"

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}

L = Agent()
ensure_both("--no-build" not in sys.argv)
host_orig = None
with game.ProtectedSession():
    h = seeded_both(L)
    try:
        for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
        host_orig = h(f"get {S}")["value"]; lap = L(f"get {S}")["value"]
        print(f"  settings: host {host_orig}, laptop {lap}", flush=True)
        A = 1 if lap == 0 else 0          # the host's setting for the test: differs from the laptop's (glovebox holds only above 0)
        B = 2 if A == 0 else 0            # what the host changes to mid-session
        if B == lap: B = 1
        h(f"set {S} {A}")
        p0 = L("mp physlock 0 0 1")
        verdict("0 BEFORE: THE LAPTOP USES ITS OWN", p0["used"] == lap, f"{p0}")
        if RELAY != "off": relay_both(h, L, RELAY)
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        hp = h("gpos")
        h(f"tpground {hp['gx'] + 15} {hp['gz']}"); time.sleep(3)
        before = set(ents(h))
        print("  host spawns:", h(f"devspawn {CAR}"), flush=True)
        until(lambda: any(e["car"] and not e["proxy"] for n, e in ents(h).items() if n not in before), 20)
        net = [n for n, e in ents(h).items() if n not in before and e["car"] and not e["proxy"]][0]
        if not until(lambda: net in ents(L), 30): sys.exit("laptop never got the car")
        time.sleep(6)
        c = ents(h)[net]["pos"]
        L(f"tpground {c[0] + 6} {c[2] + 6}"); time.sleep(4)

        def both(tag):
            c = ents(h)[net]["pos"]
            o, l = h(f"mp physlock {c[0]} {c[2]} 4"), L(f"mp physlock {c[0]} {c[2]} 4")
            print(f"  {tag}: host {o}\n  {' ' * len(tag)}  laptop {l}", flush=True)
            return o, l

        o, l = both("1 joined")
        verdict("1 THE LAPTOP USES THE HOST'S SETTING", l["used"] == A and l["local"] == lap and l["host"] == A and l["reads"] > 0,
                f"used {l['used']} (host {A}), own still {l['local']}, game reads {l['reads']}")
        verdict("1b ITS CONTAINERS HOLD LIKE THE HOST'S", l["holds"] and l["holds"] == o["holds"], f"host {o['holds']} laptop {l['holds']}")
        h(f"set {S} {B}"); time.sleep(3)
        o, l = both(f"2 host changed to {B}")
        verdict("2 THE LAPTOP FOLLOWS A CHANGE", l["used"] == B and l["local"] == lap and l["holds"] == o["holds"],
                f"used {l['used']}, holds same {l['holds'] == o['holds']}")
    finally:
        try: L("mp stop"); h("mp stop")
        except Exception as ex: print("stop", ex)
        if RELAY != "off":
            try: h("net relay off"); L("net relay off")
            except Exception: pass
        if host_orig is not None:
            print("  host setting restored:", h(f"set {S} {host_orig}"), flush=True)
    time.sleep(2)
    l = L("mp physlock 0 0 1")
    verdict("3 AFTER: THE LAPTOP USES ITS OWN AGAIN", l["used"] == lap and l["local"] == lap, f"{l}")
L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
