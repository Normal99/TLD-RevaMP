#!/usr/bin/env python3
"""B9 passenger of a moving car (two machines): the host drives, the laptop rides passenger. The passenger must
STAY seated while the car moves and their body must follow it on the host's machine — at 0 and 180 ms."""
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
        RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"   # real latency by default
        if RELAY != "off": b(f"net relay {RELAY}"); L(f"net relay {RELAY}")
        b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        L("set TLDRevamp.Net.Mp.RemoteTrace True")   # the lifecycle traces on the laptop
        b("set TLDRevamp.Net.Mp.RemoteTrace True")   # and on the host: the id-0 relay lines
        if RELAY != "off": L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): raise SystemExit("LAPTOP NEVER JOINED")
        L("set kaposztaleves.s.settings.god True"); b("set kaposztaleves.s.settings.god True")
        rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        cx, cz = rp["x"] + F[0] * 15, rp["z"] + F[1] * 15
        g = b(f"ground {cx} {cz}")
        b(f"spawnpile 223 1 {cx} {g['y']} {cz} 1 1 1.0 4242"); time.sleep(4)
        b(f"carface {cx} {g['y']} {cz} {yaw}"); time.sleep(1)
        b(f"drivein {cx} {g['y']} {cz}"); time.sleep(2); b(f"drivein {cx} {g['y']} {cz}"); time.sleep(3)
        net = min(((e["net"], math.dist(e["pos"], (cx, g["y"], cz))) for e in b("mp entities")["entities"] if e["part"] < 0),
                  key=lambda t: t[1])[0]
        print("  host seated:", b("get mainscript.s.player.Bsitting")["value"])
        print("  passenger step:", L(f"mp sitpassenger {net}")); time.sleep(2)
        print("  passenger sit:", L(f"mp sitpassenger {net}")); time.sleep(2)
        print("  laptop seated:", L("get mainscript.s.player.Bsitting")["value"])

        def ride_check(seconds, tag):
            worst = 0.0; seated_all = True; bad = []
            t0 = time.time()
            b("autodrive on 20")
            while time.time() - t0 < seconds:
                pl = L("mp players")["players"]
                seat = next((p for p in pl if p["id"] == 0), None)   # the laptop's view of the HOST (its own id is never in its own remote list)
                if not seat or not seat["seated"] or seat["seatNet"] != net:
                    seated_all = False
                    if len(bad) < 4:
                        bad.append({"t": round(time.time()-t0,1), "seated": seat and seat["seated"], "seatNet": seat and seat["seatNet"], "rawLaptopRows": pl})
                hb = b("mp players")["players"]
                hseat = next((p for p in hb if p["id"] == 1), None)
                car = next((e for e in b("mp entities")["entities"] if e["net"] == net), None)
                if hseat and car and hseat["has"]:
                    worst = max(worst, math.dist(hseat["shown"], car["pos"]))
                time.sleep(1)
            b("autodrive off"); time.sleep(2)
            print(f"  {tag}: seated the whole ride {seated_all}, worst body-to-car distance on the host {worst:.2f} m")
            if bad: print(f"  {tag} failing samples: {bad}")
            return seated_all and worst < 6.0

        ok0 = ride_check(18, "0 ms")
        L("net lag 180 2 10 40")
        ok180 = ride_check(18, "180 ms")
        L("net lag 0")
        print("B9 VERDICT:", "PASS" if ok0 and ok180 else "FAIL")
        L("driveout"); b("driveout")
        b("mp stop"); L("mp stop"); b("set kaposztaleves.s.settings.god False"); L("set kaposztaleves.s.settings.god False")
finally:
    L.close()
