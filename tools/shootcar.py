#!/usr/bin/env python3
"""Shooting a car in MP (two machines, relay iad by default). What a bullet does to a car is the game's own
weaponscript.Shot on the SHOOTER's machine: breakable parts (glass, lights, mirrors: breakablescript → Break destroys
the part, the host spawns its gib), tanks (a hole: a tankcapscript leaking from the tank), an impulse. The mod sends
player damage only (PlayerCombat) — so: does a part the other player shoots break on the owner's car, does the tank
leak there?
  1 non-owner shoots: the laptop shoots the host's parked car01 — up to --parts breakable parts, then the fuel tank
  2 owner shoots    : the host shoots other parts of its own car, and the tank
After each phase both machines' view of the car (mp carbreak: breakable parts by name + position, tanks, amounts).
Verdicts per phase: the same parts broken on both; tank holes on both; the owner's fuel going down if holed.
  tools/shootcar.py [--no-build] [--relay iad|off] [--parts 4]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP
from roadkit import until, g, car_ready

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY, NPARTS = arg("--relay", "iad"), arg("--parts", 4, int)
if RELAY == "off": RELAY = None
MAGNUM = 64

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def unit(yaw): return (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))

def view(s, net):
    v = s(f"mp carbreak {net}")
    return v

def match(parts_a, p):
    """the same part on the other machine: same name, nearest position (ids differ between machines)"""
    c = [q for q in parts_a if q["name"].replace("(Clone)", "") == p["name"].replace("(Clone)", "")]
    return min(c, key=lambda q: math.dist(q["pos"], p["pos"]), default=None)

def summary(v):
    broken = sorted(p["name"] for p in v["parts"] if p["destroyed"] or p["hp"] <= 0)
    return {"parts": len(v["parts"]), "broken": broken, "tanks": [(t["name"], t["amount"], t["caps"]) for t in v["tanks"]]}

def compare(tag, hv0, lv0, hv1, lv1):
    """parts gone (destroyed or no longer listed) and tank caps added, per machine, by name"""
    def gone(v0, v1):
        """by name and count (a destroyed part leaves the list; two headlights share a name)"""
        from collections import Counter
        alive = lambda v: Counter(p["name"].replace("(Clone)", "") for p in v["parts"] if not p["destroyed"] and p["hp"] > 0)
        return sorted((alive(v0) - alive(v1)).elements())
    def holes(v0, v1):
        return {t["name"]: t["caps"] - next((u["caps"] for u in v0["tanks"] if u["name"] == t["name"]), t["caps"]) for t in v1["tanks"]}
    def fuel(v0, v1):
        return {t["name"]: round(t["amount"] - next((u["amount"] for u in v0["tanks"] if u["name"] == t["name"]), t["amount"]), 3) for t in v1["tanks"]}
    gh, gl = gone(hv0, hv1), gone(lv0, lv1)
    hh, hl = holes(hv0, hv1), holes(lv0, lv1)
    fh, fl = fuel(hv0, hv1), fuel(lv0, lv1)
    print(f"  {tag}: parts gone — owner (host) {gh}, laptop's copy {gl}", flush=True)
    print(f"  {tag}: new tank holes — owner {hh}, laptop {hl}; amount change — owner {fh}, laptop {fl}", flush=True)
    if gh or gl: verdict(f"{tag}: SAME PARTS BROKEN ON BOTH", gh == gl, f"owner {gh} / laptop {gl}")
    hole_any = any(v > 0 for v in list(hh.values()) + list(hl.values()))
    if hole_any:
        verdict(f"{tag}: TANK HOLED ON BOTH", any(v > 0 for v in hh.values()) and any(v > 0 for v in hl.values()), f"owner {hh} / laptop {hl}")
        verdict(f"{tag}: OWNER'S TANK LEAKS", any(v < -0.01 for v in fh.values()), f"owner amounts {fh}")
    else:
        print(f"  {tag}: no tank hole made on either machine (the shot may not have reached a tank)", flush=True)

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        b = seeded_both(L)
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("relay only via", RELAY, b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
        b("mp host 27070")
        if RELAY: L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("route:", L("net route"), flush=True)
        time.sleep(4)
        rp = b("roadpoint"); yaw = rp["yaw"]; D = unit(yaw); R = (D[1], -D[0])
        cx, cz = rp["gx"], rp["gz"]
        # nobody on the car's spawn point (shiftdrive runs 8/10)
        b(f"tpground {cx - R[0] * 10} {cz - R[1] * 10}"); time.sleep(2)
        if not car_ready(b, cx, cz, yaw, "host (its car)"): sys.exit("host car not ready")
        me = b("mp entities")["me"]; gp = b("gpos")
        cars = [e for e in b("mp entities")["entities"] if e["car"] and e["owner"] == me and not e["dormant"]]
        car = min(cars, key=lambda e: math.hypot(e["pos"][0] - gp["gx"], e["pos"][2] - gp["gz"]))
        net = car["net"]; print("host car net", net, flush=True)
        b("brakecar"); b("handbrake on"); time.sleep(1)
        b("driveout"); time.sleep(1)
        b(f"tpground {cx - R[0] * 12} {cz - R[1] * 12}"); time.sleep(2)   # the host away on the left
        L(f"tpground {cx + R[0] * 7} {cz + R[1] * 7}"); time.sleep(2)
        print("laptop magnum:", L(f"devspawn {MAGNUM}"), flush=True); time.sleep(2)
        if not until(lambda: "parts" in L(f"mp carbreak {net}"), 15): sys.exit("the laptop has no copy of the car")
        hv0, lv0 = view(b, net), view(L, net)
        print("before — owner:", summary(hv0), flush=True)
        print("before — laptop:", summary(lv0), flush=True)
        # run 2: a car01 has no breakable parts at all — a bullet holes its tanks (weapon hithole) and pushes it


        # ---- 1: the laptop (not the owner) shoots
        right = [p for p in lv0["parts"] if not p["destroyed"]]
        right.sort(key=lambda p: math.hypot(p["pos"][0] - (cx + R[0] * 7), p["pos"][2] - (cz + R[1] * 7)))   # nearest first
        shot1 = right[:NPARTS]
        for p in shot1:
            print(f"  laptop shoots {p['name']} at {p['pos']}:", L(f"mp shootpoint {p['pos'][0]} {p['pos'][1]} {p['pos'][2]}"), flush=True); time.sleep(1.5)
        for t in lv0["tanks"]:
            for _ in range(2):
                print(f"  laptop shoots tank {t['name']} at {t['pos']}:", L(f"mp shootpoint {t['pos'][0]} {t['pos'][1]} {t['pos'][2]}"), flush=True); time.sleep(0.7)
        time.sleep(8)
        hv1, lv1 = view(b, net), view(L, net)
        compare("1 NON-OWNER SHOOTS", hv0, lv0, hv1, lv1)

        # ---- 2: the owner shoots its own car
        b(f"tpground {cx + R[0] * 7} {cz + R[1] * 7}"); time.sleep(2)   # the host to the right, the laptop away
        L(f"tpground {cx - R[0] * 12} {cz - R[1] * 12}"); time.sleep(2)
        print("host magnum:", b(f"devspawn {MAGNUM}"), flush=True); time.sleep(2)
        left = [p for p in hv1["parts"] if not p["destroyed"] and not any(match([q], p) for q in shot1)]
        left.sort(key=lambda p: math.hypot(p["pos"][0] - (cx + R[0] * 7), p["pos"][2] - (cz + R[1] * 7)))
        for p in left[:NPARTS]:
            print(f"  host shoots {p['name']} at {p['pos']}:", b(f"mp shootpoint {p['pos'][0]} {p['pos'][1]} {p['pos'][2]}"), flush=True); time.sleep(1.5)
        for t in hv1["tanks"]:
            for _ in range(2):
                print(f"  host shoots tank {t['name']} at {t['pos']}:", b(f"mp shootpoint {t['pos'][0]} {t['pos'][1]} {t['pos'][2]}"), flush=True); time.sleep(0.7)
        time.sleep(8)
        hv2, lv2 = view(b, net), view(L, net)
        compare("2 OWNER SHOOTS", hv1, lv1, hv2, lv2)
        L("mp stop"); b("mp stop")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")
