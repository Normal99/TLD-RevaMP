#!/usr/bin/env python3
"""Bug 3, the owner's side: another player bolts a wheel onto YOUR car. Desktop only: the host takes a wheel off its
car (hubcap first, as the game requires); an item bot shares a copy of that wheel (the host's own is removed) and sends
the AttachSync a remote player's attach key sends for the empty slot. The host — the car's owner — replays it
(save_attachable.Load → the slot's Craft) and takes the wheel over (bolted onto its object). Then the host drives the car.
Compared with the same car whose wheel the host bolts back on itself (control).
  checks: the wheel in the slot, the host's, no display-copy leftovers (kinematic body / no collisions); driving 6 s at
  10 m/s: no part lost, no crash damage beyond the control's, the car's wheel sits on the ground (not sunk)
  tools/wheelowner.py [--no-build] [--car Car06Full]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

CAR = sys.argv[sys.argv.index("--car") + 1] if "--car" in sys.argv else "Car06Full"
T = "TLDRevamp.Net.Entities"
VERDICTS = []
RESULTS = {}

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

def seat_idx(b):
    seats = b("seatraw")["seats"]
    target = next(s["i"] for s in seats if s["main"] == "null" and s["d0"])
    return next(s["i"] for s in seats if s["main"] == f"#{target}")

def drive(b, net, slot, w_net):
    cp = ents(b)[net]["pos"]
    b(f"tpground {cp[0] + 3} {cp[2] + 3}"); time.sleep(1)
    b(f"sitseat {seat_idx(b)} redirect"); time.sleep(2)
    b("crash rec start 30"); time.sleep(0.5)
    b("crash launch 5"); time.sleep(6)
    rec = b("crash rec stop")
    car = next((c for c in rec["cars"] if c.get("net") == net), {})
    pose = b(f"mount pose {w_net} {net} {slot}")
    b("driveout")
    return car, pose

def run(b, cfg):
    p = b("gpos"); b(f"tpground {p['gx'] + 15} {p['gz']}"); time.sleep(3)
    b("mp host 27070")
    b(f"set {T}.CargoClaims False")   # the bot's wheel waits beside the car, not in it
    before = set(ents(b))
    print(f"  [{cfg}] car:", b(f"devspawn {CAR}"), flush=True); time.sleep(8)
    net = next(n for n, e in ents(b).items() if n not in before and e["car"])
    slots = b(f"mount slots {net}")["slots"]
    slot = next(r[0] for r in slots if "Wheel" in r[0] and r[1])
    known = set(ents(b))
    print("  hubcap off:", b(f"mount off {net} DisztarcsaSlotCol"), "wheel off:", b(f"mount off {net} {slot}"), flush=True)
    time.sleep(3)
    w_net = until(lambda: next((n for n, e in ents(b).items() if n not in known and e["name"].startswith("Wheel")), None), 10)
    if cfg == "control":
        print("  the host bolts it back:", b(f"mount on {w_net} {net} {slot}"), flush=True)
        wid = w_net
    else:
        cp = ents(b)[net]["pos"]
        b(f"tpground {cp[0] + 6} {cp[2]}"); time.sleep(1)
        known = set(ents(b))
        print("  item bot:", b(f"mp botitem 3 0.6 0 0.1 0 {w_net}"), flush=True)
        b(f"deleteshare {w_net}")   # the host's own wheel: only the bot's remains
        wid = until(lambda: next((n for n, e in ents(b).items() if n not in known and not e["car"] and e["owner"] != 0), None), 30)   # spawned from its record: TireFelni(Clone)
        if wid is None:
            st = b("mp entities")
            print("  addRejects", st.get("addRejects"), st.get("addRejectLast"), "new:", [(n, e["name"], e["owner"], e["part"], e["parent"]) for n, e in ents(b).items() if n not in known], flush=True)
            print("  mp status:", b("mp status"), flush=True)
            verdict(f"{cfg}: SETUP", False, "the bot's wheel never arrived"); return
        time.sleep(3)
        print("  bot bolts it on:", b(f"mp botattach {net} {slot}"), flush=True)
    time.sleep(3)
    pose = b(f"mount pose {wid} {net} {slot}")
    print("  after the attach:", pose, "\n  counters:", b("mp entities").get("attach"), flush=True)
    car, pose2 = drive(b, net, slot, wid)
    print(f"  drive: {car}\n  wheel after: {pose2}", flush=True)
    ok_on = pose.get("inSlot") and pose.get("dist", 9) < 0.05
    if cfg == "bot":
        verdict("BOT'S WHEEL BOLTED ON HERE, THE HOST'S NOW (OWNER OF THE CAR)", ok_on and pose.get("owner") == 0 and not pose.get("proxy"), f"{pose}")
    else:
        verdict("CONTROL: THE HOST'S OWN WHEEL BACK ON", ok_on, f"{pose}")
    print(f"  {cfg}: in slot {pose2.get('inSlot')}, parts lost {car.get('partsLost')}, damage {car.get('damageMaxV')}, rise {car.get('riseM')}, up {car.get('endUpY')}, moved {car.get('movedAfterM')}", flush=True)
    RESULTS[cfg] = (pose2, car)

if game.running(): game.quit_game()
if "--no-build" not in sys.argv: game.build()
else: game.deploy_local()
game.launch()
with game.ProtectedSession():
    for cfg in (sys.argv[sys.argv.index("--only") + 1].split(",") if "--only" in sys.argv else ("control", "bot")):
        game.newgame()
        b = Bridge()
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        god = b("get kaposztaleves.s.settings.god").get("value"); b("set kaposztaleves.s.settings.god True")
        try: run(b, cfg)
        except Exception as ex: verdict(f"{cfg}: RAN", False, repr(ex))
        finally:
            try: b(f"set {T}.CargoClaims True"); b("mp stop")
            except Exception: pass
            b(f"set kaposztaleves.s.settings.god {god if god is not None else False}")
game.quit_game()
if "control" in RESULTS and "bot" in RESULTS:
    (pc, cc), (pb, cb) = RESULTS["control"], RESULTS["bot"]
    verdict("DRIVES LIKE THE CONTROL: WHEEL STAYS, NO PARTS LOST, SAME DAMAGE/UPRIGHT",
            pb.get("inSlot") and cb.get("partsLost") == cc.get("partsLost") == 0 and cb.get("damageMaxV", 99) <= cc.get("damageMaxV", 0) + 3 and (cb.get("endUpY", 0) > 0.8) == (cc.get("endUpY", 0) > 0.8),
            f"control: damage {cc.get('damageMaxV')} up {cc.get('endUpY')} moved {cc.get('movedAfterM')}; bot's wheel: damage {cb.get('damageMaxV')} up {cb.get('endUpY')} moved {cb.get('movedAfterM')}")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
