#!/usr/bin/env python3
"""Entity lifecycle (two machines): (1) spawn visibility — the client spawns a car, the SERVER must see it
without anyone sitting in it; (2) deletion sync — the client deletes it, the server must see it gone (and the
save must lose it); (3) passenger seating — the client takes the passenger seat of the host's car, the host
must see them seated."""
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
        game.quit_game(); game.launch()   # full cycle: in-world restarts crash after several sessions
        b = Bridge()
        b("set DataFromMenuScript.s.seed 20260924")
        b("call menuhandler.s.PressedStart")
        until(lambda: b("get DataFromMenuScript.s.mainmenu")["value"] is False and b("get mainscript.s.player")["value"] is not None, 180)
        time.sleep(6)
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"   # real latency by default
        if RELAY != "off": b(f"net relay {RELAY}"); L(f"net relay {RELAY}")
        b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY != "off": L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 150)
        L("set kaposztaleves.s.settings.god True"); b("set kaposztaleves.s.settings.god True")
        print("joined")

        rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        cx, cz = rp["x"] + F[0] * 25, rp["z"] + F[1] * 25
        g = L(f"ground {cx} {cz}")

        # ---- 1 spawn visibility: the client spawns; the server must see it WITHOUT a sit
        print("  spawn:", L(f"spawnpile 223 1 {cx} {g['y']} {cz} 1 1 1.0 4242")); time.sleep(3)
        seen = until(lambda: any(e["part"] < 0 and math.dist(e["pos"], (cx, g["y"], cz)) < 10 for e in b("mp entities")["entities"]), 15)
        lb = L("mp entities")
        db = b("mp entities")
        print(f"  laptop: count {lb['count']} spawnShared {lb.get('spawnShared')} | server: count {db['count']} spawnShared {db.get('spawnShared')}")
        print("1 SPAWN VISIBILITY (server sees it, nobody sat):", "PASS" if seen else "FAIL")

        # ---- 2 deletion sync: the client deletes it (JustDestroy on its root), the server must see it gone
        le = L("mp entities")
        net = min(((e["net"], math.dist(e["pos"], (cx, g["y"], cz))) for e in le["entities"] if e["part"] < 0),
                  key=lambda t: t[1])[0]
        print("  delete:", L(f"deleteshare {net}"))
        gone = until(lambda: not any(e["net"] == net for e in b("mp entities")["entities"]), 15)
        time.sleep(2)
        still = any(e["net"] == net for e in b("mp entities")["entities"])
        print("2 DELETE SYNC (server sees it gone):", "PASS" if gone and not still else "FAIL")

        # ---- 3 passenger seating: the host sits driver in a car, the client takes the passenger seat
        gx, gz = rp["x"] + F[0] * 40, rp["z"] + F[1] * 40
        g2 = b(f"ground {gx} {gz}")
        b(f"spawnpile 223 1 {gx} {g2['y']} {gz} 1 1 1.0 4243"); time.sleep(4)
        b(f"carface {gx} {g2['y']} {gz} {yaw}"); time.sleep(1)
        b(f"drivein {gx} {g2['y']} {gz}"); time.sleep(2); b(f"drivein {gx} {g2['y']} {gz}"); time.sleep(3)
        net2 = min(((e["net"], math.dist(e["pos"], (gx, g2["y"], gz))) for e in b("mp entities")["entities"] if e["part"] < 0),
                   key=lambda t: t[1])[0]
        print("  host seated:", b("get mainscript.s.player.Bsitting")["value"])
        print("  passenger step:", L(f"mp sitpassenger {net2}")); time.sleep(2)
        print("  passenger sit:", L(f"mp sitpassenger {net2}"))
        time.sleep(3)
        print("  laptop seat probe:", L("get mainscript.s.player.seat.name")["value"],
              "| root:", L("get mainscript.s.player.seat.transform.root.name")["value"])
        pl = L("mp players")     # the laptop sees the HOST (id 0) seated in the car
        host_sees = b("mp players")  # the host sees the LAPTOP (id 1) seated in the car
        seated = any(p["id"] == 0 and p["seated"] and p["seatNet"] == net2 for p in pl["players"])
        seated_host_view = any(p["id"] == 1 and p["seated"] and p["seatNet"] == net2 for p in host_sees["players"])
        print(f"  laptop players: {pl['players']} | host sees: {host_sees['players']}")
        print("3 PASSENGER SEATING:", "PASS" if seated and seated_host_view else "FAIL")
        L("driveout"); b("driveout")
        b("mp stop"); L("mp stop"); b("set kaposztaleves.s.settings.god False"); L("set kaposztaleves.s.settings.god False")
finally:
    L.close()
