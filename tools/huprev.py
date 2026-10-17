#!/usr/bin/env python3
"""Going back to an earlier video on the head unit (the crash at prev, prev; 2026-10-15), step by step, single player.
  tools/huprev.py [--no-build]   env HU_SET="HeadUnit.Video.MakeGapS 0;..." HU_STEPS="prev,1;prev,6" (a step "remount,8": a new unit in the slot, the old destroyed) """
import os
os.environ.setdefault("TLD_TEST_MODS", "CarTrailer,HeadUnit")
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

def call(b, what):
    try: return str(b("call HeadUnit.Probe." + what)["value"])
    except Exception as e: return "ERROR " + str(e)[:200]

import subprocess
def rss_mb():
    try:
        pids = subprocess.run(["pgrep", "-f", "TheLongDrive.exe"], capture_output=True, text=True).stdout.split()
        return max(int(open(f"/proc/{p}/status").read().split("VmRSS:")[1].split()[0]) for p in pids) // 1024
    except Exception: return -1

def watch(b, secs):
    t0 = time.time()
    while time.time() - t0 < secs:
        if not game.running(): return f"GAME GONE after {time.time() - t0:.1f} s"
        time.sleep(0.2)
    return call(b, "Video")

with game.ProtectedSession():
    if "--no-build" not in sys.argv: game.build()
    else: game.local_mods()
    game.quit_game(); game.launch(); game.newgame()
    b = Bridge(); time.sleep(3)
    for kv in filter(None, os.environ.get("HU_SET", "").split(";")): print(" set", kv, b("set " + kv))
    b("devspawn 2"); time.sleep(3); b("devspawn 9100"); time.sleep(3)
    print(" mount:", call(b, "Mount")); time.sleep(2)
    call(b, "Press pwr"); call(b, "Press video"); time.sleep(1)
    for v in os.environ.get("HU_VIDEOS", "jNQXAC9IVRw,dQw4w9WgXcQ").split(","): call(b, f"Add {v}")
    print(" first:", watch(b, 8), "rss", rss_mb(), "MB")
    call(b, "Press next"); print(" next:", watch(b, 8))
    for step in os.environ.get("HU_STEPS", "prev,1;prev,6").split(";"):
        btn, secs = step.split(",")
        if btn == "remount":   # a new unit into the slot: the old one (its players) taken out and destroyed (Probe.Mount)
            b("devspawn 9100"); time.sleep(3); print(" remount:", call(b, "Mount")); time.sleep(2)
            call(b, "Press pwr"); call(b, "Press video"); time.sleep(1)
            for v in reversed(os.environ.get("HU_VIDEOS", "jNQXAC9IVRw,dQw4w9WgXcQ").split(",")): call(b, f"Add {v}")
        else: r = call(b, f"Press {btn}")
        print(f" {btn} -> {watch(b, float(secs))} rss {rss_mb()} MB", flush=True)
    alive = game.running()
    print("ALIVE" if alive else "CRASHED")
    if "--keep" not in sys.argv: game.quit_game()
