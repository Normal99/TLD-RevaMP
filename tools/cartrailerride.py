#!/usr/bin/env python3
"""How the Car Trailer example mod rides behind a car (its stability), single player on this machine: a car01 tows the
trailer along the road with the autopilot at a few speeds, empty and then with a car01 on the deck; the trailer's
sway (its angle to the car), roll, pitch and bounce are sampled ten times a second (CarTrailer.Probe.Ride). The game's
own trailer (item 53) rides the same course first, as the yardstick.
  TLD_TEST_MODS=CarTrailer tools/cartrailerride.py [--no-build] [--speeds 12,20,26] [--secs 18] [--only 9201] [--keep]"""
import os
os.environ.setdefault("TLD_TEST_MODS", "CarTrailer")
import sys, time, math, pathlib, statistics, re, json
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
from roadkit import clear_road, car_ready, g, until

def arg(name, default): return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default
SPEEDS = [float(x) for x in arg("--speeds", "12,20,26").split(",")]; SECS = float(arg("--secs", "18"))
ONLY = arg("--only", "")
RESULTS = []
LAST_CARGO = None   # the car course() put on the deck
DUMP = open(arg("--dump", ""), "a") if "--dump" in sys.argv else None   # every sample, a JSON line per run
CONTACTS = "--contacts" in sys.argv   # what the car trailer's body touches, printed each second
TUNE = arg("--tune", "")   # CarTrailer.Probe.Tune on the car trailer before driving

def ride(b):
    try: s = str(b("call CarTrailer.Probe.Ride")["value"])
    except Exception as e: return None
    d = dict(re.findall(r"(\w+) (-?[\d.]+|True|False)", s))
    return {k: (v == "True" if v in ("True", "False") else float(v)) for k, v in d.items()}

def speed(b):
    try: v = [float(x) for x in str(b("get mainscript.s.player.Car.RB.velocity")["value"]).strip("()").split(",")]; return math.hypot(v[0], v[2])
    except Exception: return 0.0

def run(b, label, ms):
    b(f"autodrive on {ms}")
    until(lambda: speed(b) >= ms * 0.9, 40)
    t0 = time.time(); S = []
    while time.time() - t0 < SECS:
        r = ride(b)
        if r: S.append(r)
        if CONTACTS and len(S) % 10 == 0:
            c = str(b("call CarTrailer.Probe.Contacts")["value"])
            if c != "none": print("   contacts:", c[:1500], flush=True)
        time.sleep(0.1)
    b("autodrive on 0"); until(lambda: speed(b) < 1, 30); b("autodrive off")
    if not S: return None
    if DUMP: DUMP.write(json.dumps({"label": label, "ms": ms, "samples": S}) + "\n"); DUMP.flush()
    def sd(k): return statistics.pstdev([s[k] for s in S])
    def mx(k): return max(abs(s[k]) for s in S)
    res = {"label": label, "ms": ms, "n": len(S), "sway_sd": sd("yaw"), "sway_max": mx("yaw"), "roll_sd": sd("roll"), "roll_max": mx("roll"),
           "pitch_sd": sd("pitch"), "bounce_sd": sd("vy"), "bounce_max": mx("vy"), "yawrate_sd": sd("yawrate"), "rollrate_sd": sd("rollrate"),
           "unhitched": sum(1 for s in S if not s["hitched"]), "min_upright": min(s["upright"] for s in S), "speed": statistics.mean(s["speed"] for s in S)}
    print(f"  {label} at {ms:.0f} m/s ({res['speed']:.1f} measured): sway sd {res['sway_sd']:.2f}° max {res['sway_max']:.1f}°, roll sd {res['roll_sd']:.2f}° max {res['roll_max']:.1f}°, "
          f"pitch sd {res['pitch_sd']:.2f}°, bounce sd {res['bounce_sd']:.3f} m/s max {res['bounce_max']:.2f}, yaw rate sd {res['yawrate_sd']:.3f}, roll rate sd {res['rollrate_sd']:.3f}, "
          f"unhitched {res['unhitched']}, min upright {res['min_upright']:.3f}", flush=True)
    RESULTS.append(res)
    return res

