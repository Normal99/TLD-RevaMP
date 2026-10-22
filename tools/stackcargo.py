#!/usr/bin/env python3
"""Cargo from the side that doesn't own the car (two machines, relay iad by default; review 2026-10-22 #21/#22).
The host has a pickup; the laptop (the joiner) does the breaking:
  S  STACK: the laptop puts a jerrycan in the host's bed and an oil can on top of it. On the host both must become the
     host's (the car's owner simulates what lies in it) and neither may stay a "ghost" (a copy with no collisions). Then
     the host drives off: both still in the bed on both machines. (#21: the can on the can was never the car's — the
     first thing under it was the jerrycan — and stayed a ghost for good, left behind on the road.)
  B  BESIDE: the laptop puts an oil can on the ground just beside the parked pickup's body. On the host it is no ghost:
     it overlaps nothing of the car. (#21: anything inside the pickup's 3.4 m box was one.)
  R  ROADSIDE: the host drives past the laptop's items lying beside the road, an oil can riding in its bed. They stay
     the laptop's (#22: the can riding at speed counted as a moving item and took anything within ~1.35 m of it).
A/B: TLD_TEST_SETS="Net.Entities.SurfaceSkipsItems=False;Net.Entities.CargoGhostOnlyOverlapping=False;Net.Entities.RideRelative=False"
is the build before the fixes.
  tools/stackcargo.py [--no-build] [--relay iad|off] [--kmh 60] [--only S,B,R]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, g, clear_road, seat_idx

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
KMH = float(sys.argv[sys.argv.index("--kmh") + 1]) if "--kmh" in sys.argv else 60.0
ONLY = (sys.argv[sys.argv.index("--only") + 1] if "--only" in sys.argv else "S,B,R").split(",")
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def inside(p): return p is not None and abs(p[0]) < 1.3 and -0.6 < p[1] < 2.0 and abs(p[2]) < 3.2   # car frame, m

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    hid, lid = h("mp entities")["me"], L("mp entities")["me"]
    def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
    def rel(s, car, *nets): return {int(k): v for k, v in s(f"mp relpos {car} {' '.join(map(str, nets))}")["items"].items()}
    def item_id(frag):
        hits = h(f"itemids {frag}")["items"]
        return next((i for i, n in hits if n == frag), hits[0][0] if hits else None)
    def spawn(s, name):
        before = set(ents(s))
        print(f"  spawn {name}:", s(f"devspawn {name}"), flush=True)
        until(lambda: any(n not in before and not e["car"] for n, e in ents(s).items()), 15)
        return next(n for n, e in ents(s).items() if n not in before and not e["car"])
    gods = {}; shift_h = g(h, "settingsscript.s.S.BAutoShift")
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        h("set settingsscript.s.S.BAutoShift True")
        pick = next((i for i, n in h("itemids Car06P")["items"] if n.lower().startswith("car06p")), None)
        print("  pickup id:", pick, h("itemids Car06P"), flush=True)
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw))); R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        cx, cz = r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8
        print("  host spawns the pickup:", h(f"spawncarg {pick} 0 {cx} {cz} {yaw}"), flush=True); time.sleep(4)
        me = h("gpos")
        car = min((e for e in ents(h).values() if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist((e["pos"][0], e["pos"][2]), (cx, cz)))["net"]
        print(f"  car {car} {ents(h)[car]['name']} owner {ents(h)[car]['owner']} (host {hid}, laptop {lid})", flush=True)
        until(lambda: car in ents(L), 30)
        h(f"tpground {cx - R[0] * 3} {cz - R[1] * 3}"); time.sleep(1.5)
        L(f"tpground {cx - F[0] * 5} {cz - F[1] * 5}"); time.sleep(2)   # behind the bed

        def settle(s, net, t=4.0):
            time.sleep(t); return rel(s, car, net).get(net)

        def stack():
            base = spawn(L, "benzineskanna")
            bp = None
            for z in (-1.6, -1.9, -1.3, -2.2):
                print(f"  laptop drops the jerrycan into the bed at z {z}:", L(f"mp putrel {car} {base} 0 1.6 {z}"), flush=True)
                bp = settle(L, base)
                if inside(bp) and bp[1] > 0.0: break
            hb = settle(h, base, 1.0)
            verdict("S THE JERRYCAN LIES IN THE BED (both)", inside(bp) and inside(hb), f"laptop {bp}, host {hb}")
            top = spawn(L, "OilCan")
            # the base is the host's by now (the car's owner takes what lies in it): the laptop can't move it any more
            print("  laptop puts the oil can on the jerrycan:", L(f"mp putrel {car} {top} {bp[0]} {bp[1] + 0.6} {bp[2]}"), flush=True)
            time.sleep(6)
            eh, el = ents(h), ents(L)
            th, tl, bh = rel(h, car, top).get(top), rel(L, car, top).get(top), rel(h, car, base).get(base)
            print(f"  host: base {bh} owner {eh[base]['owner']} ghost {eh[base].get('ghost')}; top {th} owner {eh[top]['owner']} ghost {eh[top].get('ghost')}", flush=True)
            print(f"  laptop: top {tl} owner {el[top]['owner']}; host itemcheck top {h(f'mp itemcheck {top}')}", flush=True)
            verdict("S THE OIL CAN STANDS ON THE JERRYCAN (host)", th and bh and th[1] > bh[1] + 0.12, f"top {th}, base {bh}")
            verdict("S BOTH ARE THE CAR OWNER'S", eh[base]["owner"] == hid and eh[top]["owner"] == hid and el[top]["owner"] == hid,
                    f"base {eh[base]['owner']}, top {eh[top]['owner']}/{el[top]['owner']} (host {hid})")
            verdict("S NO GHOST ON THE HOST", not eh[base].get("ghost") and not eh[top].get("ghost"), f"base {eh[base].get('ghost')}, top {eh[top].get('ghost')}")
            # the host drives off
            L(f"tpground {cx - R[0] * 6} {cz - R[1] * 6}"); time.sleep(1)
            h(f"tpground {cx - R[0] * 3} {cz - R[1] * 3}"); time.sleep(1.5)
            for _ in range(4):
                try: h(f"sitseat {seat_idx(h)} redirect")
                except Exception as ex: print("  host:", ex, flush=True)
                time.sleep(1)
                if g(h, "mainscript.s.player.Bsitting") is True: break
            h("fuelup")
            verdict("S THE HOST SITS IN ITS PICKUP", g(h, "mainscript.s.player.Bsitting") is True)
            h("autodrive on 8"); t0 = time.monotonic(); outs = []
            while time.monotonic() - t0 < 20:
                for who, s_ in (("host", h), ("laptop", L)):
                    r = rel(s_, car, base, top)
                    for n in (base, top):
                        if not inside(r.get(n)): outs.append((who, n, round(time.monotonic() - t0, 1), r.get(n)))
                time.sleep(0.5)
            h("autodrive off"); time.sleep(3)
            sp = abs(float(g(h, "mainscript.s.player.Car.speed") or 0))
            rh, rl = rel(h, car, base, top), rel(L, car, base, top)
            verdict("S AFTER THE DRIVE BOTH STILL IN THE BED (host and laptop)", all(inside(r.get(n)) for r in (rh, rl) for n in (base, top)),
                    f"host {rh}, laptop {rl}; first outside {outs[:3]}; speed now {sp:.0f}")
            h("driveout"); time.sleep(2)

        def beside():
            n = spawn(L, "OilCan")
            hw = 1.05
            print("  laptop puts an oil can on the ground beside the body:", L(f"mp putrel {car} {n} {hw + 0.35} 0.6 0"), flush=True)
            time.sleep(5)
            e = ents(h)[n]; r = rel(h, car, n).get(n)
            print(f"  host: {r} owner {e['owner']} ghost {e.get('ghost')}; itemcheck {h(f'mp itemcheck {n}')}", flush=True)
            verdict("B THE CAN BESIDE THE PARKED PICKUP IS NO GHOST ON THE HOST", not e.get("ghost") and e["owner"] == lid,
                    f"ghost {e.get('ghost')}, owner {e['owner']} (laptop {lid}), car frame {r}")

        def roadside():
            hc = h(f"mp entities")["entities"]; e0 = next(e for e in hc if e["net"] == car)
            cp = e0["pos"]
            # the host's own can in its bed
            h(f"tpground {cp[0] - F[0] * 5} {cp[2] - F[1] * 5}"); time.sleep(1.5)
            can = spawn(h, "OilCan")
            print("  host puts its oil can in the bed by the right wall:", h(f"mp putrel {car} {can} 0.55 1.4 -1.6"), flush=True)
            time.sleep(3); print("   ", rel(h, car, can), flush=True)
            # the laptop's items along the road ahead, just right of the pickup's path
            yawc = yaw; marks = []
            for d in (35, 45, 55):
                gx, gz = cp[0] + F[0] * d + R[0] * 1.7, cp[2] + F[1] * d + R[1] * 1.7
                L(f"tpground {gx - R[0] * 2} {gz - R[1] * 2}"); time.sleep(1.5)
                n = spawn(L, "OilCan")
                print(f"  laptop's can {d} m ahead:", L(f"mp putrel {car} {n} 1.7 0.6 {d}"), flush=True)
                marks.append(n)
            L(f"tpground {cp[0] + F[0] * 45 - R[0] * 8} {cp[2] + F[1] * 45 - R[1] * 8}"); time.sleep(4)
            p0 = {n: ents(L)[n]["pos"] for n in marks}
            n0 = h("mp entities")["attach"]
            h(f"tpground {cp[0] - R[0] * 3} {cp[2] - R[1] * 3}"); time.sleep(1.5)
            for _ in range(4):
                try: h(f"sitseat {seat_idx(h)} redirect")
                except Exception as ex: print("  host:", ex, flush=True)
                time.sleep(1)
                if g(h, "mainscript.s.player.Bsitting") is True: break
            h("fuelup"); h(f"autodrive on {KMH / 3.6:.1f}")
            t0 = time.monotonic(); vmax = 0
            while time.monotonic() - t0 < 14:
                vmax = max(vmax, abs(float(g(h, "mainscript.s.player.Car.speed") or 0))); time.sleep(0.3)
            h("autodrive off"); time.sleep(4)
            n1 = h("mp entities")["attach"]; el = ents(L); eh = ents(h)
            print(f"  top {vmax:.0f} km/h; host itemNear {n0.get('itemNear')} -> {n1.get('itemNear')}, cargoNear {n0.get('cargoNear')} -> {n1.get('cargoNear')}", flush=True)
            moved = {n: round(math.dist(p0[n], el[n]["pos"]), 2) for n in marks}
            print(f"  roadside cans: owners laptop {[el[n]['owner'] for n in marks]} host {[eh[n]['owner'] for n in marks]}; moved {moved}", flush=True)
            verdict(f"R DROVE PAST AT ~{KMH:.0f} KM/H", vmax > KMH * 0.6, f"top {vmax:.0f}")
            verdict("R THE HOST'S CAN STAYED IN ITS BED", inside(rel(h, car, can).get(can)), str(rel(h, car, can)))
            verdict("R THE ROADSIDE CANS STAYED THE LAPTOP'S", all(el[n]["owner"] == lid and eh[n]["owner"] == lid for n in marks)
                    and n1.get("itemNear") == n0.get("itemNear"), f"itemNear +{(n1.get('itemNear') or 0) - (n0.get('itemNear') or 0)}, moved {moved}")
            h("driveout"); time.sleep(2)

        if "B" in ONLY: beside()
        if "S" in ONLY: stack()
        if "R" in ONLY: roadside()
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
