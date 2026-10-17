#!/usr/bin/env python3
"""Cargo seen by a passenger (two machines, relay iad by default). The host drives a car with two of its items on the
back seats (a lamp, a radio); the laptop rides in the front passenger seat. On the laptop the cargo must stay where
it is in the car — each item where the host's game has it, in the car's frame — not trailing behind, floating
outside or jittering at speed. Sampled every ~0.4 s over a 30 s drive at --kmh; screenshots from the passenger seat.
  tools/cargoride.py [--no-build] [--relay iad|off] [--kmh 60] [--items 320,39]"""
import sys, time, math, pathlib, statistics
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, g, car_ready, clear_road

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
KMH = float(sys.argv[sys.argv.index("--kmh") + 1]) if "--kmh" in sys.argv else 60.0
ITEMS = [int(x) for x in (sys.argv[sys.argv.index("--items") + 1] if "--items" in sys.argv else "320,39").split(",")]
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def inside(p): return abs(p[0]) < 1.3 and -0.6 < p[1] < 2.0 and abs(p[2]) < 3.0   # in the car's box (car frame, m)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    gods = {}; shift_h = g(h, "settingsscript.s.S.BAutoShift")
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        h("set settingsscript.s.S.BAutoShift True")
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        if not car_ready(h, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        me = h("mp players")["me"]
        car = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
        nets = []
        for k, item in enumerate(ITEMS):
            before = {e["net"] for e in h("mp entities")["entities"]}
            print(f"item {item}:", h(f"devspawn {item}"), flush=True)
            until(lambda: any(e["net"] not in before and not e["car"] for e in h("mp entities")["entities"]), 15)
            n = next(e["net"] for e in h("mp entities")["entities"] if e["net"] not in before and not e["car"])
            print("  on rear seat", k, h(f"mp putincar {car} {n} {k}"), flush=True); nets.append(n)
        time.sleep(4)
        ids = " ".join(map(str, nets))
        until(lambda: all(str(n) in L(f"mp relpos {car} {ids}")["items"] for n in nets), 30)
        L(f"tpground {me[0] + R[0] * 3} {me[2] + R[1] * 3}"); time.sleep(2)
        for _ in range(3):
            print("  passenger:", L(f"mp sitpassenger {car}"), flush=True); time.sleep(2)
            if g(L, "mainscript.s.player.Bsitting") is True: break
        verdict("THE LAPTOP RIDES ALONG", g(L, "mainscript.s.player.Bsitting") is True)
        hp0 = h(f"mp relpos {car} {ids}")["items"]
        verdict("THE CARGO SITS IN THE CAR (host)", len(hp0) == len(nets) and all(inside(p) for p in hp0.values()), str(hp0))
        h(f"autodrive on {KMH / 3.6:.1f}")
        diffs = {n: [] for n in nets}; out_l = []; out_h = []; t0 = time.monotonic(); shot = False; vmax = 0
        while time.monotonic() - t0 < 30:
            a = L(f"mp relpos {car} {ids}")["items"]; b = h(f"mp relpos {car} {ids}")["items"]
            vmax = max(vmax, abs(float(g(h, "mainscript.s.player.Car.speed") or 0)))
            for n in nets:
                k = str(n)
                if k in a and k in b:
                    diffs[n].append(math.dist(a[k], b[k]))
                    if not inside(a[k]): out_l.append((round(time.monotonic() - t0, 1), n, a[k]))
                    if not inside(b[k]): out_h.append((round(time.monotonic() - t0, 1), n, b[k]))
            if not shot and time.monotonic() - t0 > 15:
                L(f"mp lookat {nets[0]}"); time.sleep(0.3); L("shot cargoride_laptop"); shot = True
            time.sleep(0.2)
        h("autodrive off")
        print(f"  top speed {vmax:.0f} km/h", flush=True)
        for n in nets:
            d = diffs[n]
            print(f"  item {n}: {len(d)} samples, laptop vs host in the car's frame: median {statistics.median(d):.3f} m, p90 {sorted(d)[int(len(d) * 0.9)]:.3f}, max {max(d):.3f}" if d else f"  item {n}: no samples", flush=True)
        verdict(f"DRIVEN AT ~{KMH:.0f} KM/H", vmax > KMH * 0.75, f"top {vmax:.0f} km/h")
        verdict("THE CARGO STAYED IN THE CAR (host's game)", not out_h, str(out_h[:4]))
        verdict("THE PASSENGER SEES IT IN THE CAR", not out_l, str(out_l[:4]))
        verdict("THE PASSENGER SEES IT WHERE THE HOST HAS IT", all(d and statistics.median(d) < 0.15 and max(d) < 0.5 for d in diffs.values()),
                "; ".join(f"{n}: median {statistics.median(d):.3f} max {max(d):.3f}" for n, d in diffs.items() if d))
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        try: h("autodrive off")
        except Exception: pass
        for s_, v in gods.values(): s_(f"set kaposztaleves.s.settings.god {v}")
        h(f"set settingsscript.s.S.BAutoShift {shift_h}")
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
