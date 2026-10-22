#!/usr/bin/env python3
"""Trailer sway, the standard way: towed at a steady speed, the trailer gets a sideways kick (its turn rate changed
by --kick °/s, CarTrailer.Probe.Kick) and its angle to the car is sampled ten times a second for --secs; how fast the
swing dies out (each swing against the one before: under 1 dies out, over 1 grows) and its largest angle. The game's
own trailer (53) first as the yardstick, then the car trailer (9201) empty and with a car01 on the deck, each with
every --variants setting (CarTrailer.Probe.Tune; "" = as built).
  TLD_TEST_MODS=CarTrailer tools/cartrailersway.py [--no-build] [--speeds 20,26] [--kick 25] [--secs 9]
      [--variants ";mass=500,spring=40000,damper=3500"] [--only 9201] [--cargo-only] [--keep]"""
import os
os.environ.setdefault("TLD_TEST_MODS", "CarTrailer")
import sys, time, math, pathlib, json
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
from roadkit import until, g
import cartrailerride as R   # course(), ride(), speed(), sit()

def arg(name, default): return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default
SPEEDS = [float(x) for x in arg("--speeds", "20,26").split(",")]; KICK = float(arg("--kick", "25")); SECS = float(arg("--secs", "9"))
VARIANTS = arg("--variants", "").split(";")
ONLY = arg("--only", "")
RESULTS = []
DUMP = open(arg("--dump", ""), "a") if "--dump" in sys.argv else None   # every sample, a JSON line per run

def peaks(ys):
    """the turning points of a swing (sign changes of its slope), as (index, value)"""
    out = []
    for i in range(1, len(ys) - 1):
        if (ys[i] - ys[i - 1]) * (ys[i + 1] - ys[i]) < 0 and abs(ys[i]) > 0.3: out.append((i, ys[i]))
    return out

def kick_run(b, label, ms, variant):
    b(f"autodrive on {ms}")
    until(lambda: R.speed(b) >= ms * 0.92, 45)
    time.sleep(4)
    base = [R.ride(b) for _ in range(10)]; base = [s for s in base if s]
    y0 = sum(s["yaw"] for s in base) / max(1, len(base))
    print("  kick:", b(f"call CarTrailer.Probe.Kick {KICK}")["value"], flush=True)
    t0 = time.time(); S = []
    while time.time() - t0 < SECS:
        r = R.ride(b)
        if r: S.append(r)
        time.sleep(0.1)
    b("autodrive on 0"); until(lambda: R.speed(b) < 1, 30); b("autodrive off")
    ys = [s["yaw"] - y0 for s in S]
    # the swing alone: the angle less its 1 s running mean (a bend in the road turns the trailer too, slowly)
    hp = [ys[i] - sum(ys[max(0, i - 5):i + 6]) / len(ys[max(0, i - 5):i + 6]) for i in range(len(ys))]
    def rms(v): return math.sqrt(sum(x * x for x in v) / len(v)) if v else 0.0
    early, late = rms(hp[:30]), rms(hp[-30:])
    P = peaks(ys)
    amps = [abs(v) for _, v in P]
    ratios = [amps[i + 1] / amps[i] for i in range(len(amps) - 1) if amps[i] > 0.5]
    decay = (sum(ratios) / len(ratios)) if ratios else 0.0
    period = (2 * (P[-1][0] - P[0][0]) / (len(P) - 1) * 0.1) if len(P) > 1 else 0.0
    res = {"label": label, "variant": variant or "as built", "ms": ms, "speed": sum(s["speed"] for s in S) / max(1, len(S)),
           "max": max(map(abs, ys)) if ys else 0, "swing_early": early, "swing_late": late, "last": max(map(abs, ys[-20:])) if ys else 0, "decay": decay, "swings": len(P), "period": period,
           "roll_max": max(abs(s["roll"]) for s in S) if S else 0, "min_upright": min(s["upright"] for s in S) if S else 0, "unhitched": sum(1 for s in S if not s["hitched"])}
    print(f"  {label} [{res['variant']}] at {ms:.0f} m/s ({res['speed']:.1f}): kick max {res['max']:.1f}°, last 2 s {res['last']:.1f}°, swing rms first 3 s {early:.2f}° last 3 s {late:.2f}°, swing ratio {decay:.2f} "
          f"({len(P)} turning points, period {period:.2f} s), roll max {res['roll_max']:.1f}°, min upright {res['min_upright']:.3f}, unhitched {res['unhitched']}", flush=True)
    RESULTS.append(res)
    if DUMP: DUMP.write(json.dumps({"label": label, "variant": variant, "ms": ms, "y0": y0, "samples": S}) + "\n"); DUMP.flush()

with game.ProtectedSession():
    if "--no-build" not in sys.argv: game.build()
    else: game.local_mods()
    game.quit_game(); game.launch(); game.newgame()
    b = Bridge(); time.sleep(3)
    b("set TLDRevamp.DriveLab.Recover False"); b("set settingsscript.s.S.BAutoShift True"); b("set kaposztaleves.s.settings.god True")
    b("mp host 27070"); time.sleep(3)
    courses = [(53, False, "game trailer", "")]
    courses += [(9201, False, "car trailer, empty", v) for v in VARIANTS]
    courses += [(9201, True, "car trailer, car on deck", v) for v in VARIANTS]
    if "--cargo-only" in sys.argv: courses = [c for c in courses if c[1]]
    start = b("gpos")
    for item, cargo, label, variant in courses:
        if ONLY and str(item) != ONLY: continue
        R.LAST_CARGO = None
        b(f"tpground {start['gx']} {start['gz']}"); time.sleep(4)   # every rig on the same stretch of road
        tow, trailer = R.course(b, item, cargo)   # a new rig where the last one stopped (on the road)
        print("  tow car mass:", g(b, "mainscript.s.player.Car.RB.mass"), flush=True)
        if variant: print("  tune:", str(b(f"call CarTrailer.Probe.Tune {variant}")["value"])[:400], flush=True)
        for ms in SPEEDS: kick_run(b, label, ms, variant)
        b("driveout"); time.sleep(2)
        for net in (R.LAST_CARGO, trailer, tow):
            if net is not None: print("  remove:", b(f"deleteshare {net}"), flush=True)
        time.sleep(2)
    b("mp stop")
    print("RESULTS " + json.dumps(RESULTS))
    if "--keep" not in sys.argv: game.quit_game()
