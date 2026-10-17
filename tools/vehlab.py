#!/usr/bin/env python3
"""Remote-car display test (Net/VehicleLab): drive the test car on the main road and score the network ghost at several
simulated network conditions. Each condition: fresh `vehlab start`, 30 s window.
  tools/vehlab.py [label] [--conds "0|180 2 10 40|300 5 20 80"] [--window 30]
"""
import sys, time, math, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge

def setup(b):
    rp = b("roadpoint")
    heading = rp["yaw"] + 180.0
    yaw = math.radians(heading)
    x, z = rp["x"] + 6 * math.sin(yaw), rp["z"] + 6 * math.cos(yaw)
    g = b(f"ground {x} {z}")
    b(f"spawnpile 2 1 {x} {g['y']} {z} 1 1 1.0 4242"); time.sleep(4)
    b(f"carface {x} {g['y']} {z} {heading}"); time.sleep(2)
    b(f"drivein {x} {g['y']} {z}"); time.sleep(2)
    b(f"drivein {x} {g['y']} {z}"); time.sleep(2)
    b("autodrive on 25"); time.sleep(10)

RATE = []

def main():
    label, conds, window = "vehlab", ["0", "180 2 10 40", "300 5 20 80"], 30
    it = iter(sys.argv[1:])
    for a in it:
        if a == "--conds": conds = next(it).split("|")
        elif a == "--window": window = int(next(it))
        elif a == "--rate": RATE.append(next(it))
        else: label = a
    res = {}
    with game.ProtectedSession():
        if not game.bridge_up(): game.launch()
        try:
            for c in conds:
                seeded.restart_same_seed()   # fresh world + car per condition (a long drive can end at an obstacle)
                b = Bridge(); b("set TLDRevamp.TestSafety.BlockAutoSave True")
                setup(b)
                b("net lag " + c)
                if RATE: b("set TLDRevamp.Net.VehicleLab.RateRange " + RATE[0])
                print(" ", c, b("vehlab start 6")); time.sleep(window)
                r = b("vehlab stop"); res[c] = r
                d = b("drivestats") if False else None
                moving = r["received"] > 0 and r["errorM"] is not None and r["truth"] and r["truth"]["devMaxCm"] > 1.0
                if not moving: print("   INVALID: the car wasn't driving in this window")
                e = r["errorM"] or {}
                print(f"   received {r['received']}, delay {r['delayMsAvg']} ms (+one-way latency), jitter {r['jitterMs']} ms, extrapolated {r['extrapolatedFrames']}/{r['frames']} frames")
                print(f"   ghost error m: mean {e.get('mean')} p99 {e.get('p99')} max {e.get('max')}")
                for k in ("truth", "interp", "snap"):
                    s = r[k]
                    if s: print(f"   {k:6} visible jumps(>20 cm) {s['jumps']:4}  deviation p50 {s['devP50cm']} p99 {s['devP99cm']} max {s['devMaxCm']} cm")
        finally:
            b = Bridge(); b("net lag 0"); b("autodrive off")
    path = pathlib.Path(__file__).resolve().parent.parent / "bench" / (time.strftime("%Y%m%d-%H%M%S") + f"-{label}.json")
    path.write_text(json.dumps(res, indent=1))

if __name__ == "__main__":
    main()
