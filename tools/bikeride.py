#!/usr/bin/env python3
"""A player riding a motorbike, seen by the other (two machines, relay iad by default). The laptop's player gets on a
bike on the main road and rides it (DriveLab at ~30 km/h); the host keeps alongside. On the host the rider must sit on
the bike (same-frame body-to-bike distance, mp players seatCarDist), the bike upright and moving like the owner's, no
part lost on either machine. Screenshots on both.
  tools/bikeride.py [--no-build] [--relay iad|off] [--bike 141] [--secs 30]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g, seat_idx

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
BIKE = int(sys.argv[sys.argv.index("--bike") + 1]) if "--bike" in sys.argv else 141
SECS = float(sys.argv[sys.argv.index("--secs") + 1]) if "--secs" in sys.argv else 30.0
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
        rp = h("roadpoint"); yaw = rp["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        bx, bz = rp["gx"] + R[0] * 1.8, rp["gz"] + R[1] * 1.8
        L(f"tpground {bx + R[0] * 4} {bz + R[1] * 4}"); time.sleep(2)
        print("bike:", L(f"spawncarg {BIKE} 0 {bx} {bz} {yaw}"), flush=True); time.sleep(3)
        L(f"tpground {bx + R[0] * 1.5} {bz + R[1] * 1.5}"); time.sleep(2)
        me = L("mp players")["me"]
        net = min((e for e in L("mp entities")["entities"] if e["part"] < 0 and e["car"]), key=lambda e: math.dist(e["pos"][::2], me[::2]))["net"]
        print("seats:", L(f"seats {net}"), flush=True)
        for _ in range(4):
            try: L(f"sitseat {seat_idx(L)} redirect net={net}")
            except Exception as ex: print("  sit:", ex, flush=True)
            time.sleep(1)
            if g(L, "mainscript.s.player.Bsitting") is True: break
        L("fuelup")
        if g(L, "mainscript.s.player.Bsitting") is not True: sys.exit("LAPTOP NOT ON THE BIKE")
        until(lambda: any(e["net"] == net for e in h("mp entities")["entities"]), 20)
        def row(x): return next((e for e in x("mp entities")["entities"] if e["net"] == net), None)
        parts0 = (row(L)["attached"], row(h)["attached"]); print("parts (laptop, host):", parts0, flush=True)
        def census(x): return {(r[0], r[1]) for r in x(f"mount slots {net}")["slots"] if r[2]}
        c0 = {"laptop": census(L), "host": census(h)}
        h(f"tpground {bx - R[0] * 6} {bz - R[1] * 6}"); time.sleep(2); h(f"mp lookat {net}"); h("shot bike_parked"); L("shot bike_parked")
        print("rides:", L("autodrive on 8.3"), flush=True)
        ds, ups_h, ups_l, spd, t0, shots = [], [], [], [], time.monotonic(), 0
        while time.monotonic() - t0 < SECS:
            e = row(h); le = row(L)
            if e:
                h(f"tpground {e['pos'][0] - R[0] * 7} {e['pos'][2] - R[1] * 7}")   # alongside
                for p in h("mp players")["players"]:
                    if p["seatNet"] == net and p.get("seatCarDist", -1) >= 0: ds.append(p["seatCarDist"])
                ups_h.append(e["upy"]); spd.append(math.hypot(*e["vel"][::2]) * 3.6 if "vel" in e else 0)
            if le: ups_l.append(le["upy"])
            if time.monotonic() - t0 > SECS * (shots + 1) / 3 and shots < 2:
                shots += 1; time.sleep(0.5); h(f"mp lookat {net}"); h(f"shot bike_ride{shots}"); L(f"shot bike_ride{shots}")
            time.sleep(1.0)
        c1 = {"laptop": census(L), "host": census(h)}
        L("autodrive off"); L("brakecar"); time.sleep(3)
        c2 = {"laptop": census(L), "host": census(h)}
        for n in ("laptop", "host"):
            print(f"  {n}: off while riding {sorted(c0[n] - c1[n])}, off at the brake {sorted(c1[n] - c2[n])}", flush=True)
        print(f"  seat distances on the host: n {len(ds)}, max {max(ds) if ds else None}; host's speeds {[round(x) for x in spd]}", flush=True)
        print(f"  upY host min {min(ups_h) if ups_h else None}, laptop min {min(ups_l) if ups_l else None}", flush=True)
        parts1 = (row(L)["attached"], row(h)["attached"])
        verdict("THE RIDER SITS ON THE BIKE (host's view)", ds and max(ds) < 1.5 and len(ds) >= SECS * 0.6, f"body-to-bike max {max(ds) if ds else None} m over {len(ds)} samples")
        verdict("IT RIDES", spd and max(spd) > 15, f"host's copy peak {max(spd) if spd else 0:.0f} km/h")
        verdict("UPRIGHT ON BOTH", ups_h and ups_l and min(ups_h) > 0.6 and min(ups_l) > 0.6, f"min upY host {min(ups_h) if ups_h else None} laptop {min(ups_l) if ups_l else None}")
        verdict("NO PART LOST", parts1 == parts0, f"before {parts0} after {parts1}")
        er = [x("errors") for x in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        try: L(f"set settingsscript.s.S.BAutoShift {shift_l}")
        except Exception as ex: print("shift restore", ex)
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
