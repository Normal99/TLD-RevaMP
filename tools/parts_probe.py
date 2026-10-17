#!/usr/bin/env python3
"""Deterministic part-off check: laptop shares a car, removes one part; both machines' view is printed."""
import sys, time, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, restart_both, DESKTOP_IP
from determinism import glob, go_global
restart_both(True)
L = Agent()
try:
    with game.ProtectedSession():
        L("!seeded"); seeded.restart_same_seed(); b = Bridge()
        for s_ in (b, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True"); s_("set kaposztaleves.s.settings.god True")
        b("mp host 27070"); L(f"mp join {DESKTOP_IP}:27070"); time.sleep(5)
        h = glob(b); go_global(b, h[0] + 60, h[2] + 40); me = glob(b)
        g = L(f"ground {me[0] + 12} {me[2]}"); L(f"teleportyaw {me[0] + 8} {g['y'] + 1.5} {me[2]} 90")
        gd = b(f"ground {me[0]} {me[2]}"); b(f"teleportyaw {me[0]} {gd['y'] + 1.5} {me[2]} 90")   # desktop faces the car spot
        L(f"spawnpile 2 1 {me[0] + 12} {g['y']} {me[2]} 1 1 1.0 4242"); time.sleep(4); print(L("mp share")); time.sleep(4)
        print("DESKTOP copy:", json.dumps(b("mp entdebug")))
        print("LAPTOP owner:", json.dumps(L("mp entdebug")))
        print("desktop screenshot:", b("screenshot")["path"])
        for i in (5, 10, 20):
            try: print("detach", i, L(f"mp detachpart {i}"))
            except RuntimeError as e: print("detach", i, str(e)[:120])
            time.sleep(2)
        le, de = L("mp entities"), b("mp entities")
        for name, e in (("laptop", le), ("desktop", de)):
            print(name, {k: v for k, v in e.items() if k != "entities"})
            for x in e["entities"]: print("   ", {k: x[k] for k in ("net", "parent", "part", "proxy", "owner", "attached", "pos")})
        # a drop: sample the car's position on both machines
        car = max((x for x in le["entities"] if x["part"] < 0), key=lambda x: x["net"])["net"]
        L(f"carmove {me[0] + 12} {g['y'] + 20} {me[2]} 0")
        for k in range(16):
            lp = {x["net"]: x for x in L("mp entities")["entities"]}.get(car, {}).get("pos")
            dp = {x["net"]: x for x in b("mp entities")["entities"]}.get(car, {}).get("pos")
            print(f"   t={k*0.25:.2f}s laptop y={lp and round(lp[1],1)} desktop y={dp and round(dp[1],1)}")
            time.sleep(0.25)
        print("after drop", {k: v for k, v in L("mp entities").items() if k != "entities"}, {k: v for k, v in b("mp entities").items() if k != "entities"})
        b("mp stop"); L("mp stop")
finally:
    L.close()
