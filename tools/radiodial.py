#!/usr/bin/env python3
"""Radio dials hard to turn as a passenger at speed (playtest 2026-10-09/10 (d)). Two machines over the relay: the host
drives its car at --kmh, the laptop rides as a passenger in its copy; on the laptop, every frame, the game's look ray
from the camera towards the tuning dial as it is drawn (Diagnostics.AimLab): how often it hits the dial, and how far
the drawn dial is from its collider. Parked first (the reference), then at speed.
  tools/radiodial.py [--no-build] [--relay iad|off] [--kmh 100] [--secs 12] [--old: copies interpolated while aboard]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import g, car_ready, clear_road

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD, KMH, SECS = arg("--relay", "iad"), "--old" in sys.argv, arg("--kmh", 100.0, float), arg("--secs", 12.0, float)
T = "TLDRevamp.Net.Entities"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}

def measure(L, car, secs):
    r = L(f"aimlab start {car}")
    if "error" in r: return r
    time.sleep(secs)
    return L("aimlab stop")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    gods = {}; shift_h = g(h, "settingsscript.s.S.BAutoShift")
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
            s_(f"set {T}.AboardDrawnAtPhysics {not OLD}")
        print(f"  {'OLD (the copy interpolated while aboard)' if OLD else 'CURRENT BUILD'}", flush=True)
        h("set settingsscript.s.S.BAutoShift True")
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
        R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        if not car_ready(h, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        me = h("mp players")["me"]
        car = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
        L(f"tpground {me[0] + R[0] * 3} {me[2] + R[1] * 3}"); time.sleep(4)
        for _ in range(3):
            print("  passenger:", L(f"mp sitpassenger {car}"), flush=True); time.sleep(2)
            if g(L, "mainscript.s.player.Bsitting") is True: break
        verdict("SETUP: THE LAPTOP RIDES ALONG", g(L, "mainscript.s.player.Bsitting") is True)
        park = measure(L, car, 5)
        print("  parked:", park, flush=True)
        if "error" in park: sys.exit(f"no dial: {park}")
        verdict("PARKED: THE DIAL CAN BE HIT (reference)", park["frames"] > 20 and park["hits"] / park["frames"] > 0.95, f"{park['hits']}/{park['frames']}, gap max {park['gapMax']} m")
        h(f"autodrive on {KMH / 3.6:.1f}")
        t0 = time.monotonic(); v = 0.0
        while time.monotonic() - t0 < 40:
            v = abs(float(g(h, "mainscript.s.player.Car.speed") or 0))
            if v > KMH * 0.85: break
            time.sleep(0.5)
        print(f"  at {v:.0f} km/h", flush=True)
        run = measure(L, car, SECS)
        v2 = abs(float(g(h, "mainscript.s.player.Car.speed") or 0))
        h("autodrive off")
        print(f"  at speed ({v:.0f}-{v2:.0f} km/h):", run, flush=True)
        verdict(f"AT {KMH:.0f} KM/H: THE DIAL CAN BE HIT AS IT IS SEEN", run["frames"] > 20 and run["hits"] / run["frames"] > 0.95,
                f"{run['hits']}/{run['frames']} frames, the drawn dial {run['gapAvg']} m (max {run['gapMax']}) from its collider; aboard frames {L(f'get {T}.AboardFrames')['value']}")
        er = [x_("errors") for x_ in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er], flush=True)
    finally:
        try: h("autodrive off"); L("aimlab stop")
        except Exception: pass
        for s_, v_ in gods.values():
            try: s_(f"set kaposztaleves.s.settings.god {v_}"); s_(f"set {T}.AboardDrawnAtPhysics True")
            except Exception: pass
        h(f"set settingsscript.s.S.BAutoShift {shift_h}")
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"RADIODIAL: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")
