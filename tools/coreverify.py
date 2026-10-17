#!/usr/bin/env python3
"""Two machines: the fixes of v0.57.34–39, each phase with its own verdict and the raw numbers behind it.
  1 passenger look      laptop E-presses the desktop car's front passenger seat collider → real seat, look 179°
  2 shotgun no-claim    a front passenger never takes the car (parked: nothing refuses the claim)
  3 shotgun steer       the passenger's steering reaches the owner's car; light switch toggles the owner's lights
  4 drive claim         host parks + gets out, laptop takes the driver seat → owner flips, laptop drives it away
  5 deletion both ways  laptop deletes → desktop loses it; desktop deletes → laptop loses it
  tools/coreverify.py [--no-build] [--only 1,2,...]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP

ONLY = None
if "--only" in sys.argv: ONLY = set(sys.argv[sys.argv.index("--only") + 1].split(","))
def want(n): return ONLY is None or str(n) in ONLY

def until(fn, timeout=15, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

verdicts = {}
def verdict(name, ok, detail=""):
    verdicts[name] = ok
    print(f"{name}: {'PASS' if ok else 'FAIL'}  {detail}", flush=True)

def seat_idx(side, want_driver):
    """A collider seatscript that redirects (mainseat) to the driver's / the shotgun seat — what an E-press hits."""
    seats = side("seatraw")["seats"]
    target = next(s["i"] for s in seats if s["main"] == "null" and (s["d0"] if want_driver else (s["d1"] and not s["d0"])))
    return next(s["i"] for s in seats if s["main"] == f"#{target}")

