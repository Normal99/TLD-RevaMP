#!/usr/bin/env python3
"""Roll probe: passenger ride while sampling the contact-impulse counters on both machines.
If the laptop's seated capsule still contacts the proxy car, contactSent/Relayed/Applied grow
and the host's real car gets impulse-kicked at a lateral offset — the sustained roll."""
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

def counters(x, net):
    d = x("mp entdebug")
    rows = x("mp entities")["entities"]
    car = next((e for e in rows if e["net"] == net), None)
    out = {k: d.get(k) for k in ("contactSent", "contactRelayed", "contactApplied")}
    if car: out.update(upy=car.get("upy"), pos=car.get("pos"))
    return out

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
        L("set kaposztaleves.s.settings.god True"); b("set kaposztaleves.s.settings.god True")
        rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        cx, cz = rp["x"] + F[0] * 15, rp["z"] + F[1] * 15
        g = b(f"ground {cx} {cz}")
        b(f"spawnpile 223 1 {cx} {g['y']} {cz} 1 1 1.0 4242"); time.sleep(4)
        net = min(((e["net"], math.dist(e["pos"], (cx, g["y"], cz))) for e in b("mp entities")["entities"] if e["part"] < 0),
                  key=lambda t: t[1])[0]
        b(f"tpg {cx} {cz + 8}")
        for _ in range(4):
            b(f"drivein {cx} {g['y']} {cz}"); time.sleep(2); b(f"drivein {cx} {g['y']} {cz}"); time.sleep(3)
            if b("get mainscript.s.player.Bsitting")["value"]: break
        print("  host seated:", b("get mainscript.s.player.Bsitting")["value"])
        for _ in range(4):
            L(f"mp sitpassenger {net}"); time.sleep(2)
            L(f"mp sitpassenger {net}"); time.sleep(2)
            if L("get mainscript.s.player.Bsitting")["value"]: break
        print("  laptop seated:", L("get mainscript.s.player.Bsitting")["value"])
        print("  baseline host:", counters(b, net))
        print("  baseline laptop:", counters(L, net))
        b("autodrive on 20")
        for i in range(6):
            time.sleep(1)
            print(f"  t{i+1} host:", counters(b, net))
            print(f"  t{i+1} laptop:", counters(L, net))
        b("autodrive off"); time.sleep(2)
finally:
    try: b("autodrive off")
    except Exception: pass
    try: b("driveout")
    except Exception: pass
    try: L("driveout")
    except Exception: pass
    try: b("mp stop")
    except Exception: pass
    try: L("mp stop")
    except Exception: pass
    L.close()
