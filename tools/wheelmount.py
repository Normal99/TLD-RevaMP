#!/usr/bin/env python3
"""Bug 3 (2026-10-03, "wheel attachment is buggy on cars if you are not the owner"). Desktop only: the host takes a wheel
off its own car, a bot shares a copy of that car (its owner: the wheel slot empty) and drives it slowly in a circle; the
host mounts the loose wheel onto the BOT's car the way the attach key does (partslotscript.Craft on the display copy).
Watched for 12 s: the wheel in its slot (distance to the slot point), attached/crafted, who owns it (the server moves a
part bolted onto someone's object to that object's owner), its bodies and colliders.
  tools/wheelmount.py [--no-build] [--car Car06Full] [--speed 3]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

CAR = sys.argv[sys.argv.index("--car") + 1] if "--car" in sys.argv else "Car06Full"
SPEED = sys.argv[sys.argv.index("--speed") + 1] if "--speed" in sys.argv else "3"
VERDICTS = []

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None

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
        slots = b(f"mount slots {a_net}")["slots"]
        slot = next(r[0] for r in slots if "Wheel" in r[0] and r[1])
        print(f"  wheel slot {slot}, all: {slots}", flush=True)
        known = set(ents(b))
        # the game's own order: the hubcap covers the wheel (cover_CantCraftIfCovered), it comes off first
        print("  hubcap off:", b(f"mount off {a_net} DisztarcsaSlotCol"), flush=True); time.sleep(2)
        print("  wheel off:", b(f"mount off {a_net} {slot}"), flush=True); time.sleep(3)
        w_net = until(lambda: next((n for n, e in ents(b).items() if n not in known and not e["car"] and e["name"].startswith("Wheel")), None), 10)
        print(f"  the wheel's entity: {w_net} {ents(b).get(w_net)}", flush=True)
        # the bot's car: a copy of this car as it is now (the slot empty), driven by the bot in a circle around here
        print("  botcar:", b("mp botcar"), flush=True)
        b("set TLDRevamp.Net.Mp.BotCars True")
        known = set(ents(b))
        print("  bot:", b(f"mp bots 1 14 {SPEED}"), flush=True)
        c_net = until(lambda: next((n for n, e in ents(b).items() if n not in known and e["car"] and e["proxy"]), None), 40)
        if c_net is None or w_net is None: verdict("SETUP", False, f"bot car {c_net} wheel {w_net}"); raise SystemExit
        time.sleep(4)
        print(f"  bot's car slots: {b(f'mount slots {c_net}')['slots']}", flush=True)
        cp = ents(b)[c_net]["pos"]
        b(f"tpground {cp[0] + 3} {cp[2] + 3}"); time.sleep(1)
        print("  mount on the bot's car:", b(f"mount on {w_net} {c_net} {slot}"), flush=True)
        worst, samples, lost = 0.0, [], None
        t0 = time.time()
        while time.time() - t0 < 12:
            r = b(f"mount pose {w_net} {c_net} {slot}")
            samples.append(r)
            if "error" not in r and time.time() - t0 > 1.5:   # the game slides a part into its slot first (SmoothCraft)
                worst = max(worst, r["dist"])
                if lost is None and not r["inSlot"]: lost = round(time.time() - t0, 1)
            time.sleep(0.5)
        for s in samples[::4] + samples[-1:]: print("   ", s, flush=True)
        last = samples[-1]
        print("  attach counters:", b("mp entities").get("attach"), flush=True)
        verdict("THE WHEEL STAYS IN THE BOT'S CAR'S SLOT", lost is None and worst < 0.05, f"worst distance {worst:.3f} m, left the slot at {lost}")
        now = ents(b); car_owner, wheel_owner = now[c_net]["owner"], now[w_net]["owner"]   # read together (a lease moves both)
        print(f"  owner log: car {b(f'mp ownerlog {c_net}')['log']}\n             wheel {b(f'mp ownerlog {w_net}')['log']}", flush=True)
        # the car's owner — whoever simulates the bot's car now (a crash lease may hand it to the host for a while: v0.66.5
        # carries what is bolted onto it along)
        verdict("IT BECOMES THE CAR OWNER'S, RIDING ITS CAR", wheel_owner == car_owner and last.get("ridesParent"),
                f"wheel owner {wheel_owner} (last sample {last.get('owner')}) proxy {last.get('proxy')} rides {last.get('ridesParent')}; the car's owner {car_owner}")
    finally:
        try: b("mp stop")
        except Exception: pass
        b("set TLDRevamp.Net.Mp.BotCars False")
        b(f"set kaposztaleves.s.settings.god {god if god is not None else False}")
game.quit_game()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
