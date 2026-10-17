#!/usr/bin/env python3
"""Drill-down for copycost: with BOTS bot car01s near, which part of the game's player controller (fpscontroller.Update)
costs the laptop ~88 ms a frame? Profiles fpscontroller's sub-methods on both machines, reads the physics settings.
  tools/fpsdrill.py [--no-build] [--bots 14] [--window 12]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP
from roadkit import CAR01, until

BOTS = int(sys.argv[sys.argv.index("--bots") + 1]) if "--bots" in sys.argv else 14
WINDOW = float(sys.argv[sys.argv.index("--window") + 1]) if "--window" in sys.argv else 12.0
SUBS = ["Update", "RayStuff", "Move", "LookPointCalc", "InventoryStuff", "PickedUpPointCheck", "PickedUpForceAdd", "FallDamage",
        "LadderMove", "DrawRays", "Scan", "UpdHandPos", "RightHandGrip", "SetOutSideAudio", "ReloadStuff", "CanDuck", "GetLook", "Recenter"]
ensure_both("--no-build" not in sys.argv)
L = Agent()
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
        print("bot car:", b("mp botcar"), b("clearpile"), flush=True)
        L(f"tpground {rp['gx'] + 3} {rp['gz'] + 3}"); time.sleep(3)
        print("physics desktop:", b("physics"), "\nphysics laptop:", L("physics"), flush=True)
        b("set TLDRevamp.Net.Mp.BotCars True"); b(f"mp bots {BOTS} 100 16")
        until(lambda: sum(1 for e in L("mp entities")["entities"] if e["car"] and e["proxy"]) >= BOTS, 60, 1.0)
        time.sleep(8)
        # the laptop, variants of how copies move: ms per call of LookPointCalc (the frame's first physics query pays the
        # collider sync — the game runs with autoSyncTransforms on)
        variants = [("bodies, interpolated", {"CopiesMoveByBody": True, "CopiesInterpolate": True}),
                    ("bodies, no interpolation", {"CopiesMoveByBody": True, "CopiesInterpolate": False}),
                    ("transform writes (old)", {"CopiesMoveByBody": False, "CopiesInterpolate": True})]
        print("\nLAPTOP VARIANTS (LookPointCalc ms/call, frame p50 ms):", flush=True)
        for name, sw in variants:
            for k, v in sw.items(): L(f"set TLDRevamp.Net.Entities.{k} {v}")
            time.sleep(3); L("profile start fpscontroller.LookPointCalc"); time.sleep(1)
            L("reset"); time.sleep(WINDOW)   # profiler hooked before the counters reset: hooking stalls its frame
            st = L("stats"); p = L("profile stop 5")
            lp = next((m for m in p.get("byTime", []) if "LookPointCalc" in m["m"]), None)
            per = lp["msPerSec"] / max(1, lp["callsPerSec"]) if lp else float("nan")
            print(f"   {name:28} {per:8.2f} ms/call   frame p50 {st['p50ms']} ms", flush=True)
        L("set TLDRevamp.Net.Entities.CopiesMoveByBody True"); L("set TLDRevamp.Net.Entities.CopiesInterpolate True"); time.sleep(3)
        print("\nLAPTOP COLLIDER GROUPS OFF (LookPointCalc ms/call, frame p50 ms):", flush=True)
        for grp in ("wheelcols", "jointcols", "restcols"):
            r_ = L(f"mp copies {grp} off"); time.sleep(3); L("profile start fpscontroller.LookPointCalc"); time.sleep(1)
            L("reset"); time.sleep(WINDOW)   # profiler hooked before the counters reset: hooking stalls its frame
            st = L("stats"); p = L("profile stop 5")
            lp = next((m for m in p.get("byTime", []) if "LookPointCalc" in m["m"]), None)
            per = lp["msPerSec"] / max(1, lp["callsPerSec"]) if lp else float("nan")
            print(f"   {grp:10} off ({r_.get('switched')} colliders)  {per:8.2f} ms/call   frame p50 {st['p50ms']} ms", flush=True)
            L(f"mp copies {grp} on"); time.sleep(2)
        for n, s in (("desktop", b), ("laptop", L)):
            s("profile start " + " ".join("fpscontroller." + m for m in SUBS))
        time.sleep(WINDOW)
        for n, s in (("desktop", b), ("laptop", L)):
            p = s("profile stop 25")
            print(f"\n{n}: {p.get('seconds')} s")
            for m in p.get("byTime", [])[:20]: print(f"   {m['msPerSec']:>9} ms/s  {m['callsPerSec']:>6} calls/s  max {m['maxMs']:>7} ms  {m['m']}")
        # the same with the copies' colliders off, for the difference
        L("mp copies colliders off"); b("mp copies colliders off"); time.sleep(3)
        L("profile start " + " ".join("fpscontroller." + m for m in SUBS)); time.sleep(WINDOW)
        p = L("profile stop 25"); print("\nlaptop, copies' colliders OFF:")
        for m in p.get("byTime", [])[:10]: print(f"   {m['msPerSec']:>9} ms/s  {m['callsPerSec']:>6} calls/s  max {m['maxMs']:>7} ms  {m['m']}")
        b("mp stop"); L("mp stop")
finally:
    L.close()
