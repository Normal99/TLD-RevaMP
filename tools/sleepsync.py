#!/usr/bin/env python3
"""E3 sleep sync: the laptop sleeps (simulated via the game's own sleep state); when it ends, the desktop's
world clock must jump by the same amount (the skies stay in step)."""
import sys, time, pathlib
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

def wt(who):
    try: return float(who("get napszakvaltakozas.s.t")["value"])
    except Exception: return -1

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        game.quit_game(); game.launch()
        b = Bridge()
        b("set DataFromMenuScript.s.seed 20260924"); b("call menuhandler.s.PressedStart")
        until(lambda: b("get DataFromMenuScript.s.mainmenu")["value"] is False and b("get mainscript.s.player")["value"] is not None, 180)
        time.sleep(6)
        RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"   # real latency by default
        if RELAY != "off": b(f"net relay {RELAY}"); L(f"net relay {RELAY}")
        b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY != "off": L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 150); time.sleep(5)
        b0, l0 = wt(b), wt(L)
        print(f"  world clocks: desktop {b0:.2f} laptop {l0:.2f}")
        # the laptop sleeps: enter the game's own sleep state for ~4 s (the edge fires on exit)
        L("set mainscript.s.sleeping True")
        L("set mainscript.s.sleepEndTime 0")   # sleepEndTime <= Time.time: the sleep ends next frame
        time.sleep(6)
        b1, l1 = wt(b), wt(L)
        print(f"  after the laptop's sleep: desktop {b1:.2f} (moved {b1-b0:+.2f}) laptop {l1:.2f} (moved {l1-l0:+.2f})")
        ok = abs(b1 - l1) < 0.5 and (l1 - l0) > 0.1   # the clocks converge: the final gap is the metric
        print("SLEEP-SYNC VERDICT:", "PASS — the desktop's clock jumped with the sleeper" if ok else "FAIL")
        L("set mainscript.s.sleeping False"); b("mp stop"); L("mp stop")
finally:
    L.close()
