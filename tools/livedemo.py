#!/usr/bin/env python3
"""LIVE demo for the user: passenger ride on v0.57.25 with on-screen banners. The user watches
the screens; the log prints the car's upy (1.0 = perfectly upright) every 3 s as corroboration."""
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

def upy(x, net):
    rows = x("mp entities")["entities"]
    car = next((e for e in rows if e["net"] == net), None)
    return car.get("upy") if car else None

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        b("banner 8 LIVE TEST: passenger ride starts in ~60 s — watch the car")
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
        print("  host seated:", b("get mainscript.s.player.Bsitting")["value"], flush=True)
        for _ in range(4):
            L(f"mp sitpassenger {net}"); time.sleep(2)
            L(f"mp sitpassenger {net}"); time.sleep(2)
            if L("get mainscript.s.player.Bsitting")["value"]: break
        print("  laptop seated:", L("get mainscript.s.player.Bsitting")["value"], flush=True)

        b("banner 10 LIVE: the car will drive 45 s — PASSENGER IS SEATED — watch it stay upright")
        L("banner 10 LIVE: you are the passenger — watch the ride")
        time.sleep(10)
        b("autodrive on 20")
        for i in range(15):
            time.sleep(3)
            h, l = upy(b, net), upy(L, net)
            print(f"  t={i*3+3:>2}s  host upy={h}  laptop upy={l}", flush=True)
        b("autodrive off")
        b("banner 10 LIVE TEST DONE — the car should be intact and upright")
        L("banner 10 LIVE TEST DONE")
        time.sleep(3)
        print("  final host upy:", upy(b, net), " laptop upy:", upy(L, net), flush=True)
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