def course(b, item, cargo):
    """a fresh car01 and the trailer on a clear stretch, hitched; the player drives"""
    rp = b("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
    r0 = clear_road(b, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
    F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw))); R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
    cx, cz = r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8
    if not car_ready(b, cx, cz, yaw, "player"): sys.exit("NOT IN THE CAR")
    me = b("mp players")["me"]
    tow = min((e for e in b("mp entities")["entities"] if e["part"] < 0 and e["car"]), key=lambda e: math.dist(e["pos"], me))["net"]
    before = {e["net"] for e in b("mp entities")["entities"]}
    print(f" {item}:", b(f"spawncarg {item} 0 {cx - F[0] * 7} {cz - F[1] * 7} {yaw}"), flush=True)
    until(lambda: any(e["net"] not in before and e["part"] < 0 and not e["car"] for e in b("mp entities")["entities"]), 15)
    trailer = next(e["net"] for e in b("mp entities")["entities"] if e["net"] not in before and e["part"] < 0 and not e["car"])
    time.sleep(3)
    b(f"mount grip {trailer}"); print("  hitch:", str(b(f"mount hitch {tow} {trailer}"))[:200], flush=True); time.sleep(3)
    if cargo:
        b("driveout"); time.sleep(2)
        before = {e["net"] for e in b("mp entities")["entities"]}
        b(f"spawncarg 2 0 {cx + R[0] * 8} {cz + R[1] * 8} {yaw}"); time.sleep(3)
        car = next((e["net"] for e in b("mp entities")["entities"] if e["net"] not in before and e["car"] and e["part"] < 0), None)
        global LAST_CARGO; LAST_CARGO = car
        areas = b(f"mount areas {trailer}")["areas"]
        bed = max((a for a in areas if a["name"] == "CarMount"), key=lambda a: a["size"][0] * a["size"][2])
        print("  place:", str(b(f"mount placecar {trailer} {car} {bed['i']} 0.6"))[:200], flush=True); time.sleep(6)
        b(f"mount carlock {car} 1"); time.sleep(2)
        if not sit(b, tow): sys.exit("NOT BACK IN THE TOW CAR")
    b("fuelup"); b("handbrake off")
    return tow, trailer

def sit(b, tow):
    for _ in range(5):
        b(f"mp sitdriver {tow}"); time.sleep(2)
        if g(b, "mainscript.s.player.Bsitting") is True: return True
    return False

START = {}
if __name__ == "__main__":
    with game.ProtectedSession():
        if "--no-build" not in sys.argv: game.build()
        else: game.local_mods()
        game.quit_game(); game.launch(); game.newgame()
        b = Bridge(); time.sleep(3)
        b("set TLDRevamp.DriveLab.Recover False"); b("set settingsscript.s.S.BAutoShift True"); b("set kaposztaleves.s.settings.god True")
        b("mp host 27070"); time.sleep(3)   # the mount/entity commands work on shared objects
        for item, cargo, label in ((53, False, "game trailer"), (9201, False, "car trailer, empty"), (9201, True, "car trailer, car on deck")):
            if ONLY and str(item) != ONLY: continue
            if "--empty-only" in sys.argv and cargo: continue
            if not START:
                START.update(b("gpos")); START["gx"] += float(arg("--offset", "0"))   # --offset: another stretch of road
            b(f"tpground {START['gx']} {START['gz']}"); time.sleep(4)   # every rig on the same stretch of road
            LAST_CARGO = None
            tow, trailer = course(b, item, cargo)
            tune = ",".join(x for x in (TUNE, "contacts=1" if CONTACTS else "") if x)
            if tune and item != 53: print("  tune:", str(b(f"call CarTrailer.Probe.Tune {tune}")["value"])[:300], flush=True)
            for ms in SPEEDS: run(b, label, ms)
            b("driveout"); time.sleep(2)
            for net in (LAST_CARGO, trailer, tow):
                if net is not None: b(f"deleteshare {net}")
            time.sleep(2)
        b("mp stop")
        print("RESULTS " + json.dumps(RESULTS))
        if "--keep" not in sys.argv: game.quit_game()
