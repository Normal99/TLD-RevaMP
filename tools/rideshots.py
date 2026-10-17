#!/usr/bin/env python3
"""Capture the visible defect: frames from BOTH screens during a passenger ride. Same setup as
passenger.py, then autodrive + `shot` bursts on both machines; frames land in bench/shots/."""
import sys, time, math, pathlib, subprocess, shutil
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded, tldenv
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP

SHOTS = pathlib.Path(__file__).resolve().parent.parent / "bench" / "shots"
GAME = tldenv.GAME_DIR

def until(fn, timeout=25, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def grab(machine, n):
    """Pull mpshot_<n>.png from a machine into bench/shots/."""
    out = SHOTS / f"{machine}_{n:03d}.png"
    if machine == "host":
        src = GAME / "TheLongDrive_Data" / f"mpshot_{n}.png"
        if src.exists(): shutil.move(str(src), str(out))
    else:
        r = subprocess.run(tldenv.ssh_args() + [
                            f"cat {tldenv.remote_quote(tldenv.REMOTE_GAME + f'/TheLongDrive_Data/mpshot_{n}.png')}"],
                           capture_output=True, timeout=30)
        if r.returncode == 0 and len(r.stdout) > 1000: out.write_bytes(r.stdout)
    return out.exists()

ensure_both("--no-build" not in sys.argv)
L = Agent()
SHOTS.mkdir(exist_ok=True)
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
        net = min(((e["net"], math.dist(e["pos"], (cx, g["y"], cz))) for e in b("mp entities")["entities"] if e["part"] < 0),
                  key=lambda t: t[1])[0]
        b(f"tpg {cx} {cz + 8}")
        for _ in range(4):
            b(f"drivein {cx} {g['y']} {cz}"); time.sleep(2); b(f"drivein {cx} {g['y']} {cz}"); time.sleep(3)
            if b("get mainscript.s.player.Bsitting")["value"]: break
        print("  host seated:", b("get mainscript.s.player.Bsitting")["value"])
        for _ in range(4):   # walk-then-sit twice; retry until the laptop's player is really in
            L(f"mp sitpassenger {net}"); time.sleep(2)
            L(f"mp sitpassenger {net}"); time.sleep(2)
            if L("get mainscript.s.player.Bsitting")["value"]: break
        print("  laptop seated:", L("get mainscript.s.player.Bsitting")["value"])

        n = 0; t0 = time.time()
        b("autodrive on 20")
        while time.time() - t0 < 14:
            n += 1
            b(f"shot {n}"); L(f"shot {n}")
            time.sleep(0.7)
            grab("host", n); grab("laptop", n)
        b("autodrive off"); time.sleep(2)
        print(f"  frames: {n} per machine -> {SHOTS}")
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
