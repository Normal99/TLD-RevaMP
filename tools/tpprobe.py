#!/usr/bin/env python3
"""Long teleports in a session: the far-world test landed players 129–1,836 km from where they were sent and killed
the laptop with fall damage at 2,040,716 m/s — only in MP (single player: clean). Here: host + laptop joined (relay),
then one side teleports 200 km out and back, sampling its body's velocity, rebases and position every ~0.2 s.
  tools/tpprobe.py [--no-build] [--who laptop|desktop] [--relay iad] [--solo]   (--solo: no session, same trips)"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP
from roadkit import until

def arg(n, d): return sys.argv[sys.argv.index(n) + 1] if n in sys.argv else d
WHO, RELAY = arg("--who", "laptop"), arg("--relay", "iad")
if RELAY == "off": RELAY = None

def sample(s, secs, label):
    t0 = time.time(); worst = 0.0
    while time.time() - t0 < secs:
        v = s("get mainscript.s.player.RB.velocity")["value"]
        sp = sum(float(x) ** 2 for x in str(v).split(",")) ** 0.5
        worst = max(worst, sp)
        g = s("gpos"); c = s("get mainscript.s.visszarakvaCount")["value"]
        par = s("get mainscript.s.player.transform.parent")["value"]
        print(f"   {label} t {time.time()-t0:5.2f} vel {v} |{sp:.1f}| rebases {c} at {g['gx']:.0f},{g['gz']:.0f} parent {par}", flush=True)
        time.sleep(0.2)
    return worst

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        b = seeded_both(L)
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        time.sleep(8)
        if "--solo" not in sys.argv:
            if RELAY: print("relay:", b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
            b("mp host 27070")
            if RELAY: L(f"mp joinid {b('net me')['steamId']}")
            else: L(f"mp join {DESKTOP_IP}:27070")
            if not until(lambda: L("mp status")["client"]["ready"], 180, 2): sys.exit("LAPTOP NEVER JOINED")
            L("set TLDRevamp.TestSafety.BlockAutoSave True"); time.sleep(5)
        s = L if WHO == "laptop" else b
        home = s("gpos")
        for trip, (tx, tz) in enumerate([(home["gx"] + 200000, home["gz"]), (home["gx"], home["gz"])]):
            s("set mainscript.s.player.godModeTillGrounded True")
            print(f"trip {trip}: {WHO} to {tx:.0f},{tz:.0f}", s(f"tpg {tx} {tz}" + (" hold" if "--hold" in sys.argv else "")), flush=True)
            w = sample(s, 6, f"trip{trip} falling")
            # like farworld's travel(): wait for ground there, then stand on it (tpground)
            def grounded():
                g = s("gpos"); return "y" in s(f"ground {g['ux']} {g['uz']}")
            until(grounded, 60, 1.0)
            r = s(f"tpground {tx} {tz}")
            if r.get("tpground") == "hop": until(grounded, 30, 1.0); r = s(f"tpground {tx} {tz}")
            print(f"trip {trip}: tpground {r}", flush=True)
            w = max(w, sample(s, 12, f"trip{trip} landed"))
            g = s("gpos")
            print(f"trip {trip}: worst speed {w:.1f} m/s, ended {g['gx'] - tx:.0f},{g['gz'] - tz:.0f} m from the target, deaths {s('get TLDRevamp.Diagnostics.DeathLog.Deaths')['value']}", flush=True)
        if "--solo" not in sys.argv:
            b("mp stop"); L("mp stop")
            if RELAY: b("net relay off"); L("net relay off")
finally:
    L.close()
