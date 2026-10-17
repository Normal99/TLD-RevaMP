#!/usr/bin/env python3
"""Contact impulses (two machines): the desktop's car is parked (host-owned, simulated on the desktop); the
laptop spawns a car behind it on the road and drives into it. The parked car must REACT on the desktop
(impulse applied to the real body) — before this fix it stayed frozen (a kinematic proxy blocks, never moves).
OBSOLETE since v0.57.59: the impulse relay is off (ContactRelayOn; it threw cars) — car contact is crashtest.py's.
It FAILs with contactSent 0 by design now (checked 2026-10-08)."""
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
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 150)
        L("set kaposztaleves.s.settings.god True"); b("set kaposztaleves.s.settings.god True")
        print("joined")

        rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        # the desktop's car parked on the road (host-owned → simulated on the desktop)
        ax, az = rp["x"] + F[0] * 60, rp["z"] + F[1] * 60
        ga = b(f"ground {ax} {az}")
        b(f"spawnpile 223 1 {ax} {ga['y']} {az} 1 1 1.0 4242"); time.sleep(4)
        b(f"carface {ax} {ga['y']} {az} {yaw}"); time.sleep(2)
        anet = min(((e["net"], math.dist(e["pos"], (ax, ga["y"], az))) for e in b("mp entities")["entities"] if e["part"] < 0),
                   key=lambda t: t[1])[0]
        def carpos(who, net):
            for e in who("mp entities")["entities"]:
                if e["net"] == net: return e["pos"]
            return None
        abefore = carpos(b, anet)
        # the laptop's car 150 m back, same lane
        bx, bz = rp["x"] + F[0] * (-90), rp["z"] + F[1] * (-90)
        gb = L(f"ground {bx} {bz}")
        L(f"spawnpile 223 1 {bx} {gb['y']} {bz} 1 1 1.0 4243"); time.sleep(4)
        L(f"carface {bx} {gb['y']} {bz} {yaw}"); time.sleep(1)
        L(f"drivein {bx} {gb['y']} {bz}"); time.sleep(2); L(f"drivein {bx} {gb['y']} {bz}"); time.sleep(3)
        L("set TLDRevamp.DriveLab.AvoidLook 0")   # the driver must not swerve around the target
        print("  laptop driving at the parked car...")
        L("autodrive on 25")
        moved = False
        t0 = time.time()
        while time.time() - t0 < 45:
            ab = carpos(b, anet)
            if ab and math.dist([float(v) for v in ab], [float(v) for v in abefore]) > 0.8: moved = True; break
            st = L("autodrive")
            if st.get("stuckSeconds", 0) > 6: L("nudge 12")
            time.sleep(1)
        L("autodrive off")
        time.sleep(3)
        aafter = carpos(b, anet)
        d = math.dist([float(v) for v in abefore], [float(v) for v in aafter]) if aafter and abefore else -1
        lb, db = L("mp entities"), b("mp entities")
        print(f"  parked car moved on the desktop: {d:.2f} m | laptop contactSent {lb.get('contactSent')} | desktop relayed {db.get('contactRelayed')} applied {db.get('contactApplied')}")
        le = {e["net"]: e for e in L("mp entities")["entities"]}
        de = {e["net"]: e for e in b("mp entities")["entities"]}
        if anet in le: print("  laptop still has the parked car:", le[anet].get("dormant"), le[anet]["pos"][:2])
        else: print("  laptop: the parked car entity is GONE")
        if anet in de: print("  desktop still has it:", de[anet].get("dormant"), de[anet]["pos"][:2])
        else: print("  desktop: the parked car entity is GONE")
        print("  laptop partRejects:", L("mp entities").get("partRejects", "")[:120])
        print("  laptop cars:", len([e for e in le.values() if e["part"] < 0]), "| desktop cars:", len([e for e in de.values() if e["part"] < 0]))
        bdist = None
        lc = carpos(L, L("mp entities")["entities"][0]["net"]) if L("mp entities")["entities"] else None
        print("CONTACT VERDICT:", "PASS — the parked car reacted to the other player's crash" if moved else "FAIL — frozen proxy")
        L("driveout"); b("mp stop"); L("mp stop")
finally:
    L.close()
