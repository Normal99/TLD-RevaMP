#!/usr/bin/env python3
"""How the save grows with the world a server has seen (the "region save format for big worlds" question,
architecture §8): one machine hosts a seeded world alone and travels along the road in --step km hops, letting each
stop's places load. Every --every stops: `mp save` (the session's synchronous save: its time is the hitch a server
has), the save file's size, the game's saved items, the far store's records. Prints a table and the growth per 100 km.
A measurement (desktop only); saves are backed up and restored (ProtectedSession).
  tools/bigworld.py [--no-build] [--km 200] [--step 2] [--every 10]"""
import sys, time, math, json, pathlib, glob, os
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from roadkit import tp
from mplib import ensure_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
KM, STEP, EVERY = arg("--km", 200.0, float), arg("--step", 2.0, float), arg("--every", 10, int)

def save_file():
    p = game.SAVES / "TLDsaveData_autosave1.tlds"
    return p.stat().st_size if p.exists() else None

ensure_both("--no-build" not in sys.argv)
rows = []
with game.ProtectedSession():
    seeded.restart_same_seed(); b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True"); b("set kaposztaleves.s.settings.god True")
    b("bg on"); b("set UnityEngine.QualitySettings.vSyncCount 0")   # full speed unfocused (mplib.seeded_both)
    print("  hosting alone:", str(b("mp host 27070"))[:120], flush=True); time.sleep(5)
    rp = b("roadpoint"); x, z = rp["gx"], rp["gz"]; last = rp; heading = (0.0, 0.0)
    stops = int(KM / STEP)
    t0 = time.time()
    for i in range(stops + 1):
        if i % EVERY == 0:
            sv = b("mp save"); time.sleep(1)
            row = {"km": round(i * STEP, 1), "saveMs": sv.get("ms"), "items": sv.get("items"), "bytes": save_file(),
                   "objects": len(b("mp entities")["entities"]), "minutes": round((time.time() - t0) / 60, 1)}
            rows.append(row); print("  " + json.dumps(row), flush=True)
        if i == stops: break
        # along the road: hop, then the road's point and heading there (run 1: a straight 2 km hop left the bending road,
        # "no road near"); a hop that lands away from it is retried shorter from the last road point
        try: rp = b("roadpoint"); last = rp
        except RuntimeError: rp = last
        f = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        if i > 0 and (f[0] * heading[0] + f[1] * heading[1]) < 0: f = (-f[0], -f[1])   # on the way we were going
        heading = f
        for hop in (STEP * 1000, STEP * 500, STEP * 250):
            tp(b, rp["gx"] + f[0] * hop, rp["gz"] + f[1] * hop, timeout=60); time.sleep(6)
            try: b("roadpoint"); break
            except RuntimeError: print(f"    no road {hop:.0f} m on; shorter", flush=True)
    b("mp stop")
print("\nBIGWORLD")
print(json.dumps(rows, indent=1))
if len(rows) >= 2 and rows[-1]["km"] > 0:
    a, z_ = rows[0], rows[-1]; per = 100.0 / (z_["km"] - a["km"])
    print(f"per 100 km: save +{((z_['bytes'] or 0) - (a['bytes'] or 0)) * per / 1e6:.2f} MB, +{(z_['saveMs'] - a['saveMs']) * per:.0f} ms, +{(z_['items'] - a['items']) * per:.0f} items")
