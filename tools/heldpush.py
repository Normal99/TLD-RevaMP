#!/usr/bin/env python3
"""A held item pressed against someone else's car (two machines, relay iad by default). Fluids run 16: the laptop's can,
placed inside the host's engine bay, made the host's parked car lose 13 parts at once (carscript.DamageStuff: the
game tears parts off on a sudden speed change, and a body pushed out of something it sits inside gets exactly that).
Here the way a player does it: can in hand (picked up at arm's length), walking into the car (the forward key held).
  1 OWNER  : the host presses its own can against its own car — the single-machine reference
  2 VISITOR: the laptop presses its can against the host's car
Per phase on the owner: parts attached before/after, how far the car was moved, crash causes logged (Diagnostics.
CrashCause: the jolt, the car's last hits, everything sunk into it). Verdict: no part comes off, the car isn't flung.
  tools/heldpush.py [--no-build] [--relay iad|off] [--secs 5]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP
from roadkit import until, g, car_ready

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY, SECS = arg("--relay", "iad"), arg("--secs", 5.0, float)
if RELAY == "off": RELAY = None
JERRYCAN = 72

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def unit(yaw): return (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))

def new_can(side, who):
    me = side("mp entities")["me"]
    before = {e["net"] for e in side("mp entities")["entities"]}
    print(f"  {who} jerry can:", side(f"devspawn {JERRYCAN}"), flush=True)
    got = []
    until(lambda: got.extend(e["net"] for e in side("mp entities")["entities"]
                             if e["net"] not in before and e["owner"] == me and not e["car"]) or got, 10)
    return got[0] if got else None

def carpos(s, net):
    e = next((e for e in s("mp entities")["entities"] if e["net"] == net), None)
    return e["pos"] if e else None

def phase(tag, pusher, can, car, gx, gz):
    print(f"  {tag}: to the side of the car:", pusher(f"tpground {gx} {gz}"), flush=True); time.sleep(2)
    parts0 = len(b(f"mp attachedparts {car}")["parts"]); lp0 = len(L(f"mp attachedparts {car}")["parts"])
    pos0 = carpos(b, car); cc0 = g(b, "TLDRevamp.Diagnostics.CrashCause.Logged")
    print(f"  {tag}: hold:", pusher(f"mp holdnet {can}"), flush=True); time.sleep(1)
    print(f"  {tag}: walk into the car:", pusher(f"mp walkat {car} {SECS}"), flush=True)
    for k in range(int(SECS) + 3):
        time.sleep(1)
        p = carpos(b, car)
        print(f"    t{k + 1:>3}s  car moved {math.dist(p, pos0):.2f} m  held {g(pusher, 'mainscript.s.player.pickedUp')}  "
              f"crash causes {g(b, 'TLDRevamp.Diagnostics.CrashCause.Logged')}", flush=True)
    if g(pusher, "mainscript.s.player.pickedUp") not in (None, "null"): pusher("dropheld")
    time.sleep(3)
    parts1 = len(b(f"mp attachedparts {car}")["parts"]); lp1 = len(L(f"mp attachedparts {car}")["parts"])
    moved = math.dist(carpos(b, car), pos0); cc1 = g(b, "TLDRevamp.Diagnostics.CrashCause.Logged")
    if cc1 != cc0: print(f"  {tag}: owner's crash cause: {g(b, 'TLDRevamp.Diagnostics.CrashCause.Last')}", flush=True)
    lc = g(L, "TLDRevamp.Diagnostics.CrashCause.Last")
    if lc: print(f"  {tag}: laptop's last crash cause: {lc}", flush=True)
    verdict(f"{tag}: NO PART CAME OFF", parts1 == parts0 and lp1 == lp0, f"owner {parts0} → {parts1}, laptop's copy {lp0} → {lp1}")
    verdict(f"{tag}: THE CAR WASN'T FLUNG", moved < 1.0, f"moved {moved:.2f} m; crash causes logged {cc1 - cc0}")

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
        b(f"tpground {cx - R[0] * 10} {cz - R[1] * 10}"); time.sleep(2)
        if not car_ready(b, cx, cz, yaw, "host (its car)"): sys.exit("host car not ready")
        me = b("mp entities")["me"]; gp = b("gpos")
        cars = [e for e in b("mp entities")["entities"] if e["car"] and e["owner"] == me and not e["dormant"]]
        car = min(cars, key=lambda e: math.hypot(e["pos"][0] - gp["gx"], e["pos"][2] - gp["gz"]))["net"]
        b("brakecar"); b("handbrake on"); time.sleep(1)
        b("driveout"); time.sleep(1)
        b(f"tpground {cx - R[0] * 6} {cz - R[1] * 6}"); time.sleep(2)
        L(f"tpground {cx + R[0] * 6} {cz + R[1] * 6}"); time.sleep(3)
        hcan, lcan = new_can(b, "host"), new_can(L, "laptop")
        if hcan is None or lcan is None: sys.exit("no cans")
        time.sleep(2)
        phase("1 OWNER PUSHES", b, hcan, car, cx - R[0] * 3.5, cz - R[1] * 3.5)
        b(f"tpground {cx - R[0] * 8} {cz - R[1] * 8}"); time.sleep(2)
        phase("2 VISITOR PUSHES", L, lcan, car, cx + R[0] * 3.5, cz + R[1] * 3.5)
        L("mp stop"); b("mp stop")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")
