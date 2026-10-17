#!/usr/bin/env python3
"""Foreground probe of the sit phase: timestamps around EVERY call, nothing filtered. The
harness's Agent/read plumbing is under suspicion — this makes each step's timing and result
explicit so the exact silent-failure point is visible."""
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

def ts(msg):
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)

def T(x, cmd):
    ts(f"-> {x.__name__ if hasattr(x,'__name__') else 'bridge'}: {cmd}")
    t0 = time.time()
    r = x(cmd)
    ts(f"<- {time.time()-t0:.2f}s: {str(r)[:180]}")
    return r

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        ts("session up")
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        T(L, f"mp join {DESKTOP_IP}:27070")   # join first: before it, the client is null by design
        until(lambda: L("mp status")["client"]["ready"], 150)
        ts("joined")
        L("set kaposztaleves.s.settings.god True"); b("set kaposztaleves.s.settings.god True")
        L("set settingsscript.s.S.BAutoShift True"); b("set settingsscript.s.S.BAutoShift True")
        rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        cx, cz = rp["x"] + F[0] * 15, rp["z"] + F[1] * 15
        g = b(f"ground {cx} {cz}")
        b(f"spawnpile 2 1 {cx} {g['y']} {cz} 1 1 1.0 4242"); time.sleep(4)
        net = min(((e["net"], math.dist(e["pos"], (cx, g["y"], cz))) for e in b("mp entities")["entities"] if e["part"] < 0),
                  key=lambda t: t[1])[0]
        ts(f"car net {net}")
        b(f"tpg {cx} {cz + 8}")
        for _ in range(4):
            b(f"drivein {cx} {g['y']} {cz}"); time.sleep(2); b(f"drivein {cx} {g['y']} {cz}"); time.sleep(3)
            if b("get mainscript.s.player.Bsitting")["value"]: break
        b("fuelup"); b("autodrive on 20"); time.sleep(6); b("autodrive off")
        b("brakecar"); b("handbrake on"); time.sleep(3)
        ts(f"host parked: {b('mp entities')['entities'] and 'ok'}")
        b("driveout"); time.sleep(2)

        for attempt in range(3):
            T(L, f"mp sitdriver {net}")
            for beat in range(12):
                time.sleep(1)
                r = T(L, "get mainscript.s.player.Bsitting")
                if "value" not in r: ts(f"!! no value key: {r}")
                ok = r.get("value")
                if ok: break
            if ok: break
        ts(f"seated={ok}")
        if ok:
            T(L, "handbrake off")
            ts(f"owner host: {next((e for e in b('mp entities')['entities'] if e['net']==net), {}).get('owner')}")
            T(L, "autodrive on 15")
            for i in range(6):
                time.sleep(1)
                lp = next((e for e in L("mp entities")["entities"] if e["net"]==net), None)
                hp = next((e for e in b("mp entities")["entities"] if e["net"]==net), None)
                ts(f"drive t{i+1}s laptop={lp and lp.get('pos')} host={hp and hp.get('pos')} owner={hp and hp.get('owner')}")
            T(L, "autodrive off")
finally:
    for closer, cmds in ((b, ("autodrive off", "driveout", "mp stop")), (L, ("autodrive off", "driveout", "mp stop"))):
        for c in cmds:
            try: closer(c)
            except Exception as e: ts(f"teardown {c}: {e}")
    L.close()
