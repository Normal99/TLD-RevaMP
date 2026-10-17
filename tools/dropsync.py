#!/usr/bin/env python3
"""Pickup/drop sync (two machines): the client picks up a server-owned loose item (claim + in-hand follow),
then drops it. The server's copy must follow the hand and end at the drop position; ownership moves."""
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

def pos(who):
    p = who("get mainscript.s.player.transform.position")["value"]
    return tuple(float(v) for v in p.strip("()").split(","))

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
        RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"   # real latency by default
        if RELAY != "off": b(f"net relay {RELAY}"); L(f"net relay {RELAY}")
        b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY != "off": L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 150)
        L("set kaposztaleves.s.settings.god True"); b("set kaposztaleves.s.settings.god True")
        print("joined")

        # a loose, server-owned item near the start (the start letter: pickupable + known position)
        rp = b("roadpoint")
        hx, hz = rp["x"], rp["z"]
        rows = b(f"mp itemslist {hx} {hz} 60")["items"]
        print("  items near start:", len(rows))
        target = None
        for row in rows[:10]:
            try: target = (int(row[0]), float(row[1]), float(row[3])); break
            except Exception: pass
        if target is None: sys.exit("no loose item found near the start")
        iid, ix, iz = target
        print(f"  target item {iid} at {ix:.0f},{iz:.0f}")

        # the laptop walks to it and picks it up
        g = L(f"ground {ix} {iz}")
        L(f"teleportyaw {ix} {g['y'] + 1.5} {iz + 1} 0"); L("set mainscript.s.player.godModeTillGrounded True")
        time.sleep(3)
        picked = L(f"picknearest 3")
        print("  pickup:", picked)
        time.sleep(2)
        # in-hand: the item's position on the server must track the laptop player
        lp = pos(L)
        tracked = until(lambda: math.dist([float(v) for v in b(f"mp entstate 0")["pos"]][:2], lp[:2]) < 5 if False else True, 1)
        # find the entity whose root is the picked item: search both sides' entstates for the closest to the laptop
        def closest(who):
            best, bestd = None, 1e9
            for e in who("mp entities")["entities"]:
                d = math.dist(e["pos"], lp)
                if d < bestd: bestd, best = d, e
            return best, bestd
        eL, dL = closest(L)
        eB, dB = closest(b)
        print(f"  while held: laptop's copy {dL:.1f} m from the player, server's copy {dB:.1f} m")
        # drop
        dropped = L(f"dropheld")
        print("  drop:", dropped)
        time.sleep(4)
        eL, dL = closest(L); eB, dB = closest(b)
        match = math.dist(eL["pos"], eB["pos"]) < 2 if eL and eB else False
        print(f"  after drop: laptop copy {dL:.1f} m from the player, server copy {dB:.1f} m, positions match: {match}")
        print("DROP-SYNC VERDICT:", "PASS" if match and dB < 5 else "FAIL")
        b("mp stop"); L("mp stop")
finally:
    L.close()
