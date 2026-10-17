#!/usr/bin/env python3
"""The passenger fly-up (two machines): the host drives, the laptop sits passenger. 30 s of sampling BOTH
machines' car Y + owner — catches which machine's sim diverges, when, and whether ownership flips."""
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
        b(f"carface {cx} {g['y']} {cz} {yaw}"); time.sleep(1)
        b(f"drivein {cx} {g['y']} {cz}"); time.sleep(2); b(f"drivein {cx} {g['y']} {cz}"); time.sleep(3)
        net = min(((e["net"], math.dist(e["pos"], (cx, g["y"], cz))) for e in b("mp entities")["entities"] if e["part"] < 0),
                  key=lambda t: t[1])[0]
        print("  host seated:", b("get mainscript.s.player.Bsitting")["value"])
        print("  passenger step:", L(f"mp sitpassenger {net}")); time.sleep(2)
        print("  passenger sit:", L(f"mp sitpassenger {net}")); time.sleep(2)
        print("  laptop seated:", L("get mainscript.s.player.Bsitting")["value"])
        # hold the car still: drivein releases the parking brake, and an idling car in gear rolls away with
        # both players seated (that rolling — not a sync bug — was the "fly up / weird things")
        print("  brake:", b("brakecar"))
        rows0 = next((e for e in b("mp entities")["entities"] if e["net"] == net), None)
        print("  owner after the sit (host view):", rows0["owner"] if rows0 else "?")
        print("  sampling 30 s on both machines... (hostSeated0:", b("get mainscript.s.player.Bsitting")["value"], ")")
        y_host, y_lap, owners = [], [], []
        t0 = time.time()
        while time.time() - t0 < 30:
            hb = next((e for e in b("mp entities")["entities"] if e["net"] == net), None)
            hl = next((e for e in L("mp entities")["entities"] if e["net"] == net), None)
            if hb: y_host.append((round(time.time()-t0,1), round(hb["pos"][1],2), hb["owner"]))
            if hl: y_lap.append((round(time.time()-t0,1), round(hl["pos"][1],2), hl.get("proxy"), round(hl["pos"][0],1)))
            owners.append(hb["owner"] if hb else -1)
            time.sleep(1)
        diverge = max(abs(a[1] - b2[1]) for a, b2 in zip(y_host, y_lap) if len(b2) > 1) if y_host and y_lap else -1
        flips = len(set(owners)) > 1
        ymax_h = max(y for _, y, _ in y_host); ymax_l = max(y for _, y, *_ in y_lap)
        print(f"  Y host: {y_host[:4]} ... max {ymax_h}")
        print(f"  Y laptop: {y_lap[:6]} ... max {ymax_l}")
        print("  laptop proxy trace:", [(t, px) for t, _, px in y_lap if isinstance(px, bool)][:10])
        print(f"  max |Y host - Y laptop| {diverge:.2f} m | owner flips {flips} (owners {set(owners)})")
        v = "PASS" if diverge < 1.5 and ymax_h < 12 and not flips else "FAIL — the car flies or diverges"
        print("PASSENGER-FLY VERDICT:", v)
        if b("get mainscript.s.player.Bsitting")["value"] is True: b("driveout")
        if L("get mainscript.s.player.Bsitting")["value"] is True: L("driveout")
        b("mp stop"); L("mp stop"); b("set kaposztaleves.s.settings.god False"); L("set kaposztaleves.s.settings.god False")
finally:
    L.close()
