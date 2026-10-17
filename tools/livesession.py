#!/usr/bin/env python3
"""Hand the controller over: desktop hosts the test world, the laptop joins, a car01 (right fuel in the tank) is parked
next to both players, banners explain what to try. The session is left running for the player (no mp stop).
  --relay <dc>: both machines relay-only through Valve datacenter <dc> (e.g. iad, a far one: real-world latency), friend join.
Saves: autosave blocked in the running games; the player's autosave slots are backed up and restored by ProtectedSession."""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP

def until(fn, timeout=15, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else None   # real internet via one Valve relay datacenter
ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        for side in (b, L): side("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("relay only via", RELAY, ":", b(f"net relay {RELAY}"), L(f"net relay {RELAY}"))
        b("mp host 27070")
        if RELAY: L(f"mp joinid {b('net me')['steamId']}")   # the MP panel's friend join (Steam P2P)
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("laptop never joined: " + str(L("mp status")))
        route = None
        if RELAY:
            route = L("net route"); print("route:", {k: route.get(k) for k in ("ping", "relayed", "popRelay", "popRemote")})
            if not route.get("relayed"): sys.exit("NOT RELAYED: " + str(route))
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        cx, cz = rp["x"] + F[0] * 20, rp["z"] + F[1] * 20
        g = b(f"ground {cx} {cz}")
        b(f"teleportyaw {cx + 6} {g['y'] + 1.5} {cz} 270"); time.sleep(1)
        b(f"spawnpile 2 1 {cx} {g['y']} {cz} 1 1 1.0 4242"); time.sleep(4); b(f"carface {cx} {g['y']} {cz} {yaw}"); time.sleep(2)
        net = min((e for e in b("mp entities")["entities"] if e["part"] < 0 and e["car"]), key=lambda e: math.dist(e["pos"], (cx, g["y"], cz)))["net"]
        # fuel it the way its engine drinks (the spawner can roll a diesel engine with petrol): sit, fuel, handbrake, out
        for _ in range(3):
            b(f"sitseat 5 redirect"); time.sleep(1)
            if b("get mainscript.s.player.Bsitting")["value"] is True: break
        print("fuel:", b("fuelup"), b("handbrake on")); b("driveout"); time.sleep(1)
        b(f"teleportyaw {cx + 6} {g['y'] + 1.5} {cz} 270")
        until(lambda: any(e["net"] == net for e in L("mp entities")["entities"]), 20)
        L(f"tpg {cx - 6} {cz}"); time.sleep(6)
        for side in (b, L):
            side("banner 25 " + (f"LAGGY LIVE SESSION — real internet via Steam relay ({RELAY}), ~{route['ping']} ms ping.\\n" if RELAY else "") +
                 "LIVE SESSION — car01 is parked between you (handbrake on, fuelled).\\n"
                 "Try: desktop drives, laptop sits front passenger → look around, grab the wheel (steer keys), lights.\\n"
                 "Then desktop parks + gets out, laptop takes the driver seat and drives off.")
        print(f"live: car net {net}; desktop {b('mp status')['server']['ready']} ready peer(s); laptop client id {L('mp status')['client']['id']}")
finally:
    L.close()
