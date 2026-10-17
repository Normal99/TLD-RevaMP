#!/usr/bin/env python3
"""One machine, no session: the prefab truth about car seats (mainseat redirects, own sitPos, look limits) and
what sitting in each seat does to the look state. Usage: seatprobe.py [spawnpile-type ...] (default: 2 = car01, 6 = car06)."""
import sys, time, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import running_build, local_build_hash

types = [a for a in sys.argv[1:] if a.isdigit()] or ["2"]
if running_build() != local_build_hash(): game.quit_game(); game.launch()
with game.ProtectedSession():
    seeded.restart_same_seed(); b = Bridge()
    b("set kaposztaleves.s.settings.god True")
    if "--host" in sys.argv: print("hosting:", "server" in b("mp host 27070"))   # a session: shared cars must keep the game's seats
    rp = b("roadpoint")
    for n, t in enumerate(types):
        x, z = rp["x"] + 40 * n, rp["z"] + 20
        g = b(f"ground {x} {z}")
        b(f"teleportyaw {x} {g['y'] + 1.5} {z - 6} 0")
        b(f"spawnpile {t} 1 {x} {g['y']} {z} 1 1 1.0 {777 + n}"); time.sleep(4)
        b(f"carface {x} {g['y']} {z} 0"); time.sleep(2)
        if "--host" in sys.argv: print("   shared:", any(e["car"] for e in b("mp entities")["entities"] if e["part"] < 0 and e["owner"] == 0))
        r = b("seatraw")
        print(f"== spawnpile {t}: {r['car']} — {len(r['seats'])} seatscripts")
        for s in r["seats"]: print("  ", json.dumps(s))
        # sit in every seat as the game would (with the mainseat redirect) and without, record the look state
        for s in r["seats"]:
            for redirect in ((True,) if "--host" in sys.argv else (True, False)):
                if b("get mainscript.s.player.Bsitting")["value"] is True: b("driveout"); time.sleep(1)
                b(f"teleportyaw {x} {g['y'] + 1.5} {z - 4} 0"); time.sleep(0.5)
                res = b(f"sitseat {s['i']} {'redirect' if redirect else 'raw'}")
                time.sleep(0.8)
                b("set mainscript.s.player.FxRot 75"); time.sleep(0.5)
                look = b("seatlook")
                print(f"   seat {s['i']:2} {'E-press' if redirect else 'raw    '}: {res} -> {look}")
        if b("get mainscript.s.player.Bsitting")["value"] is True: b("driveout")
    if "--host" in sys.argv: b("mp stop")
    b("set kaposztaleves.s.settings.god False")
