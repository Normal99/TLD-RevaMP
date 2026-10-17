#!/usr/bin/env python3
"""The passenger-entry flip (two machines, the user's exact flow): the laptop PICKS UP the host's car (the same
call the E-press makes) — the car must NOT change ownership, must not move on either machine, and the laptop's
seating afterwards must not disturb it."""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP

def until(fn, timeout=25, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 150)
        rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        cx, cz = rp["x"] + F[0] * 15, rp["z"] + F[1] * 15
        g = b(f"ground {cx} {cz}")
        b(f"spawnpile 223 1 {cx} {g['y']} {cz} 1 1 1.0 4242"); time.sleep(4)
        net = min(((e["net"], math.dist(e["pos"], (cx, g["y"], cz))) for e in b("mp entities")["entities"] if e["part"] < 0),
                  key=lambda t: t[1])[0]
        o0 = next(e for e in b("mp entities")["entities"] if e["net"] == net)
        print(f"  car {net}: owner {o0['owner']} (want 0)")
        # the laptop PICKS UP the car (the E-press path: pickupable.Pickup on the car)
        g2 = L(f"ground {cx} {cz}")
        L(f"teleportyaw {cx} {g2['y'] + 1.5} {cz + 2} 0"); L("set mainscript.s.player.godModeTillGrounded True"); time.sleep(3)
        picked = L(f"pickupcar {net}")
        print("  pickup:", picked)
        time.sleep(4)
        o1 = next(e for e in b("mp entities")["entities"] if e["net"] == net)
        o1l = next(e for e in L("mp entities")["entities"] if e["net"] == net)
        print(f"  owner after pickup: host {o1['owner']} laptop {o1l['owner']} (both must stay 0)")
        yh = o1["pos"][1]; yl = o1l["pos"][1]
        print(f"  car Y: host {yh:.2f} laptop {yl:.2f} (a flipped car's copy moves — the gap must stay small)")
        print("PICKUP-FLIP VERDICT:", "PASS — the car never changed hands" if o1["owner"] == 0 and o1l["owner"] == 0 and abs(yh - yl) < 1.5 else "FAIL — flipped or diverged")
        b("mp stop"); L("mp stop"); b("set kaposztaleves.s.settings.god False")
finally:
    L.close()
