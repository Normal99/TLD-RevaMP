#!/usr/bin/env python3
"""A/B of Physics.autoSyncTransforms (the game ships it ON) with BOTS bot car01s near: frame times on both machines and the
laptop's LookPointCalc (the frame's first physics query — with auto-sync on it pays the re-sync of every collider moved
since the last query: 36 ms a call on the laptop, fpsdrill v0.57.73). Windows alternate ON, OFF, ON, OFF so the laptop's
drift over a run (it got slower as runs went on) can't pass for an effect. Restored to ON at the end.
  tools/syncab.py [--no-build] [--bots 14] [--window 12]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP
from roadkit import CAR01, until

BOTS = int(sys.argv[sys.argv.index("--bots") + 1]) if "--bots" in sys.argv else 14
WINDOW = float(sys.argv[sys.argv.index("--window") + 1]) if "--window" in sys.argv else 12.0
ensure_both("--no-build" not in sys.argv)
L = Agent()
rows = []
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
        print("auto-sync now:", b("get UnityEngine.Physics.autoSyncTransforms"), L("get UnityEngine.Physics.autoSyncTransforms"), flush=True)
        b("set TLDRevamp.Net.Mp.BotCars True"); b(f"mp bots {BOTS} 100 16")
        until(lambda: sum(1 for e in L("mp entities")["entities"] if e["car"] and e["proxy"]) >= BOTS, 60, 1.0)
        time.sleep(8)
        def setsync(side, mode):
            for i in range(3):
                t = time.time()
                try:
                    r = side(f"set UnityEngine.Physics.autoSyncTransforms {mode}")
                    print(f"   set {mode}: {r} in {time.time() - t:.1f} s", flush=True); return
                except RuntimeError as e: print(f"   set {mode} try {i}: {e} after {time.time() - t:.1f} s; laptop stats {L('stats')}", flush=True)
            raise RuntimeError("could not switch auto-sync")
        print("before the first switch: laptop", L("stats"), flush=True)
        for mode in (True, False, True, False):
            for s in (b, L): setsync(s, mode)
            time.sleep(3)
            for s in (b, L): s("reset")
            L("profile start fpscontroller.LookPointCalc"); b("profile start fpscontroller.LookPointCalc")
            time.sleep(WINDOW)
            pl, pb = L("profile stop 5"), b("profile stop 5")
            sb, sl = b("stats"), L("stats")
            def per(p):
                m = next((m for m in p.get("byTime", []) if "LookPointCalc" in m["m"]), None)
                return m["msPerSec"] / max(1, m["callsPerSec"]) if m else float("nan")
            rows.append((mode, sb, sl, per(pb), per(pl)))
            print(f"auto-sync {'ON ' if mode else 'OFF'}: desktop p50 {sb['p50ms']} p99 {sb['p99ms']} look {per(pb):.2f} ms | laptop p50 {sl['p50ms']} p99 {sl['p99ms']} look {per(pl):.2f} ms", flush=True)
        for s in (b, L): setsync(s, True)
        b("mp stop"); L("mp stop")
finally:
    L.close()
