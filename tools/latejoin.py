#!/usr/bin/env python3
"""Two machines: the desktop hosts, shares a car and changes its state (fuel, a part, a knob); only then does the
laptop join. Its copy must show the current state, not the state at share time."""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP

def until(fn, timeout=15, step=0.25):
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
        b("set TLDRevamp.TestSafety.BlockAutoSave True"); b("set kaposztaleves.s.settings.god True")
        RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"   # real latency by default
        if RELAY != "off": b(f"net relay {RELAY}"); L(f"net relay {RELAY}")
        b("mp host 27070")
        rp = b("roadpoint"); x, z = rp["x"], rp["z"]; g = b(f"ground {x} {z}")
        b(f"teleportyaw {x} {g['y'] + 1.5} {z + 4} 0")
        b(f"spawnpile 2 1 {x} {g['y']} {z} 1 1 1.0 4242"); time.sleep(4)
        b(f"carface {x} {g['y']} {z} {rp['yaw'] + 180}"); time.sleep(2)
        b(f"drivein {x} {g['y']} {z}"); time.sleep(2); b(f"drivein {x} {g['y']} {z}"); time.sleep(2); b("driveout")
        net = max(e["net"] for e in b("mp entities")["entities"] if e["part"] < 0)
        print("poke:", b(f"mp entpoke {net} fuel 1.25"), b(f"mp entpoke {net} part 2 3"), b(f"mp entpoke {net} usable 0 1"))
        time.sleep(4)
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY != "off": L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 120)
        L("set TLDRevamp.TestSafety.BlockAutoSave True"); L("set kaposztaleves.s.settings.god True")
        L(f"teleportyaw {x} {g['y'] + 1.5} {z - 6} 0")
        ok = until(lambda: "error" not in L(f"mp entstate {net}"), 30)
        time.sleep(2)
        a, c = b(f"mp entstate {net}"), L(f"mp entstate {net}")
        same = all(a[k] == c[k] for k in ("tanks", "parts", "usables", "doors"))
        print(f"late joiner has the car: {ok}; same state as the host: {same}")
        for k in ("tanks", "parts", "usables", "doors"):
            if a[k] != c[k]: print(f"  {k}\n   host   {a[k][:300]}\n   joiner {c[k][:300]}")
        print("joiner resync:", L("mp entities")["resync"])
        L("mp stop"); b("mp stop"); b("set kaposztaleves.s.settings.god False"); L("set kaposztaleves.s.settings.god False")
finally:
    L.close()
