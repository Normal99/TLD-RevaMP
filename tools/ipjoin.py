#!/usr/bin/env python3
"""Join IP without a port (two machines, LAN): the laptop types only the desktop's address; it must reach the F7 host
on UDP 27070 (before v0.64.4 a bare address parsed to port 0 and the join silently went nowhere).
  tools/ipjoin.py [--no-build]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

ensure_both("--no-build" not in sys.argv)
L = Agent()
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    print("host:", h("mp host 27070"), flush=True)
    print(f"laptop joins '{DESKTOP_IP}' (no port):", str(L(f"mp join {DESKTOP_IP}"))[:200], flush=True)
    ok = until(lambda: L("mp status")["client"]["ready"], 90)
    print("VERDICT:", "PASS - joined with a bare address" if ok else "FAIL - never joined", flush=True)
    L("mp stop"); h("mp stop")
L.close()
