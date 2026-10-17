#!/usr/bin/env python3
"""Bug 3: dismounting a part on another player's car. Single player puts the part in your hand (UnCraft, then Pickup);
before v0.65.5 a non-owner got nothing in hand — the part dropped off a network delay later (and pressing again sent
more requests). Desktop only: a bot owns a car copy (and answers removal requests as an owner's game: PartOff); the
host, standing by it, dismounts its hubcap as the player (Entities.TestAsPlayer). Expected: one request, the hubcap
comes off and lands in the host's hand, the host owns it.
  tools/dismounthand.py [--no-build] [--car Car06Full]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

CAR = sys.argv[sys.argv.index("--car") + 1] if "--car" in sys.argv else "Car06Full"
T = "TLDRevamp.Net.Entities"
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
        b(f"deleteshare {a_net}")
        b("set TLDRevamp.Net.Mp.BotCars True")
        known = set(ents(b))
        b("mp bots 1 10 0")
        c_net = None
        for _ in range(60):
            c_net = next((n for n, e in ents(b).items() if n not in known and e["car"] and e["proxy"]), None)
            if c_net: break
            time.sleep(0.5)
        time.sleep(3)
        cp = ents(b)[c_net]["pos"]
        b(f"tpground {cp[0] + 2.5} {cp[2] + 1}"); time.sleep(2)
        a0 = b("mp entities")["attach"]
        known = set(ents(b))
        b(f"set {T}.TestAsPlayer True")
        print("  dismount hubcap:", b(f"mount off {c_net} DisztarcsaSlotCol"), flush=True)
        b(f"set {T}.TestAsPlayer False")
        time.sleep(2)
        a1 = b("mp entities")["attach"]
        held = b("get mainscript.s.player.pickedUp.name").get("value")
        new = [(n, e["name"], e["owner"], e["proxy"]) for n, e in ents(b).items() if n not in known]
        print(f"  requests {a1['detachReqsSent'] - a0['detachReqsSent']}, handed over {b(f'get {T}.HandedOver')['value']}, in hand {held}, new {new}", flush=True)
        part = next((x for x in new if "Disztarcsa" in x[1]), None)
        verdict("THE DISMOUNTED HUBCAP LANDS IN THE HAND, OURS", held is not None and "Disztarcsa" in str(held) and part is not None and part[2] == 0 and not part[3],
                f"in hand {held}, part entity {part}, requests {a1['detachReqsSent'] - a0['detachReqsSent']}")
        b("dropheld") if False else None
    finally:
        try: b(f"set {T}.TestAsPlayer False"); b("mp stop")
        except Exception: pass
        b("set TLDRevamp.Net.Mp.BotCars False")
        b(f"set kaposztaleves.s.settings.god {god if god is not None else False}")
game.quit_game()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
