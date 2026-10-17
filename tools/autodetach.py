#!/usr/bin/env python3
"""Bugs 3/7 (2026-10-03): the friend's machine took the host's rear seat and door off over and over (ApplyDetachReq,
6 times in 30 s) — requests from the host's DISPLAY COPY of the car. v0.65.4 forwards a copy's detach only when the
local player did it (Entities.PlayerActing); anything else is counted (autoDetachesIgnored). Desktop only: a bot owns a
copy of the car (BotCars), parked next to the host's player for 30 s, the host walks around it; counted: detaches the
game tried on the copy by itself, requests that went out.
  tools/autodetach.py [--no-build] [--car Car10Full]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

CAR = sys.argv[sys.argv.index("--car") + 1] if "--car" in sys.argv else "Car10Full"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def ents(b): return {e["net"]: e for e in b("mp entities")["entities"]}

if game.running(): game.quit_game()
if "--no-build" not in sys.argv: game.build()
else: game.deploy_local()
game.launch()
with game.ProtectedSession():
    game.newgame()
    b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True")
    god = b("get kaposztaleves.s.settings.god").get("value"); b("set kaposztaleves.s.settings.god True")
    try:
        p = b("gpos"); b(f"tpground {p['gx'] + 15} {p['gz']}"); time.sleep(3)
        b("mp host 27070")
        before = set(ents(b))
        print("  car:", b(f"devspawn {CAR}"), flush=True); time.sleep(8)
        a_net = next(n for n, e in ents(b).items() if n not in before and e["car"])
        print("  botcar:", b("mp botcar"), flush=True)
        b("deleteshare %d" % a_net)   # only the bot's copy remains
        b("set TLDRevamp.Net.Mp.BotCars True")
        known = set(ents(b))
        b("mp bots 1 10 0")
        c_net = None
        for _ in range(60):
            c_net = next((n for n, e in ents(b).items() if n not in known and e["car"] and e["proxy"]), None)
            if c_net: break
            time.sleep(0.5)
        print(f"  bot's car: {c_net}", flush=True)
        a0 = b("mp entities")["attach"]
        cp = ents(b)[c_net]["pos"]
        for k in range(15):   # walk around it
            ang = k * 24 * 3.14159 / 180
            import math
            b(f"tpground {cp[0] + 3.5 * math.cos(ang)} {cp[2] + 3.5 * math.sin(ang)}"); time.sleep(2)
        a1 = b("mp entities")["attach"]
        st = b("mp entities")
        print(f"  before {a0}\n  after  {a1}\n  suppressedFallOff {st.get('suppressedFallOff')}", flush=True)
        auto = a1["autoDetachesIgnored"] - a0["autoDetachesIgnored"]; req = a1["detachReqsSent"] - a0["detachReqsSent"]
        print(f"  the game detached on the copy by itself {auto} times; requests sent {req}", flush=True)
        verdict("NO DETACH REQUESTS FROM A COPY NOBODY TOUCHED", req == 0, f"automatic detaches ignored {auto}, requests {req}")
    finally:
        try: b("mp stop")
        except Exception: pass
        b("set TLDRevamp.Net.Mp.BotCars False")
        b(f"set kaposztaleves.s.settings.god {god if god is not None else False}")
game.quit_game()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
