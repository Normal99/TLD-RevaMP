#!/usr/bin/env python3
"""A Wi-Fi stall seen from a passenger (two machines, relay iad by default). The host drives at --kmh; the laptop rides
along. Its game holds every message from the host for --stall s (`mp stall`: what a Wi-Fi stall looks like there),
then gets them all at once. Measured in-game, every frame: how the car moves on the laptop's screen (shown speed;
'held' = frames nearly stopped) — a 4 s window with no stall (baseline) vs a 4 s window with one.
  --lag MS: instead of a stall, a lasting extra delay (`mp lag`: a new, longer route) — 6 s after it starts, 6 s after it ends
  tools/stallcar.py [--no-build] [--relay iad|off] [--kmh 70] [--stall 1.0] [--lag 600] [--rounds 3] [--tag x]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g, car_ready, clear_road

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, KMH, STALL, ROUNDS, TAG = arg("--relay", "iad"), arg("--kmh", 70.0, float), arg("--stall", 1.0, float), arg("--rounds", 3, int), arg("--tag", "")
LAG = arg("--lag", 0, int)
lagrows = []
L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    gods = {}; shift_h = g(h, "settingsscript.s.S.BAutoShift"); rows = []
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        h("set settingsscript.s.S.BAutoShift True")
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        if not car_ready(h, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        me = h("mp players")["me"]
        car = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
        until(lambda: any(e["net"] == car for e in L("mp entities")["entities"]), 30)
        L(f"tpground {me[0] + R[0] * 3} {me[2] + R[1] * 3}"); time.sleep(2)
        for _ in range(3):
            L(f"mp sitpassenger {car}"); time.sleep(2)
            if g(L, "mainscript.s.player.Bsitting") is True: break
        print("laptop rides along:", g(L, "mainscript.s.player.Bsitting"), flush=True)
        h(f"autodrive on {KMH / 3.6:.1f}")
        until(lambda: abs(float(g(h, "mainscript.s.player.Car.speed") or 0)) > KMH * 0.85, 45, 0.5)
        for rnd in range(1, ROUNDS + 1):
            L(f"mp copymotion {car} reset"); time.sleep(4); base = L(f"mp copymotion {car}")
            if LAG:
                L(f"mp copymotion {car} reset"); L(f"mp lag {LAG}"); time.sleep(6); st = L(f"mp copymotion {car}")
                print(f"    lag on: {st} | interp {L(f'mp interp {car}').get('delayMs')} ms", flush=True)
                L(f"mp copymotion {car} reset"); L("mp lag 0"); time.sleep(6); off = L(f"mp copymotion {car}")
                print(f"    lag off: {off} | interp {L(f'mp interp {car}').get('delayMs')} ms", flush=True)
                lagrows.append((st, off))
            else:
                L(f"mp copymotion {car} reset"); time.sleep(0.5); L(f"mp stall {STALL}"); time.sleep(3.5); st = L(f"mp copymotion {car}")
            v = abs(float(g(h, "mainscript.s.player.Car.speed") or 0))
            print(f"  round {rnd} at {v:.0f} km/h — no stall: {base} | {STALL} s stall: {st}", flush=True)
            rows.append((base, st))
        h("autodrive off")
        er = [x_("errors") for x_ in (h, L)]
        print("errors", [(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er], flush=True)
    finally:
        try: h("autodrive off")
        except Exception: pass
        for s_, v in gods.values(): s_(f"set kaposztaleves.s.settings.god {v}")
        h(f"set settingsscript.s.S.BAutoShift {shift_h}")
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
if LAG:
    print(f"\nLAG {LAG} ms RESULT {TAG}: held frames / max shown speed (m/s), car at {KMH / 3.6:.1f} m/s")
    for i, (a_, o_) in enumerate(lagrows, 1):
        print(f"  round {i}: lag on: held {a_.get('held')} min {a_.get('min')} p50 {a_.get('p50')} max {a_.get('max')} | lag off: held {o_.get('held')} min {o_.get('min')} p50 {o_.get('p50')} max {o_.get('max')}")
print(f"\nRESULT {TAG}: held frames / min shown speed (m/s), car at {KMH / 3.6:.1f} m/s")
for i, (b, s) in enumerate(rows, 1):
    print(f"  round {i}: no stall held {b.get('held')} min {b.get('min')} p10 {b.get('p10')} max {b.get('max')} | stall held {s.get('held')} min {s.get('min')} p10 {s.get('p10')} max {s.get('max')}")
