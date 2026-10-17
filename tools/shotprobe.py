#!/usr/bin/env python3
"""One machine (the desktop), the shooting tool alone: fresh game, seeded world, god mode (restored), a creature and a
magnum spawned, the player 7 m in front of the creature, 6 shots with the full reply each (gun chosen, can it shoot,
did the game's ray run, what it met). tools/shotprobe.py [--no-build]"""
import sys, time, math, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from roadkit import until, g, tp

if "--no-build" not in sys.argv: game.build()
game.quit_game(); game.launch()
god = None
with game.ProtectedSession():
    seeded.restart_same_seed()
    b = Bridge()
    try:
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        god = g(b, "kaposztaleves.s.settings.god"); b("set kaposztaleves.s.settings.god True")
        ids = b("mp aiids")["ai"]; print("creatures:", ids, flush=True)
        rp = b("roadpoint"); print("road:", tp(b, rp["gx"], rp["gz"]), flush=True); time.sleep(2)
        before = {a["who"] for a in b("mp ai")["ai"]}
        print(b(f"devspawn {ids[0][0]}"), flush=True)
        until(lambda: len(b("mp ai")["ai"]) > len(before), 10)
        a = [x for x in b("mp ai")["ai"] if x["who"] not in before][0]
        print("creature:", a, flush=True)
        lk = a["look"]; n = math.hypot(*lk) or 1
        print("7 m in front:", tp(b, a["pos"][0] + lk[0] / n * 7, a["pos"][2] + lk[1] / n * 7), flush=True)
        print("magnum:", b("devspawn 64"), flush=True); time.sleep(1.5)
        for c in b("mp aicols")["cols"]: print("   col", c, flush=True)
        for k in range(40):
            a = [x for x in b("mp ai")["ai"] if x["who"] == a["who"]][0]
            if a["died"]: print(f"DEAD after {k} shots", flush=True); break
            me = b("gpos")
            if math.hypot(me["gx"] - a["pos"][0], me["gz"] - a["pos"][2]) < 3.0:
                lk = a["look"]; n = math.hypot(*lk) or 1
                tp(b, a["pos"][0] + lk[0] / n * 7, a["pos"][2] + lk[1] / n * 7); time.sleep(0.6)
                a = [x for x in b("mp ai")["ai"] if x["who"] == a["who"]][0]
            r = b(f"mp shootpoint {a['aim'][0]} {a['aim'][1]} {a['aim'][2]} eye")
            if k < 3: print(f"shot {k + 1}: {json.dumps(r)}", flush=True)
            print(f"   shot {k + 1}: aim {a['aim']} root {a['pos']} rayN {r.get('rayN')} can {r.get('canShoot')} gun {r.get('gun')} hits {r.get('shotHits')} hp {a['health']}", flush=True)
            time.sleep(0.6)
        else: print("NOT DEAD after 40 shots", flush=True)
        a = [x for x in b("mp ai")["ai"] if x["who"] == a["who"]][0]; print("end:", a, flush=True)
    finally:
        if god is not None: b(f"set kaposztaleves.s.settings.god {god}")
