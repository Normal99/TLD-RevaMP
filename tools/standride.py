#!/usr/bin/env python3
"""A player standing on a vehicle another player drives (roof / bed; two machines, relay iad by default). The laptop's
player stands on the host's car; the host drives off gently to --kmh. Every ~0.4 s, as drawn (LateUpdate):
  - the laptop's game: is its player still on the car (the game's own physics: reported, the reference)
  - the host's game: the laptop's body in the car's frame vs where the laptop has itself — the same place on both
Screenshots from the host looking at the laptop's player. God mode on both (a fall), restored after.
  tools/standride.py [--no-build] [--relay iad|off] [--kmh 30] [--secs 15] [--aboard-interp: before v0.66.8]"""
import sys, time, math, pathlib, statistics
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g, car_ready, clear_road

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, KMH, SECS = arg("--relay", "iad"), arg("--kmh", 30.0, float), arg("--secs", 15.0, float)
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def speed(x): return abs(float(g(x, "mainscript.s.player.Car.speed") or 0))
def stop_gently(x):
    x("autodrive on 0"); until(lambda: speed(x) < 3, 20, 0.3); x("autodrive off"); x("brakecar"); x("handbrake on")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    if "--aboard-interp" in sys.argv:   # A/B: the car the laptop stands on interpolated as before v0.66.8
        L("set TLDRevamp.Net.Entities.AboardDrawnAtPhysics False"); print("  OLD: the car stood on is interpolated", flush=True)
    saved = []
    try:
        for s_ in (h, L):
            saved.append((s_, "kaposztaleves.s.settings.god", g(s_, "kaposztaleves.s.settings.god"))); s_("set kaposztaleves.s.settings.god True")
            saved.append((s_, "settingsscript.s.S.BAutoShift", g(s_, "settingsscript.s.S.BAutoShift"))); s_("set settingsscript.s.S.BAutoShift True")
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        if not car_ready(h, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        me = h("mp players")["me"]
        car = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
        h("handbrake on")
        L(f"tpground {me[0] + R[0] * 4} {me[2] + R[1] * 4}"); time.sleep(2)
        until(lambda: any(e["net"] == car for e in L("mp entities")["entities"]), 20)
        lid = None
        until(lambda: h("mp players")["players"], 10); lid = h("mp players")["players"][0]["id"]
        if "--probe" in sys.argv:
            print("  probe, on the road:", L("mp footprobe"), flush=True)
            print("  stands on the host's car:", L(f"mp standon {car} 0.15"), flush=True); time.sleep(3)
            print("  probe, on the host's car (a copy here):", L("mp footprobe"), flush=True)
            L(f"tpground {me[0] + R[0] * 9} {me[2] + R[1] * 9}"); time.sleep(2)
            before = {e["net"] for e in L("mp entities")["entities"]}
            print("  laptop spawns its own car:", L("devspawn 2"), flush=True); time.sleep(4)
            own = next((e["net"] for e in L("mp entities")["entities"] if e["net"] not in before and e["car"] and e["part"] < 0), None)
            if own:
                print("  stands on its own car:", L(f"mp standon {own} 0.15"), flush=True); time.sleep(3)
                print("  probe, on its own car:", L("mp footprobe"), flush=True)
            raise SystemExit(0)
        print("  laptop stands on the car:", L(f"mp standon {car} 0.15"), flush=True); time.sleep(3)
        L(f"mp relplayers {car}"); h(f"mp relplayers {car}"); time.sleep(0.3)
        a0 = L(f"mp relplayers {car}")["late"].get("-1"); b0 = h(f"mp relplayers {car}")["late"].get(str(lid))
        print(f"  parked: laptop has itself at {a0} in the car, the host shows it at {b0}", flush=True)
        print("  laptop's footing:", g(L, "TLDRevamp.Net.Entities.FootWhy"), "| sensor sensed", g(L, "mainscript.s.player.grounded.sensed"), "T", g(L, "mainscript.s.player.grounded.T.name"), flush=True)
        h("mp lookplayer 1"); time.sleep(0.4); h("shot standride_parked")
        verdict("PARKED: ON THE CAR, SAME PLACE ON BOTH", a0 and b0 and a0[1] > 0.5 and math.dist(a0, b0) < 0.3, f"{math.dist(a0, b0) if a0 and b0 else -1:.2f} m apart")
        # drive off gently
        h("handbrake off"); h(f"autodrive on {KMH / 3.6:.1f}")
        diffs, onbar, samples, sp, worst = [], [], [], [], None
        t0 = time.monotonic(); shot = False
        while time.monotonic() - t0 < SECS:
            a = L(f"mp relplayers {car}")["late"].get("-1"); b = h(f"mp relplayers {car}")["late"].get(str(lid))
            sp.append(speed(h)); t = round(time.monotonic() - t0, 1)
            if a: onbar.append((t, a[1] > 0.5 and abs(a[0]) < 1.5 and abs(a[2]) < 3.0))
            if a and b:
                d = math.dist(a, b); diffs.append(d); samples.append((t, round(sp[-1]), a, b))
                if worst is None or d > worst[0]: worst = (d, t, a, b)
            if len(sp) % 10 == 5: print(f"  t {t}: laptop's footing: {g(L, 'TLDRevamp.Net.Entities.FootWhy')}", flush=True)
            if not shot and sp[-1] > KMH * 0.8: h("mp lookplayer 1"); time.sleep(0.3); h("shot standride_moving"); shot = True
            time.sleep(0.4)
        off = [t for t, ok in onbar if not ok]
        print(f"  speeds {min(sp):.0f}-{max(sp):.0f} km/h; laptop off the car at {off[:6]}", flush=True)
        print(f"  host vs laptop, the laptop's player in the car's frame: median {statistics.median(diffs) if diffs else -1:.2f} m, max {max(diffs, default=-1):.2f} m, n {len(diffs)}; worst {worst}", flush=True)
        print("  samples (t, km/h, laptop, host):", samples[::4], flush=True)
        verdict("RIDING (laptop's game): THE PLAYER STAYS ON THE CAR", onbar and not off, f"off at {off[:6]}")
        verdict("RIDING: THE HOST SHOWS IT WHERE IT IS ON THE CAR", diffs and statistics.median(diffs) < 0.3 and max(diffs) < 0.8, f"median {statistics.median(diffs) if diffs else -1:.2f} m, max {max(diffs, default=-1):.2f} m")
        print("  footing: laptop sent", g(L, "TLDRevamp.Net.Entities.FootingSent"), "| host frames shown on the car", g(h, "TLDRevamp.Net.RemotePlayers.FootShownFrames"), flush=True)
        stop_gently(h)
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        for s_ in (h, L):
            try: s_("autodrive off")
            except Exception: pass
        for s_, k, v in saved: s_(f"set {k} {v}")
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
