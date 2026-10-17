#!/usr/bin/env python3
"""Watchable core demo (the user watches both screens): spawn-without-sitting, deletion sync,
passenger seating + a ride together. Each step is captioned and measured on the spot."""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
from mplib import Agent, DESKTOP_IP

def until(fn, timeout=25, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

b = Bridge()
if b("mp status").get("server") is None:
    print("hosting a fresh session on the current world...")
    b("mp host 27070")
L = Agent()
try:
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    L(f"mp join {DESKTOP_IP}:27070")
    until(lambda: L("mp status")["client"]["ready"], 150)
    b("set kaposztaleves.s.settings.god True"); L("set kaposztaleves.s.settings.god True")
    def banner(txt, secs=9): b(f"banner {secs} {txt}"); L(f"banner {secs} {txt}")
    def hostpos():
        p = b("get mainscript.s.player.transform.position")["value"]
        return [float(v) for v in p.strip("()").split(",")]
    def hostyaw():
        p = b("get mainscript.s.player.transform.rotation")["value"]
        import re as _re
        m = _re.search(r"\(([-0-9.e]+),", p)
        return float(m.group(1)) if m else 0.0
    def clear_host(fx, fz):
        # the host steps 12 m aside so a spawn ahead of them can't crush them
        px, py, pz = hostpos()
        rx, rz = fz, -fx   # right of the host's facing
        b(f"teleportyaw {px + rx * 12} {py + 2} {pz + rz * 12} 0")
        b("set mainscript.s.player.godModeTillGrounded True")
        time.sleep(2)

    # ---- 1 spawn visibility
    banner("1/3 SPAWN: the LAPTOP spawns a car 20 m ahead of you - it must appear with NOBODY sitting in it", 10)
    px, py, pz = hostpos()
    fy = hostyaw(); fx, fz = math.sin(math.radians(fy)), math.cos(math.radians(fy))
    clear_host(fx, fz)
    cx, cz = px + fx * 20, pz + fz * 20
    g = L(f"ground {cx} {cz}")
    L(f"spawnpile 223 1 {cx} {g['y']} {cz} 1 1 1.0 4242"); time.sleep(4)
    seen = until(lambda: any(e["part"] < 0 and math.dist(e["pos"], (cx, g["y"], cz)) < 10 for e in b("mp entities")["entities"]), 12)
    net = min(((e["net"], math.dist(e["pos"], (cx, g["y"], cz))) for e in b("mp entities")["entities"] if e["part"] < 0),
              key=lambda t: t[1])[0]
    banner(f"1/3 RESULT: {'the car appeared for you - spawn visibility OK' if seen else 'FAILED - the car did not appear'}", 7)
    print("1 spawn visibility:", "PASS" if seen else "FAIL")

    # ---- 2 deletion
    banner("2/3 DELETE: the LAPTOP deletes that car - watch it vanish from your world too", 9)
    L(f"deleteshare {net}"); time.sleep(2.5)
    gone = not any(e["net"] == net for e in b("mp entities")["entities"])
    banner(f"2/3 RESULT: {'the car is gone from your world - deletion sync OK' if gone else 'FAILED - it is still there'}", 7)
    print("2 deletion sync:", "PASS" if gone else "FAIL")

    # ---- 3 passenger + ride
    banner("3/3 PASSENGER: the LAPTOP takes your passenger seat - the car must hold still, owner unchanged", 10)
    px, py, pz = hostpos()
    fy = hostyaw(); fx, fz = math.sin(math.radians(fy)), math.cos(math.radians(fy))
    clear_host(fx, fz)
    gx, gz = px + fx * 25, pz + fz * 25
    g2 = b(f"ground {gx} {gz}")
    b(f"spawnpile 223 1 {gx} {g2['y']} {gz} 1 1 1.0 4243"); time.sleep(4)
    b(f"carface {gx} {g2['y']} {gz} {yaw}"); time.sleep(1)
    b(f"drivein {gx} {g2['y']} {gz}"); time.sleep(2); b(f"drivein {gx} {g2['y']} {gz}"); time.sleep(3)
    net2 = min(((e["net"], math.dist(e["pos"], (gx, g2["y"], gz))) for e in b("mp entities")["entities"] if e["part"] < 0),
               key=lambda t: t[1])[0]
    b("brakecar")
    print("  passenger step:", L(f"mp sitpassenger {net2}")); time.sleep(2)
    print("  passenger sit:", L(f"mp sitpassenger {net2}")); time.sleep(2)
    seated_ok = until(lambda: next((p for p in b("mp players")["players"] if p["id"] == 1), None) is not None
                      and next((p for p in b("mp players")["players"] if p["id"] == 1), None)["seated"]
                      and next((p for p in b("mp players")["players"] if p["id"] == 1), None)["seatNet"] == net2, 6)
    own_ok = until(lambda: next((e for e in b("mp entities")["entities"] if e["net"] == net2), None) is not None
                   and next((e for e in b("mp entities")["entities"] if e["net"] == net2), None)["owner"] == 0, 4)
    banner(f"3/3 RESULT: {'the passenger is in your car - owner unchanged, no flip' if seated_ok and own_ok else 'FAILED'}", 8)
    print("3 passenger seating:", "PASS" if seated_ok and own_ok else "FAIL")

    # ---- 4 the ride
    banner("RIDE: the car drives 15 s - the passenger must stay seated and their body must follow", 8)
    b("autodrive on 20")
    worst = 0.0; seated_all = True; t0 = time.time()
    while time.time() - t0 < 15:
        hs = next((p for p in L("mp players")["players"] if p["id"] == 0), None)
        if not hs or not hs["seated"] or hs["seatNet"] != net2: seated_all = False
        hh = next((p for p in b("mp players")["players"] if p["id"] == 1), None)
        car = next((e for e in b("mp entities")["entities"] if e["net"] == net2), None)
        if hh and hh["has"] and car: worst = max(worst, math.dist(hh["shown"], car["pos"]))
        time.sleep(1)
    b("autodrive off"); time.sleep(2)
    ride_ok = seated_all and worst < 6.0
    banner(f"RIDE RESULT: {'the passenger stayed seated and their body followed - max drift ' + format(worst, '.2f') + ' m' if ride_ok else 'FAILED'}", 9)
    print(f"4 ride: seated throughout {seated_all}, worst drift {worst:.2f} m:", "PASS" if ride_ok else "FAIL")
    if b("get mainscript.s.player.Bsitting")["value"] is True: b("driveout")
    banner("CORE DEMO COMPLETE" if all([seen, gone, seated_ok, own_ok, ride_ok]) else "CORE DEMO - a step failed, see the console", 12)
    b("mp stop"); L("mp stop"); b("set kaposztaleves.s.settings.god False"); L("set kaposztaleves.s.settings.god False")
finally:
    L.close()
