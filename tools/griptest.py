#!/usr/bin/env python3
"""Walking another player's vehicle by its grip (v0.64.13, Entities.PushGrip). Two machines over the relay.
The game joins the player to the vehicle's body; on a copy that body is kinematic - the player was held by a vehicle
that does not move. Now taking the grip takes the vehicle.
  B the laptop takes the grip of the HOST's vehicle and walks with the steering held (0.3): the vehicle turns
  A then walks straight: the laptop owns it, the vehicle moves with the laptop's player, the host's copy is where
    the laptop has it (runs 3-4: B after A started where A left the car - the same spot both runs, 1 m up: stuck)
  C the host gets on it as its driver: the driver takes it, the laptop's player lets go
  D the laptop takes the grip while the host rides it: let go at once, the host keeps it
  tools/griptest.py [--no-build] [--relay iad|off] [--vehicle name]"""
import sys, os, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
VEH = sys.argv[sys.argv.index("--vehicle") + 1] if "--vehicle" in sys.argv else "Car01Full"   # run 2: Car01 is the bare body
VERDICTS = []
STEER = float(sys.argv[sys.argv.index("--steer") + 1]) if "--steer" in sys.argv else 0.3   # run 3: full lock (1) rolled 0.44 m - the walk goes sideways against the wheels

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
def gp(s): p = s("gpos"); return (p["gx"], p["gy"], p["gz"])
def yaw(e): return e.get("yaw")

def driver_seat(side):
    seats = side("seatraw")["seats"]
    main = [s for s in seats if s["main"] == "null" and s["d0"]]
    if not main: return None
    redir = [s["i"] for s in seats if s["main"] == f"#{main[0]['i']}"]
    return (redir[0], True) if redir else (main[0]["i"], False)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    grips = h("mp grips")["grips"]
    print("  vehicles with grips:", grips, flush=True)
    name = VEH or (grips[0][1] if grips else None)
    if not name: sys.exit("no vehicle has grips")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    gods = {}
    for nm, s_ in (("host", h), ("laptop", L)):
        gods[nm] = (s_, s_("get kaposztaleves.s.settings.god").get("value"))
        s_("set kaposztaleves.s.settings.god True")
    try:
        hp = h("gpos")
        h(f"tpground {hp['gx'] + 15} {hp['gz']}"); time.sleep(3)
        before = set(ents(h))
        print(f"  host spawns {name}:", h(f"devspawn {name}"), flush=True)
        until(lambda: any(not e["proxy"] for n, e in ents(h).items() if n not in before and e["part"] < 0 and e["name"].startswith(name)), 20)
        net = [n for n, e in ents(h).items() if n not in before and e["part"] < 0 and e["name"].startswith(name)][0]
        if not until(lambda: net in ents(L), 30): sys.exit("laptop never got the vehicle")
        time.sleep(6)
        # a parked car rolls only with its handbrake off (spawned cars have it on: carsigtest): the host lets it off
        ds = driver_seat(h)
        if ds:
            print("  host sits to let the handbrake off:", h(f"sitseat {ds[0]}{' redirect' if ds[1] else ''}"), flush=True); time.sleep(1.5)
            print("   ", h("handbrake off"), h(f"mp carsig {net}"), flush=True); time.sleep(1)
            h("driveout"); time.sleep(3)
        v0 = ents(h)[net]["pos"]
        L(f"tpground {v0[0] + 1.5} {v0[2] + 1.5}"); time.sleep(3)
        # A
        print("  laptop takes the grip:", L(f"mp grip {net} 0 0"), flush=True)
        time.sleep(1.5)
        le, he = ents(L)[net], ents(h)[net]
        print(f"  t+1.5 s: laptop owns {not le['proxy']}, host copy {he['proxy']}, laptop grip {L('mp grip')}, handbrake {L(f'mp carsig {net}')['handbrake']}", flush=True)
        # B
        g0 = L("mp grip"); q0 = ents(L)[net]
        print(f"  laptop walks 5 s steering {STEER}:", L(f"mp grip {net} 5 {STEER}"), flush=True)
        time.sleep(2.5); gm = L("mp grip"); time.sleep(3)
        g1 = L("mp grip"); q1 = ents(L)[net]
        turn = abs((g1["yaw"] - g0["yaw"] + 180) % 360 - 180); moved2 = math.dist(q0["pos"], q1["pos"])
        print(f"  B: yaw {g0['yaw']} -> {g1['yaw']} ({turn:.1f} deg), steer mid-walk {gm['steer']}, moved {moved2:.2f} m", flush=True)
        verdict("B STEERING TURNS IT", turn > 10 and moved2 > 1.0, f"turned {turn:.1f} deg, moved {moved2:.2f} m")
        p0 = gp(L); v0 = ents(L)[net]["pos"]
        print("  laptop walks 6 s:", L(f"mp grip {net} 6 0"), flush=True)
        time.sleep(6.5)
        p1 = gp(L)
        mv = ents(L)[net]["pos"]; time.sleep(3)   # run 5: read while still rolling, 0.55 m behind at relay latency
        le, he = ents(L)[net], ents(h)[net]
        print(f"  after the walk: still rolling {math.dist(mv, le['pos']):.2f} m in the 3 s after", flush=True)
        moved = math.dist(v0, le["pos"]); walked = math.dist(p0, p1); off = math.dist(le["pos"], he["pos"])
        print(f"  vehicle {[round(x, 2) for x in v0]} -> {[round(x, 2) for x in le['pos']]} ({moved:.2f} m), laptop player walked {walked:.2f} m, host copy {off:.2f} m off", flush=True)
        verdict("A WALKING THE HOST'S VEHICLE", not le["proxy"] and he["proxy"] and moved > 1.0 and walked > 1.0 and off < 0.3,
                f"owns {not le['proxy']}, moved {moved:.2f} m, walked {walked:.2f} m, copy off {off:.2f} m")
        # C
        cp = ents(h)[net]["pos"]   # run 5: walked 28 m off, the host sat in the car nearest to IT - another one
        h(f"tpground {cp[0] + 2.5} {cp[2] + 2.5}"); time.sleep(3)
        ds = driver_seat(h)
        print("  host driver seat:", ds, flush=True)
        if ds is None: verdict("C THE DRIVER TAKES IT", False, "no driver seat")
        else:
            print("  host sits:", h(f"sitseat {ds[0]}{' redirect' if ds[1] else ''}"), flush=True)
            time.sleep(4)
            le, he = ents(L)[net], ents(h)[net]; g = L("mp grip")
            verdict("C THE DRIVER TAKES IT, THE WALKER LETS GO", le["proxy"] and not he["proxy"] and not g["gripping"],
                    f"host owns {not he['proxy']}, laptop still gripping {g['gripping']} (lost {g['lost']})")
            # D
            print("  laptop tries the grip:", L(f"mp grip {net} 3 0"), flush=True)
            time.sleep(2)
            le, he = ents(L)[net], ents(h)[net]; g = L("mp grip")
            verdict("D NO GRIP ON A RIDDEN VEHICLE", le["proxy"] and not he["proxy"] and not g["gripping"] and g["refused"] > 0,
                    f"host owns {not he['proxy']}, laptop gripping {g['gripping']}, refused {g['refused']}")
    finally:
        try: L("mp grip stop")
        except Exception: pass
        try: h("driveout")
        except Exception: pass
        for nm, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", nm, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
