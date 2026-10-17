#!/usr/bin/env python3
"""Something of yours left in another player's car with the physics lock on (playtest 2026-10-09: "items appear on the
other car which don't appear for them … if something from the environment physlocks on there by accident"). Two
machines over the relay; the host's physics lock set to 3 (restored after).
  The host's car (a copy on the laptop). The laptop's own lamp is put on its back seat ON THE LAPTOP (on its copy of
  the car). Then the host moves its car 6 m. After each step, on both machines: who owns the lamp, is it held by a
  container (parented into the car), where it is in the car's frame — the same everywhere?
  tools/physlockenv.py [--no-build] [--relay iad|off] [--old] [--item 320]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import CAR01

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD, ITEM = arg("--relay", "iad"), "--old" in sys.argv, arg("--item", 320, int)
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def ent(s, net): return next((e for e in s("mp entities")["entities"] if e["net"] == net), None)
def rel(s, car, it):
    r = s(f"mp relpos {car} {it}")["items"]
    return r.get(str(it))

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    lock0 = h("get settingsscript.s.S.IPhysicsLock")["value"]
    try:
        h("set settingsscript.s.S.IPhysicsLock 3")
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_(f"set TLDRevamp.Net.Entities.CopyContainersInert {not OLD}")
        print(f"  {'OLD (copy containers take anything)' if OLD else 'NEW'}; host physics lock 3", flush=True)
        time.sleep(6)
        hp = h("gpos"); me = h("mp entities")["me"]; lme = L("mp entities")["me"]
        cx, cz = hp["gx"] + 8, hp["gz"] + 8
        print("  host car:", h(f"spawncarg {CAR01} 0 {cx} {cz} 0"), flush=True)
        car = []
        until(lambda: bool(car.extend(e["net"] for e in h("mp entities")["entities"] if e["car"] and e["owner"] == me and "Car01" in e["name"]
                                      and math.hypot(e["pos"][0] - cx, e["pos"][2] - cz) < 15) or car), 20)
        car = max(car)
        until(lambda: (ent(L, car) or {}).get("dormant") is False, 20); time.sleep(5)
        L(f"tpground {cx - 3} {cz - 3}"); time.sleep(3)
        before = {e["net"] for e in L("mp entities")["entities"]}
        u = L("gpos"); gy = L(f"ground {u['ux'] + 2} {u['uz']}").get("y")
        print("  laptop's lamp:", L(f"spawnpile {ITEM} 1 {u['ux'] + 2} {gy + 0.5} {u['uz']} 1 1 0 7"), flush=True)
        it = []
        until(lambda: bool(it.extend(e["net"] for e in L("mp entities")["entities"] if e["net"] not in before and e["owner"] == lme and e["part"] < 0) or it), 15)
        if not it: sys.exit("the lamp was not shared")
        it = it[0]
        until(lambda: ent(h, it) is not None, 15); time.sleep(3)
        print("  laptop puts it on the back seat of its copy of the host's car:", L(f"mp putincar {car} {it} 1"), flush=True)

        def check(label):
            time.sleep(6)
            eh, el = ent(h, it) or {}, ent(L, it) or {}
            rh, rl = rel(h, car, it), rel(L, car, it)
            print(f"  [{label}] owner: host sees {eh.get('owner')}, laptop sees {el.get('owner')} | in the car's frame: host {rh} laptop {rl}", flush=True)
            same_owner = eh.get("owner") == el.get("owner")
            close = rh is not None and rl is not None and math.dist(rh["p"] if isinstance(rh, dict) else rh, rl["p"] if isinstance(rl, dict) else rl) < 0.3
            verdict(f"{label}: BOTH AGREE ON THE LAMP", same_owner and close, f"owners {eh.get('owner')}/{el.get('owner')}, host {rh}, laptop {rl}")
        check("1 PUT IN")
        print("  host moves its car 6 m:", h(f"mp moveitem {car} 6 0"), flush=True)
        check("2 CAR MOVED")
        print("  physlock stats:", h("mp physlock 0 0 1").get("storesRefused"), L("mp physlock 0 0 1").get("storesRefused"), flush=True)
    finally:
        try: h(f"set settingsscript.s.S.IPhysicsLock {lock0}")
        except Exception as ex: print("restore physics lock:", ex)
        for s_ in (h, L):
            try: s_("set TLDRevamp.Net.Entities.CopyContainersInert True")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