ensure_both("--no-build" not in sys.argv)
L = Agent()
b = None
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        print("host:", "server" in b("mp host 27070"))
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150):
            sys.exit(f"LAPTOP NEVER JOINED: {L('mp status')}")
        print("laptop joined", flush=True)
        for side in (b, L): side("set kaposztaleves.s.settings.god True")
        shift0 = L("get settingsscript.s.S.BAutoShift")["value"]

        rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        cx, cz = rp["x"] + F[0] * 20, rp["z"] + F[1] * 20
        g = b(f"ground {cx} {cz}")
        b(f"teleportyaw {cx + 5} {g['y'] + 1.5} {cz} 270"); time.sleep(1)
        b(f"spawnpile 2 1 {cx} {g['y']} {cz} 1 1 1.0 4242"); time.sleep(4)
        b(f"carface {cx} {g['y']} {cz} {yaw}"); time.sleep(2)
        car = min((e for e in b("mp entities")["entities"] if e["part"] < 0 and e["car"]), key=lambda e: math.dist(e["pos"], (cx, g["y"], cz)))
        net = car["net"]
        print(f"car01 net {net}, owner {car['owner']}", flush=True)
        if not until(lambda: any(e["net"] == net for e in L("mp entities")["entities"]), 20): sys.exit("laptop never got the car")
        drv = seat_idx(b, True)
        print("host sits:", b(f"sitseat {drv} redirect")); time.sleep(1.5)
        print("host seat:", b("seatlook")); b("fuelup"); b("handbrake on")

        # the laptop walks up to the car (global coordinates: floating-origin safe)
        pos = {e["net"]: e for e in b("mp entities")["entities"]}[net]["pos"]
        L(f"tpg {pos[0] + 3} {pos[2] + 3}"); time.sleep(7)
        sg = seat_idx(L, False)

        def phase(fn):
            try: fn()
            except Exception as ex: print("   PHASE ERROR:", repr(ex)[:300], flush=True)
        if want(1) or want(2) or want(3):
            print("laptop sits (shotgun collider E-press):", L(f"sitseat {sg} redirect")); time.sleep(1.5)
            L("set mainscript.s.player.FxRot 75"); time.sleep(0.6)
            lk = L("seatlook")
            verdict("1 PASSENGER LOOK", lk.get("sitting") is True and lk.get("shotgun") is True and lk.get("rotLimit") == 179 and abs(lk.get("FxRot", 0) - 75) < 1, str(lk))

            time.sleep(4)   # DriveClaimTick retries every 1.5 s — a shotgun claim would have gone through by now (car parked)
            ob, ol = ({e["net"]: e for e in side("mp entities")["entities"]}[net]["owner"] for side in (b, L))
            verdict("2 SHOTGUN NO-CLAIM", ob == 0 and ol == 0, f"owner on desktop {ob}, on laptop {ol} (want 0)")

        if want(3):
            s0 = b("seatlook")
            L("set TLDRevamp.Net.Entities.TestSteer 0.8"); time.sleep(1.5)
            s1 = b("seatlook")
            L("set TLDRevamp.Net.Entities.TestSteer 0"); time.sleep(1.5)
            s2 = b("seatlook")
            ang = s1.get("steerAngle", 0)
            steer_ok = ang > 0 and s1.get("carSteer", 0) > 0.5 * ang and abs(s2.get("carSteer", 99)) < 0.2 * ang
            lights0 = b("seatlook").get("lights")
            L("set TLDRevamp.Net.Entities.TestLights True"); time.sleep(2)
            lights1 = b("seatlook").get("lights")
            diag = {k: L(f"get {k}")["value"] for k in ("mainscript.s.player.seat.Car", "mainscript.s.player.Car", "mainscript.s.player.seat.Car.driving2",
                     "mainscript.s.player.seat.Car.isPlayerDriving2", "TLDRevamp.Net.Entities.ShotgunSent")}
            diag["desktop ShotgunApplied"] = b("get TLDRevamp.Net.Entities.ShotgunApplied")["value"]
            print("   shotgun diag:", diag)
            verdict("3 SHOTGUN STEER", steer_ok, f"owner's steer before {s0.get('carSteer')} / with passenger at 0.8 {s1.get('carSteer')} / released {s2.get('carSteer')} (angle {ang})")
            verdict("3 SHOTGUN LIGHTS", lights0 is not None and lights1 != lights0, f"owner's light switch {lights0} → {lights1}")

        if want(4):
            b("brakecar"); b("handbrake on"); b("driveout"); time.sleep(1.5)
            if L("get mainscript.s.player.Bsitting")["value"] is True: L("driveout"); time.sleep(1.5)
            p0 = {e["net"]: e for e in b("mp entities")["entities"]}[net]["pos"]
            L(f"tpg {p0[0] - 3} {p0[2] - 3}"); time.sleep(7)
            print("laptop takes the wheel:", L(f"sitseat {seat_idx(L, True)} redirect")); time.sleep(1)
            print("laptop seat:", L("seatlook"))
            for i in range(6):
                ob, ol = ({e["net"]: e for e in side("mp entities")["entities"]}[net]["owner"] for side in (b, L))
                print(f"   t={i}s owner desktop {ob} laptop {ol}; laptop sitting {L('get mainscript.s.player.Bsitting')['value']}", flush=True)
                if ob == 1 and ol == 1: break
                time.sleep(1)
            flipped = ob == 1 and ol == 1
            L("set settingsscript.s.S.BAutoShift True"); print("   handbrake:", L("handbrake off"), "fuel:", L("fuelup"))
            print("   autodrive:", L("autodrive on 12"))
            for i in range(10):
                time.sleep(1)
                print(f"   t={i+1}s laptop sitting {L('get mainscript.s.player.Bsitting')['value']} seat {L('seatlook').get('seat')} drive {str(L('autodrive'))[:160]}", flush=True)
            L("autodrive off")
            if L("get mainscript.s.player.Bsitting")["value"] is True: L("brakecar")
            p1 = {e["net"]: e for e in b("mp entities")["entities"]}[net]["pos"]
            moved = math.dist(p0, p1)
            verdict("4 DRIVE CLAIM", flipped and moved > 8, f"owner flipped to the laptop on both {flipped}; desktop saw the car move {moved:.1f} m")
            L(f"set settingsscript.s.S.BAutoShift {shift0}")
            if L("get mainscript.s.player.Bsitting")["value"] is True: L("driveout")
            if b("get mainscript.s.player.Bsitting")["value"] is True: b("driveout")

        if want(5):
            me = b("mp players")["me"]
            for who, other, label in ((L, b, "laptop deletes"), (b, L, "desktop deletes")):
                x, z = me[0] + 8, me[2] + (6 if who is L else -6)
                gy = b(f"ground {x} {z}")["y"]
                # both machines stand at the same spot in these tests' early world (no floating-origin shift yet)
                who(f"spawnpile 223 1 {x} {gy + 1} {z} 1 1 1.0 {99 if who is L else 98}"); time.sleep(3)
                ents = who("mp entities")["entities"]
                it = min((e for e in ents if e["part"] < 0 and not e.get("dormant")), key=lambda e: math.dist(e["pos"], (x, gy, z)))
                seen = until(lambda: any(e["net"] == it["net"] for e in other("mp entities")["entities"]), 15)
                print(f"   {label}: spawned net {it['net']} ({it['name']}), other side sees it {seen}; delete:", who(f"deleteshare {it['net']}"))
                gone = until(lambda: not any(e["net"] == it["net"] for e in other("mp entities")["entities"]), 15)
                verdict(f"5 DELETE ({label})", seen and gone, f"other side had it {seen}, lost it {gone}")

        for side in (b, L): side("set kaposztaleves.s.settings.god False")
        L("mp stop"); b("mp stop")
finally:
    L.close()
print("\nCORE-VERIFY:", "ALL PASS" if verdicts and all(verdicts.values()) else "FAILURES", {k: ("PASS" if v else "FAIL") for k, v in verdicts.items()})
