#!/usr/bin/env python3
"""Why do the same 14 far car copies cost the laptop ~36 ms a frame and the desktop ~1 ms (copycost v0.57.75: laptop
54 ms with them, 18 ms with their colliders off; desktop 15.5 vs 14.8)? The CPUs are close per core (Ryzen 5 7530U vs
Ryzen 9 3900X). Per machine: physics stats + floating origin, then Unity frame phases (PlayerLoopProfiler) over
WINDOW-second windows with the copies' colliders on and off.
  tools/copydiag.py [--no-build] [--bots 14] [--window 12]"""
import sys, time, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP
from roadkit import CAR01, until

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
BOTS, WINDOW = arg("--bots", 14, int), arg("--window", 12.0, float)

def phases(sides):
    for s in sides: s("set TLDRevamp.PlayerLoopProfiler.SlowMs 0"); s("reset"); s("loopprof start")   # every frame "slow": phases sorted by total time
    time.sleep(WINDOW)
    out = []
    for s in sides:
        lp = s("loopprof stop 12"); st = s("stats")
        out.append({"p50": st["p50ms"], "frames": lp["frames"], "phases": [(p["p"], p["avgMsAll"], p["maxMs"]) for p in lp["phases"]]})
    return out

ensure_both("--no-build" not in sys.argv)
L = Agent()
res = {}
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        b("mp host 27070"); L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        rp = b("roadpoint")
        b(f"spawncarg {CAR01} 0 {rp['gx'] + 6} {rp['gz']} 0"); time.sleep(3)
        b(f"tpground {rp['gx'] + 8} {rp['gz'] + 1}"); time.sleep(2)
        bc = b("mp botcar"); b("clearpile")
        if "Car01" not in bc.get("car", ""): sys.exit(f"bots would drive {bc}")
        L(f"tpground {rp['gx'] + 3} {rp['gz'] + 3}"); time.sleep(3)
        print("physics before bots\n  desktop:", b("physics"), "\n  laptop: ", L("physics"), flush=True)
        res["no bots"] = phases([b, L])
        b("set TLDRevamp.Net.Mp.BotCars True")
        print("bots:", b(f"mp bots {BOTS} 100 16")["bots"], flush=True)
        until(lambda: sum(1 for e in L("mp entities")["entities"] if e["car"] and e["proxy"]) >= BOTS, 60, 1.0)
        time.sleep(8)
        print("physics with bots\n  desktop:", b("physics"), "\n  laptop: ", L("physics"), flush=True)
        print("gpos desktop", b("gpos"), "laptop", L("gpos"), flush=True)
        print("copies desktop", b("mp physical"), "\n       laptop", L("mp physical"), flush=True)
        res["14 far copies"] = phases([b, L])
        for s in (b, L): s("mp copies colliders off")
        res["their colliders off"] = phases([b, L])
        for s in (b, L): s("mp copies colliders on")
        b("mp stop"); L("mp stop")
finally:
    L.close()

for k, (d, l) in res.items():
    print(f"\n=== {k}: frame p50 desktop {d['p50']} ms ({d['frames']} frames), laptop {l['p50']} ms ({l['frames']} frames)")
    for name, side in (("desktop", d), ("laptop", l)):
        print(f"  {name}: " + " | ".join(f"{p} {a}/{m}" for p, a, m in side["phases"][:8]))
