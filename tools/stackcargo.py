#!/usr/bin/env python3
"""Cargo from the side that doesn't own the car (two machines, relay iad by default; review 2026-10-22 #21/#22).
The host has a car; the laptop (the joiner) does the breaking:
  B  BESIDE: the laptop puts an oil can on the ground just beside the parked car's body. On the host it is no ghost
     (a copy with no collisions): it overlaps nothing of the car. (#21: anything inside the car's box was one; a
     pickup's box is 3.4 m wide.)
  R  ROADSIDE: the host drives past the laptop's cans lying beside the road with its own cargo on the car. They stay
     the laptop's. (#22: cargo riding at speed counted as a moving item and took anything within ~1.35 m of it.)
  S  STACK: the laptop puts a crate (--base: or a jerrycan) on the host's car and an oil can on it. On the host the can is the car
     owner's as it is set down, never a ghost, and both stay with the car when the host drives off. (#21: the first
     thing under the can was the crate, not the car, so the can stayed the laptop's and a ghost.)
Inside a car the game mounts whatever comes to rest (TrunkMount, PassengerMount: parented, no body), so a stack in
the bed or on a seat is the car's anyway. On the roof nothing is mounted: --roof puts the crate and the host's own cargo there, which is
where both bugs show (Car01Full).
A/B: TLD_TEST_SETS="Net.Entities.SurfaceSkipsItems=False;Net.Entities.CargoGhostOnlyOverlapping=False;Net.Entities.RideRelative=False"
is the code before the fixes.
--hurry: the can is set down within the host's 2 s claim cooldown (a ghost there until it may be claimed; the pickup must not move).
  tools/stackcargo.py [--no-build] [--relay iad|off] [--kmh 60] [--only B,R,S] [--car Car06PFull|Car01Full] [--roof] [--base Box|benzineskanna] [--hurry]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, g, clear_road, seat_idx

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
KMH = float(sys.argv[sys.argv.index("--kmh") + 1]) if "--kmh" in sys.argv else 60.0
CAR = sys.argv[sys.argv.index("--car") + 1] if "--car" in sys.argv else "Car06PFull"   # Car01Full: cargo on the back seat
SIDE = 1.75 if CAR.startswith("Car06P") else 1.25   # the laptop's roadside cans, right of the car's centre line (m)
BASE = sys.argv[sys.argv.index("--base") + 1] if "--base" in sys.argv else "Box"   # what the can stands on: a crate, or benzineskanna
HURRY = "--hurry" in sys.argv   # the can set down within the host's claim cooldown: a ghost there until it may be claimed
ROOF = "--roof" in sys.argv   # stack on the roof: there the game parents nothing to the car (a mount only takes what's inside)
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
        # the spawner's own new item: another machine's entity that appears meanwhile is a copy here (a proxy), and
        # putting it anywhere was refused ("not the owner", 2 of 5 runs on 2026-10-23)
        before = set(ents(s))
        print(f"  spawn {name}:", s(f"devspawn {name}"), flush=True)
        mine = lambda: [n for n, e in ents(s).items() if n not in before and not e["car"] and not e.get("proxy")]
        until(lambda: mine(), 15)
        return mine()[0]
    gods = {}; shift_h = g(h, "settingsscript.s.S.BAutoShift")
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        h("set settingsscript.s.S.BAutoShift True")
        ids = h(f"itemids {CAR}")["items"]
        pick = next(i for i, n in ids if n == CAR)
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

        def pose():
            """the car's ground position and its forward/right on the ground (from its rotation)"""
            e = ents(h)[car]; x, y, z, w = e["rot"]
            fx, fz = 2 * (x * z + w * y), 1 - 2 * (x * x + y * y)
            rx, rz = 1 - 2 * (y * y + z * z), 2 * (x * z - w * y)
            nf, nr = math.hypot(fx, fz) or 1, math.hypot(rx, rz) or 1
            return e["pos"], (fx / nf, fz / nf), (rx / nr, rz / nr)
        def near(s, back=0.0, side=0.0):
            p, f, r = pose(); s(f"tpground {p[0] + f[0] * back + r[0] * side} {p[2] + f[1] * back + r[1] * side}"); time.sleep(1.5)
        def speed(): return abs(float(g(h, "mainscript.s.player.Car.speed") or 0))
        def sit():
            near(h, 0, -3)
            for _ in range(4):
                h(f"mp sitdriver {car}"); time.sleep(1.5)
                if g(h, "mainscript.s.player.Bsitting") is True: return True
            return False
        def drive(secs, ms, watch=None):
            h("fuelup"); h("handbrake off"); h(f"autodrive on {ms}"); t0, vmax = time.monotonic(), 0
            while time.monotonic() - t0 < secs:
                vmax = max(vmax, speed())
                if watch: watch(time.monotonic() - t0)
                time.sleep(0.4)
            # watched through the stop and the driver getting out too: a can on the crate ended 79 m behind the pickup
            # with nothing seen in the 20 s of driving (2026-10-23)
            def stopped():
                if watch: watch(time.monotonic() - t0)
                return speed() < 2
            h("autodrive on 0"); until(stopped, 25, 0.5); h("autodrive off"); h("brakecar"); h("handbrake on"); time.sleep(2)
            if watch: watch(time.monotonic() - t0)
            h("driveout"); time.sleep(2)
            if watch: watch(time.monotonic() - t0)
            return vmax
        def rabbit_took(n):
            """the host's owner log: the last owner change of `n` was a rabbit's claim. A rabbit the host simulates takes
            what it comes near (RabbitObjectNear), so it can push it; that is the rabbits' rule, not the cargo rules this
            tests (the can beside the pickup and a roadside can went to the host so, 2026-10-23)"""
            log = h(f"mp ownerlog {n}").get("log", [])
            print(f"   {n} owner log on the host: {log[-3:]}", flush=True)
            return bool(log) and "claim: Rabbit" in log[-1]
        def claims(s):
            a = s("mp entities")["attach"]; return {k: a.get(k) for k in ("cargoNear", "cargoOnContact", "itemNear", "itemOnContact", "cargoGhostsMade")}

        BED = CAR.startswith("Car06P")
        def settle(s, net, t=4.0):
            time.sleep(t); return rel(s, car, net).get(net)

        def put_from_above(n, x, y, z, clear=False):
            """the laptop's item to (x, y, z) in the car's frame by way of a point 2 m above it: on the host its copy is
            moved along the line between two poses, and a jump from the ground behind the pickup to its bed ran that line
            through the tailgate. A copy moved through a car launches it (the parked pickup at 32.7 m/s, the can thrown
            30-265 m, 2026-10-23); a player's item never moves so."""
            try: L(f"mp putrel {car} {n} {x} {y + 2.0} {z}")
            except RuntimeError as e_:
                if "not the owner" not in str(e_): raise
                return "the host took it where it was staged, 9 m behind the pickup: not put"
            time.sleep(0.3)   # long enough for a state to reach the host, short
            # enough not to land: in the bed the game mounts it (no entity of its own there any more)
            try: return L(f"mp putrel {car} {n} {x} {y} {z}" + (" clear" if clear else ""))
            except RuntimeError as e_:
                if "not the owner" not in str(e_): raise
                # the host took it as it came (the near claim, falling into its car's box): it falls from there
                return "the host claimed it on its way down: it falls from 2 m above"

        def staged(name):
            """the laptop's new item, put first on the road 9 m behind the pickup: the game's spawn puts it where the
            laptop looks, once 17 m off beside a parked bike that the host simulates, and the host took it there on
            contact before it ever reached the bed (2026-10-23). Taken anyway: spawned again (3 tries)."""
            for _ in range(3):
                n = spawn(L, name)
                # 2.5 s: past the host's 2 s claim cooldown since it became the laptop's, so it is claimed as it
                # comes; --hurry: within it
                try: L(f"mp putrel {car} {n} 0 0.5 -9"); time.sleep(0.3 if HURRY else 2.5)
                except RuntimeError as e_:
                    if "not the owner" not in str(e_): raise
                    print(f"   {name} {n} was taken by the host where it was spawned: again", flush=True); continue
                # the host may take it there too (a rabbit's owner takes what the rabbit touches: RabbitObjectNear)
                if not ents(L).get(n, {"proxy": True}).get("proxy"): return n
                print(f"   {name} {n} was taken by the host where it was staged: again", flush=True)
            raise RuntimeError(f"{name}: taken by the host three times before it could be put")

        def stack():
            near(L, -8)
            base = staged(BASE)
            bp = None
            for i_, z in enumerate((-0.3, -0.6, 0.0, -0.9) if ROOF else (-1.6, -1.9, -1.3, -2.2)):
                # once in the host's car the base is the host's: a second try is refused, and the base lies where it is
                if i_ and ents(L).get(base, {"proxy": True}).get("proxy"): print("   the base is the host's now (or stored): no second try", flush=True); break
                if ROOF: print(f"  laptop drops the base onto the roof at z {z}:", L(f"mp putrel {car} {base} 0 {1.25 if BASE == 'Box' else 1.7} {z}"), flush=True)
                elif BED: print(f"  laptop drops the base into the bed at z {z}:", put_from_above(base, 0, 1.6, z), flush=True)
                else: print("  laptop puts the base on the back seat:", L(f"mp putincar {car} {base} 0"), flush=True)
                bp = settle(L, base)
                if ROOF and bp and bp[1] > 0.7 or not ROOF and (inside(bp) and bp[1] > 0.0 or not BED): break
            print("   host sees the base:", h(f"mp cargoinfo {base}"), flush=True)
            hb = settle(h, base, 1.0)
            verdict("S THE JERRYCAN LIES IN THE BED (both)", inside(bp) and inside(hb), f"laptop {bp}, host {hb}")
            top = staged("OilCan")
            bp = rel(L, car, base).get(base)
            print("   the base before the can goes on:", bp, h(f"mp cargoinfo {base}"), flush=True)
            if not (bp and (bp[1] > 0.7 if ROOF else inside(bp))): verdict("S THE JERRYCAN STAYED PUT", False, str(bp)); return
            c0 = claims(h); car0 = ents(h)[car]["pos"]
            # set down on it, not dropped: a can landing hard bumps the base, and the host's moving base takes it
            # from inside the base upward until it overlaps nothing: a fixed height above the base's centre started the
            # can inside the crate when the crate lay on another face, and the physics threw it 40-225 m (2026-10-23)
            print("  laptop sets the oil can down on the base:", put_from_above(top, bp[0], bp[1] + 0.1, bp[2], clear=True), flush=True)
            for _ in range(3): time.sleep(0.5); print("   host sees the top:", h(f"mp cargoinfo {top}"), "\n   the laptop:", L(f"mp cargoinfo {top}"), flush=True)
            time.sleep(4.5)
            print("   host sees the base:", h(f"mp cargoinfo {base}"), "\n   the top:", h(f"mp cargoinfo {top}"), flush=True)
            eh, el = ents(h), ents(L)
            th, tl, bh = rel(h, car, top).get(top), rel(L, car, top).get(top), rel(h, car, base).get(base)
            print(f"  host: base {bh} owner {eh[base]['owner']} ghost {eh[base].get('ghost')}; top {th} owner {eh[top]['owner']} ghost {eh[top].get('ghost')}", flush=True)
            print(f"  laptop: top {tl} owner {el[top]['owner']}; host claims {c0} -> {claims(h)}", flush=True)
            # a note, not a verdict: set down by a teleport, the can sometimes rolls off or knocks the base over (on both
            # machines alike); the ghost and owner checks below hold either way
            stands = th and bh and th[1] > bh[1] + 0.12 and math.dist((th[0], th[2]), (bh[0], bh[2])) < 0.2
            print(f"  the can {'stands on' if stands else 'did NOT stay on'} the base: top {th}, base {bh}", flush=True)
            verdict("S BOTH ARE THE CAR OWNER'S", eh[base]["owner"] == hid and eh[top]["owner"] == hid and el[top]["owner"] == hid,
                    f"base {eh[base]['owner']}, top {eh[top]['owner']}/{el[top]['owner']} (host {hid})")
            c1 = claims(h)
            # a copy touching the car gives it the whole impulse: the parked pickup was launched at 32.7 m/s (2026-10-23)
            kick = math.dist(car0, ents(h)[car]["pos"])
            verdict("S THE PICKUP STAYED PUT AS THE CAN LANDED (host)", kick < 0.05, f"moved {kick:.3f} m; claims on contact +{c1['cargoOnContact'] - c0['cargoOnContact']}")
            if c1["cargoGhostsMade"] != c0["cargoGhostsMade"]: rabbit_took(top)
            if HURRY: print(f"  --hurry: a ghost while the host may not take it yet is the rule; ghosts made {c0['cargoGhostsMade']} -> {c1['cargoGhostsMade']}", flush=True)
            else: verdict("S THE TOP WAS NEVER A GHOST ON THE HOST (claimed as it was set down)", c1["cargoGhostsMade"] == c0["cargoGhostsMade"],
                          f"ghosts made {c0['cargoGhostsMade']} -> {c1['cargoGhostsMade']}")
            verdict("S NO GHOST ON THE HOST", not eh[base].get("ghost") and not eh[top].get("ghost"), f"base {eh[base].get('ghost')}, top {eh[top].get('ghost')}")
            # the host drives off, the laptop stays
            near(L, 0, -8)
            verdict("S THE HOST SITS IN ITS PICKUP", sit())
            outs = []
            def watch(t):
                for who, s_ in (("host", h), ("laptop", L)):
                    r = rel(s_, car, base, top)
                    for n in (base, top):
                        if not inside(r.get(n)): outs.append((who, n, round(t, 1), r.get(n)))
            vmax = drive(20, 8, watch)
            rh, rl = rel(h, car, base, top), rel(L, car, base, top)
            if ROOF:   # loose on a roof they may fall off as it pulls away: on both machines the same, then
                verdict("S AFTER THE DRIVE THE SAME PLACE ON BOTH", vmax > 15 and all(rh.get(n) and rl.get(n) and math.dist(rh[n], rl[n]) < 0.3 for n in (base, top)),
                        f"top {vmax:.0f} km/h; host {rh}, laptop {rl}; first off the car {outs[:3]}")
            else:
                verdict("S AFTER THE DRIVE BOTH STILL IN THE BED (host and laptop)", vmax > 15 and all(inside(r.get(n)) for r in (rh, rl) for n in (base, top)),
                        f"top {vmax:.0f} km/h; host {rh}, laptop {rl}; first outside {outs[:3]}")

        def beside():
            n = spawn(L, "OilCan")
            hw = 1.05
            print("  laptop puts an oil can on the ground beside the body:", L(f"mp putrel {car} {n} {hw + 0.35} 0.6 0"), flush=True)
            time.sleep(5)
            e = ents(h)[n]; r = rel(h, car, n).get(n)
            print(f"  host: {r} owner {e['owner']} ghost {e.get('ghost')}; itemcheck {h(f'mp itemcheck {n}')}", flush=True)
            verdict("B THE CAN BESIDE THE PARKED PICKUP IS NO GHOST ON THE HOST", not e.get("ghost") and (e["owner"] == lid or rabbit_took(n)),
                    f"ghost {e.get('ghost')}, owner {e['owner']} (laptop {lid}), car frame {r}")

        def roadside():
            # the host's own can in its bed, by the right wall
            near(h, -5)
            can = spawn(h, "benzineskanna" if ROOF else "OilCan")
            if ROOF: print("  host puts its jerrycan on the roof, right of centre:", h(f"mp putrel {car} {can} 0.35 2.2 -0.3"), flush=True)
            elif BED: print("  host puts its oil can in the bed by the right wall:", h(f"mp putrel {car} {can} 0.55 0.6 -1.6"), flush=True)
            else: print("  host puts its oil can on the back seat:", h(f"mp putincar {car} {can} 0"), flush=True)
            print("   settled:", settle(h, can), flush=True)
            # the laptop's cans along the road ahead, just right of the pickup's path
            marks = []
            for d in (60, 75, 90):
                near(L, d, 3.5)
                n = spawn(L, "OilCan")
                print(f"  laptop's can {d} m ahead:", L(f"mp putrel {car} {n} {SIDE} 0.3 {d}"), flush=True)
                marks.append(n)
            near(L, 75, 9); time.sleep(3)
            p0 = {n: ents(L)[n]["pos"] for n in marks}
            print("  the cans in the car's frame:", rel(h, car, *marks), flush=True)
            if not sit(): verdict("R THE HOST SITS IN ITS PICKUP", False); return
            c0 = claims(h)
            seen, ghosted = [], set()
            def watch(t):
                # the roadside cans themselves: the host's ghost count also counts any other copy at rest against a car
                # simulated there (a joiner's bus tyre against a parked bike by the road made it fail, 2026-10-23)
                ghosted.update(n for n in h("mp entities")["attach"].get("ghostNets", []) if n in marks)
                if len(seen) < int(t / 1.5) + 1: seen.append(1); print(f"   +{t:.1f} s the host's can:", h(f"mp cargoinfo {can}"), flush=True)
            vmax = drive(13, KMH / 3.6, watch)
            watch(99)
            c1 = claims(h); el = ents(L); eh = ents(h)
            moved = {n: round(math.dist(p0[n], el[n]["pos"]), 2) for n in marks}
            print(f"  top {vmax:.0f} km/h; host claims {c0} -> {c1}", flush=True)
            print(f"  roadside cans: owners laptop {[el[n]['owner'] for n in marks]} host {[eh[n]['owner'] for n in marks]}; moved {moved}", flush=True)
            rabbits = {n for n in marks if eh[n]["owner"] != lid and rabbit_took(n)}   # who took it, and why
            verdict(f"R DROVE PAST AT ~{KMH:.0f} KM/H", vmax > KMH * 0.6, f"top {vmax:.0f}")
            verdict("R THE HOST'S CAN RODE IN ITS BED", inside(rel(h, car, can).get(can)), str(rel(h, car, can)))
            verdict("R PASSING MADE NO GHOSTS OF THEM", not ghosted, f"roadside cans a ghost on the host: {sorted(ghosted)}; ghosts made anywhere +{c1['cargoGhostsMade'] - c0['cargoGhostsMade']}")
            verdict("R THE ROADSIDE CANS STAYED THE LAPTOP'S", all(n in rabbits or el[n]["owner"] == lid and eh[n]["owner"] == lid for n in marks)
                    and c1["itemNear"] == c0["itemNear"] and c1["itemOnContact"] == c0["itemOnContact"],
                    f"itemNear +{c1['itemNear'] - c0['itemNear']}, itemOnContact +{c1['itemOnContact'] - c0['itemOnContact']}, moved {moved}; a rabbit took {sorted(rabbits)}")

        if "B" in ONLY: beside()
        if "R" in ONLY: roadside()
        if "S" in ONLY: near(L, -5); stack()
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
