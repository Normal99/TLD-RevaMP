#!/usr/bin/env python3
"""Bailing out of a moving car (two machines, relay iad by default). The laptop's player drives at ~50 km/h, lets go and
gets out (the game's own GetOut). The empty car rolls on and stops; the player lands beside the road. Both machines
must agree: where the car comes to rest, where the player stands, every part still on. Screenshots on both.
  tools/bailout.py [--no-build] [--relay iad|off] [--kmh 50]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g, car_ready, clear_road

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
KMH = float(sys.argv[sys.argv.index("--kmh") + 1]) if "--kmh" in sys.argv else 50.0
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    gods = {}; shift_l = g(L, "settingsscript.s.S.BAutoShift")
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        L("set settingsscript.s.S.BAutoShift True")
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(L, rp["gx"], rp["gz"], F[0], F[1])
        yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        if not car_ready(L, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "laptop"): sys.exit("LAPTOP NOT IN ITS CAR")
        me = L("mp players")["me"]
        net = min((e for e in L("mp entities")["entities"] if e["part"] < 0 and e["car"]), key=lambda e: math.dist(e["pos"][::2], me[::2]))["net"]
        until(lambda: any(e["net"] == net for e in h("mp entities")["entities"]), 20)
        def row(x): return next((e for e in x("mp entities")["entities"] if e["net"] == net), None)
        def census(x): return {(r[0], r[1]) for r in x(f"mount slots {net}")["slots"] if r[2]}
        c0 = {"laptop": census(L), "host": census(h)}
        L(f"autodrive on {KMH / 3.6:.1f}")
        until(lambda: abs(float(g(L, "mainscript.s.player.Car.speed") or 0)) > KMH * 0.85, 45, 0.5)
        for _ in range(3):   # the host keeps up, beside the road
            e = row(h); h(f"tpground {e['pos'][0] - R[0] * 12} {e['pos'][2] - R[1] * 12}"); time.sleep(1.5)
        v = abs(float(g(L, "mainscript.s.player.Car.speed") or 0))
        L("autodrive off"); print("bail out at", round(v), "km/h:", L("driveout"), flush=True)
        # at rest on its OWNER's machine (an empty car may be taken over, and it can roll on down a slope: run 2
        # accelerated 1 -> 6.6 m/s after a slow moment, and a single slow sample read as "at rest")
        t0, still = time.monotonic(), 0
        while time.monotonic() - t0 < 60 and still < 4:
            el, eh = row(L), row(h); own = eh if not eh["proxy"] else el
            if el and eh and math.hypot(*own["vel"]) < 0.1: still += 1
            else: still = 0
            h(f"tpground {eh['pos'][0] - R[0] * 9} {eh['pos'][2] - R[1] * 9}") if eh else None   # the host keeps up
            if int(time.monotonic() - t0) % 10 == 0:
                cars = lambda x: [{k: c[k] for k in ("kmh", "engine", "gear", "handbrake", "throttle", "driven")} for c in x("carsnear 60")["cars"] if "Car01" in c["name"]]
                print(f"  +{time.monotonic() - t0:.0f}s owner {'host' if not eh['proxy'] else 'laptop'}; host {cars(h)} | laptop {cars(L)}", flush=True)
            time.sleep(1)
        print(f"  the car stopped after {time.monotonic() - t0:.0f} s (still {still})", flush=True)
        time.sleep(4)
        e = row(h); h(f"tpground {e['pos'][0] - R[0] * 9} {e['pos'][2] - R[1] * 9}"); time.sleep(3)
        # an empty car in neutral can swing back and forth in a dip for minutes (run 4: 24 km/h after a minute; single
        # player's stopped dead on a rise — asleep): compare the two machines whenever the owner's car is nearly still
        still_gaps = []
        t1 = time.monotonic()
        while time.monotonic() - t1 < 40 and len(still_gaps) < 3:
            el, eh = row(L), row(h); own = eh if not eh["proxy"] else el
            if math.hypot(*own["vel"]) < 0.15: still_gaps.append(math.dist(el["pos"], eh["pos"]))
            time.sleep(0.3)
        print(f"  gaps at nearly-still moments: {[round(x, 3) for x in still_gaps]}", flush=True)
        el, eh = row(L), row(h)
        car_gap = min(still_gaps) if still_gaps else math.dist(el["pos"], eh["pos"])
        lp = L("mp players")["me"]; hp = next((p for p in h("mp players")["players"] if p["id"] == 1), None)
        body_gap = math.dist(lp, hp["shown"]) if hp else None
        rolled = math.dist(el["pos"][::2], (r0["gx"], r0["gz"]))
        print(f"  car at rest {rolled:.0f} m down the road; owner {el.get('owner')} (laptop proxy {el['proxy']}, host proxy {eh['proxy']}); upY {el['upy']} / {eh['upy']}", flush=True)
        print(f"  laptop's player sitting {g(L, 'mainscript.s.player.Bsitting')}, at {lp}; host sees {hp and hp['shown']} seated {hp and hp['seated']}", flush=True)
        h(f"mp lookat {net}"); h("shot bailout_host"); L(f"mp lookat {net}"); L("shot bailout_laptop")
        c1 = {"laptop": census(L), "host": census(h)}
        verdict("THE EMPTY CAR IS IN THE SAME PLACE ON BOTH (when still)", car_gap < 0.3, f"{car_gap:.3f} m apart" + ("" if still_gaps else " (never still: a moving sample)"))
        verdict("THE PLAYER STANDS IN THE SAME PLACE ON BOTH", body_gap is not None and body_gap < 1.0 and not hp["seated"], f"{body_gap if body_gap is None else round(body_gap, 2)} m apart")
        verdict("NOTHING FELL OFF", c0 == c1, f"lost laptop {sorted(c0['laptop'] - c1['laptop'])} host {sorted(c0['host'] - c1['host'])}")
        er = [x("errors") for x in (h, L)]
        verdict("NO ERRORS", all(x["errors"] == 0 and x["exceptions"] == 0 for x in er), str([(x["errors"], x["exceptions"], (x.get('kinds') or [])[:3]) for x in er]))
    finally:
        try: L(f"set settingsscript.s.S.BAutoShift {shift_l}")
        except Exception as ex: print("shift restore", ex)
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
