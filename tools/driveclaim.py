#!/usr/bin/env python3
"""Drive-claim test (the user's scenario): the host spawns a car and drives it, parks, exits;
the laptop player enters the driver seat — ownership must flip to them and the car must move
under THEIR control. Verdict: owner==1 on both machines + the car moves on the host's view."""
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

def owner(x, net):
    rows = x("mp entities")["entities"]
    car = next((e for e in rows if e["net"] == net), None)
    return (car or {}).get("owner"), (car or {}).get("pos")

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"   # real latency by default
        if RELAY != "off": b(f"net relay {RELAY}"); L(f"net relay {RELAY}")
        b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY != "off": L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150):
            print("  LAPTOP NEVER JOINED — status:", L("mp status"), flush=True)
            raise SystemExit(1)
        print("  laptop joined:", L("mp status")["client"], flush=True)
        L("set kaposztaleves.s.settings.god True"); b("set kaposztaleves.s.settings.god True")
        L("set settingsscript.s.S.BAutoShift True"); b("set settingsscript.s.S.BAutoShift True")
        rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        cx, cz = rp["x"] + F[0] * 15, rp["z"] + F[1] * 15
        g = b(f"ground {cx} {cz}")
        b(f"spawnpile 2 1 {cx} {g['y']} {cz} 1 1 1.0 4242"); time.sleep(4)
        net = min(((e["net"], math.dist(e["pos"], (cx, g["y"], cz))) for e in b("mp entities")["entities"] if e["part"] < 0),
                  key=lambda t: t[1])[0]
        print("  spawned car net", net, "owner:", owner(b, net)[0], flush=True)

        # the host drives it (autodrive), then parks and gets out
        b(f"tpg {cx} {cz + 8}")
        for _ in range(4):
            b(f"drivein {cx} {g['y']} {cz}"); time.sleep(2); b(f"drivein {cx} {g['y']} {cz}"); time.sleep(3)
            if b("get mainscript.s.player.Bsitting")["value"]: break
        print("  fuelup:", b("fuelup"), flush=True)   # seated now: fill the tank with what the engine consumes (vanilla wrong-fuel roll)
        b("autodrive on 20"); time.sleep(6); b("autodrive off")
        b("brakecar"); b("handbrake on")   # kill momentum, then the parking brake holds it
        time.sleep(3)
        p1 = owner(b, net)[1]; time.sleep(2); p2 = owner(b, net)[1]
        still = p1 and p2 and math.dist(p1, p2) < 0.5
        print(f"  car parked and still: {still}", flush=True)
        if not still: print("  INVALID: the car never stopped", flush=True)
        b("driveout"); time.sleep(2)
        hpos0 = owner(b, net)[1]
        print("  host drove and exited; car at", hpos0, flush=True)
        # park position ground for the laptop's approach
        g2 = b(f"ground {hpos0[0]} {hpos0[2]}")

        # the laptop player takes the wheel
        print("  laptop seat dump:", L(f"mp seats {net}"), flush=True)
        for attempt in range(3):
            r = L(f"mp sitdriver {net}")
            print(f"  sitdriver attempt {attempt}: {r}", flush=True)
            for beat in range(12):
                time.sleep(1)
                ok = L("get mainscript.s.player.Bsitting")["value"]
                print(f"    t={beat+1}s bsitting={ok}", flush=True)
                if ok: break
            if ok: break
        print("  laptop seated:", ok, flush=True)
        L("handbrake off"); time.sleep(1)   # release the parking brake before driving off
        print("  owners right after sit — host:", owner(b, net)[0], " laptop:", owner(L, net)[0], flush=True)

        ok_owner = until(lambda: owner(b, net)[0] == 1 and owner(L, net)[0] == 1, 10)
        print("  owner flip to laptop:", ok_owner, " host:", owner(b, net)[0], " laptop:", owner(L, net)[0], flush=True)

        # the laptop drives it — the car must move on BOTH machines
        L("autodrive on 15")
        for i in range(6):
            time.sleep(1)
            print(f"  drive t{i+1}s — laptop pos: {owner(L, net)[1]}  host pos: {owner(b, net)[1]}", flush=True)
        L("autodrive off")
        hpos1 = owner(b, net)[1]
        moved = math.dist(hpos0, hpos1) if hpos0 and hpos1 else 0
        print(f"  laptop drove: car moved {moved:.1f} m on the host's view (now {hpos1})", flush=True)
        print("  DRIVE-CLAIM VERDICT:", "PASS" if ok_owner and moved > 8 else "FAIL", flush=True)
        L("driveout"); time.sleep(1)
finally:
    try: b("driveout")
    except Exception: pass
    try: L("driveout")
    except Exception: pass
    try: b("mp stop")
    except Exception: pass
    try: L("mp stop")
    except Exception: pass
    L.close()
