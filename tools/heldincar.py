#!/usr/bin/env python3
"""Holding items in a vehicle (two machines, relay iad by default; the user asked for it 2026-10-08). The laptop's
player holds its own item:
  A  RIGHT HAND, PASSENGER: taken out of the inventory into the right hand (InvSwitchTo), the laptop gets into the
     host's car as a passenger holding it, the host drives at --kmh
  B  PICKED UP, PASSENGER: still riding, it lets go (the item drops into the car) and picks it up again (pickupable)
  C  RIGHT HAND, DRIVER: the laptop drives its own car past the host standing by the road
Every ~0.5 s on both machines: the item with the laptop's player (within 1.5 m of its body as each machine shows it),
not stored/hidden, still held; the car not shoved (speed steady) and no part lost. Screenshots from the host.
  tools/heldincar.py [--no-build] [--relay iad|off] [--kmh 50] [--item 320]"""
import sys, time, math, pathlib, statistics
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, g, car_ready, clear_road

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, KMH, ITEM = arg("--relay", "iad"), arg("--kmh", 50.0, float), arg("--item", 320, int)
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def row(x, n): return next((e for e in x("mp entities")["entities"] if e["net"] == n), None)
def hand(x, again=True):
    for f in ("pickedUp", "inHandP"):
        v = g(x, f"mainscript.s.player.{f}.name")
        if isinstance(v, str) and v not in ("", "null"): return f, v
    # two reads, not one: an item going from one hand field to the other between them read as nothing in the hands
    # (final run 2026-10-22: one sample of fifteen); empty twice is empty
    if again: time.sleep(0.1); return hand(x, False)
    return None, None
def parts(x, car): return x("mp entities")["partsReported"]
def stop_gently(x):
    """ease to a halt, then brake: `brakecar` zeroes the velocity in one frame — the game's crash rule then threw
    hubcaps off at 37 km/h (runs 5-7)"""
    x("autodrive on 0")
    until(lambda: abs(float(g(x, "mainscript.s.player.Car.speed") or 0)) < 3, 20, 0.3)
    x("autodrive off"); x("brakecar"); x("handbrake on")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    for i, a in enumerate(sys.argv):   # --set Entities.RidingFrames=False (A/B flags under TLDRevamp.Net, both games)
        if a == "--set":
            k, v = sys.argv[i + 1].split("=", 1)
            for s_ in (h, L): s_(f"set TLDRevamp.Net.{k} {v}")
            print(f"set {k} = {v} on both", flush=True)
    gods = {}; shifts = {}
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
            shifts[name] = (s_, g(s_, "settingsscript.s.S.BAutoShift")); s_("set settingsscript.s.S.BAutoShift True")
        LID = {}; seated = []; track = []; LASTRELS = []

        def watch(tag, car, net, secs, driver):
            """sample: the item vs the laptop's player on each machine; the car's speed on its owner"""
            dl, dh, stored, notheld, speeds, rel, rels, early = [], [], [], [], [], [], [], []
            seated.clear()
            t0 = time.monotonic()
            while time.monotonic() - t0 < secs:
                a, b = row(L, net), row(h, net)
                me = L("mp players")["me"]
                hp = h("mp players")["players"]
                if "id" not in LID and hp: LID["id"] = hp[0]["id"]
                lb = next((p for p in hp if p["id"] == LID.get("id")), None)
                if lb: seated.append(bool(lb.get("seated")))
                if lb and not dh: print(f"  {tag}: the host's row for the laptop's player: " + str({k: lb.get(k) for k in ('seated', 'seatNet', 'seatIdx', 'seatCarDist', 'shown')}), flush=True)
                if a: dl.append(math.dist(a["pos"], me))
                if b and lb: dh.append(math.dist(b["pos"], lb["shown"]))
                # the item in the car's frame on each machine: where in the car it is (no lag in the world)
                # as drawn (read in LateUpdate: the bridge itself runs before the copies are placed for the frame)
                xa, xb = L(f"mp relpos {car} {net}"), h(f"mp relpos {car} {net}")
                ra, rb = xa.get("late", {}).get(str(net)), xb.get("late", {}).get(str(net))
                ua, ub = xa["items"].get(str(net)), xb["items"].get(str(net))
                if ra and rb: rel.append(math.dist(ra, rb)); rels.append((ra, rb))
                if ua and ub: early.append(math.dist(ua, ub))
                if (a and a["stored"]) or (b and (b["stored"] or b["shown"] is False)): stored.append(round(time.monotonic() - t0, 1))
                if hand(L)[0] is None: notheld.append(round(time.monotonic() - t0, 1))
                speeds.append(abs(float(g(driver, "mainscript.s.player.Car.speed") or 0)))
                if tag == "C": track.append((round(time.monotonic() - t0, 1), [round(v) for v in L("mp players")["me"][::2]], round(speeds[-1])))
                time.sleep(0.4)
            if rel:
                k = max(range(len(rel)), key=rel.__getitem__)
                print(f"  {tag}: item in the car's frame as drawn, laptop vs host: median {statistics.median(rel):.2f} m, max {rel[k]:.2f} m (laptop {rels[k][0]}, host {rels[k][1]}), n {len(rel)}"
                      f" | read at the bridge's time: median {statistics.median(early) if early else -1:.2f} m", flush=True)
            print(f"  {tag}: item from the laptop's player — laptop max {max(dl, default=-1):.2f} m, host max {max(dh, default=-1):.2f} m (median {statistics.median(dh) if dh else -1:.2f}); "
                  f"speed {min(speeds):.0f}-{max(speeds):.0f} km/h; stored at {stored[:5]}; not held at {notheld[:5]}", flush=True)
            LASTRELS[:] = rels
            return rel, stored, notheld, speeds

        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        # the laptop's item, into its right hand (out of the inventory)
        L(f"tpground {r0['gx'] + R[0] * 6} {r0['gz'] + R[1] * 6}"); time.sleep(2)
        before = {e["net"] for e in L("mp entities")["entities"]}
        print("laptop spawns:", L(f"devspawn {ITEM}"), flush=True)
        until(lambda: any(e["net"] not in before and not e["car"] for e in L("mp entities")["entities"]), 15)
        net = next(e["net"] for e in L("mp entities")["entities"] if e["net"] not in before and not e["car"])
        # a fresh spawn can refuse the pick-up for a moment (CanPickup false while it settles): try again
        if not until(lambda: L(f"mp pickupnet {net}"), 10, 1): sys.exit("COULD NOT PICK UP THE SPAWNED ITEM")
        time.sleep(1.5)
        s0 = int(g(L, "mainscript.s.player.selectedInv")); n_inv = int(g(L, "mainscript.s.player.inventory.Count"))
        L(f"call mainscript.s.player.InvSwitchTo {(s0 + 1) % n_inv} False"); time.sleep(1); L(f"call mainscript.s.player.InvSwitchTo {s0} False"); time.sleep(1.5)
        print("  laptop holds:", hand(L), flush=True)
        # A: the host drives its car, the laptop gets in holding it
        if not car_ready(h, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        me = h("mp players")["me"]
        car = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
        until(lambda: row(L, car) is not None, 20)
        L(f"tpground {me[0] + R[0] * 3} {me[2] + R[1] * 3}"); time.sleep(2)
        for _ in range(3):
            L(f"mp sitpassenger {car}"); time.sleep(2)
            if g(L, "mainscript.s.player.Bsitting") is True: break
        held_after_sit = hand(L)
        print("  seated:", g(L, "mainscript.s.player.Bsitting"), "| holds after getting in:", held_after_sit, flush=True)
        verdict("A GETTING IN DOES NOT LOSE THE ITEM", held_after_sit[0] is not None, str(held_after_sit))
        p0 = parts(h, car) + parts(L, car)
        h(f"autodrive on {KMH / 3.6:.1f}")
        until(lambda: abs(float(g(h, "mainscript.s.player.Car.speed") or 0)) > KMH * 0.85, 40, 0.5)
        rel, st, nh, sp = watch("A", car, net, 15, h)
        h("mp lookplayer 1"); time.sleep(0.4); h("shot heldincar_A")
        verdict("A RIDING: SAME PLACE IN THE CAR ON BOTH SCREENS", rel and statistics.median(rel) < 0.3 and max(rel) < 0.75, f"median {statistics.median(rel) if rel else -1:.2f} m, max {max(rel, default=-1):.2f} m")
        verdict("A THE HOST SHOWS THE PASSENGER IN THE SEAT", seated and all(seated), f"seated {sum(seated)}/{len(seated)} samples")
        verdict("A RIDING: NEVER SHOWN AS STORED, STILL HELD", not st and not nh, f"stored {st[:3]}, not held {nh[:3]}")
        print("  A speeds:", [round(v) for v in sp], flush=True)
        verdict("A THE CAR IS NOT SHOVED", min(sp) > KMH * 0.75, f"{min(sp):.0f}-{max(sp):.0f} km/h")
        # B: let go in the car, pick up again
        f_, _ = hand(L)
        if f_ == "inHandP": L("call mainscript.s.player.RightHandRelease True")
        else: L("dropheld")
        time.sleep(2); print("  let go in the car:", hand(L), "| in the car's frame", L(f"mp relpos {car} {net}")["items"].get(str(net)), flush=True)
        # where it lies in the car, within reach (not the test shortcut that brings it 1 m before the camera: facing the
        # side window that was outside the glass — the user saw it held out of the window)
        r_ = L(f"mp pickupnet {net} here"); print("  picks it up again where it lies:", r_, flush=True); time.sleep(2)
        rp_ = L(f"mp relpos {car} {net}")["items"].get(str(net))
        verdict("B HELD INSIDE THE CAR (laptop)", rp_ is not None and abs(rp_[0]) < 0.95, f"in the car's frame {rp_}")
        print("  holds:", hand(L), flush=True)
        rel, st, nh, sp = watch("B", car, net, 10, h)
        h("mp lookplayer 1"); time.sleep(0.4); h("shot heldincar_B")
        try: print(f"  B host interp {net}:", h(f"mp interp {net}"), flush=True)
        except Exception as ex_: print("  interp:", ex_)
        verdict("B PICKED UP WHILE RIDING: SAME PLACE IN THE CAR ON BOTH SCREENS", rel and statistics.median(rel) < 0.3 and max(rel) < 0.75 and not nh, f"median {statistics.median(rel) if rel else -1:.2f} m, max {max(rel, default=-1):.2f} m, not held {nh[:3]}")
        verdict("B THE CAR IS NOT SHOVED", min(sp) > KMH * 0.75, f"{min(sp):.0f}-{max(sp):.0f} km/h")
        # B2: let go in the moving car — loose cargo now (the laptop's first, the car owner's once it touches the car)
        f_, _ = hand(L)
        if f_ == "inHandP": L("call mainscript.s.player.RightHandRelease True")
        else: L("dropheld")
        time.sleep(1.5)
        rel, st, nh, sp = watch("B2", car, net, 8, h)
        own = [row(x, net)["owner"] for x in (h, L)]
        tail = rel[len(rel) // 2:]
        print(f"  B2: owner on host/laptop {own}", flush=True)
        verdict("B2 DROPPED IN THE MOVING CAR: SAME PLACE IN THE CAR ON BOTH SCREENS (settled)", tail and statistics.median(tail) < 0.3 and max(tail) < 0.75, f"median {statistics.median(tail) if tail else -1:.2f} m, max {max(tail, default=-1):.2f} m; whole: max {max(rel, default=-1):.2f} m")
        inside = [rb_ for _, rb_ in LASTRELS if abs(rb_[0]) < 0.95 and abs(rb_[2]) < 2.5 and -0.5 < rb_[1] < 1.6]
        verdict("B2 IT STAYS IN THE CAR (host's screen)", LASTRELS and len(inside) == len(LASTRELS), f"{len(inside)}/{len(LASTRELS)} samples inside; last {LASTRELS[-1][1] if LASTRELS else None}")
        verdict("B2 THE CAR IS NOT SHOVED", min(sp) > KMH * 0.75, f"{min(sp):.0f}-{max(sp):.0f} km/h")
        verdict("A+B NO PART LOST", parts(h, car) + parts(L, car) == p0, f"{p0} -> {parts(h, car) + parts(L, car)}")
        stop_gently(h); time.sleep(2)
        # C: the laptop drives its own car holding it in the right hand, past the host
        L("driveout"); h("driveout"); time.sleep(2)
        print("  after getting out, holds:", hand(L), flush=True)
        if hand(L)[0] is None:
            r_ = row(L, net) or {}
            print("  before picking it up again: owner", r_.get("owner"), "proxy", r_.get("proxy"), "pos", r_.get("pos"), "me", L("mp players")["me"], flush=True)
            cs_ = L(f"mp copystate {net}"); mp_ = L(f"mount probe {net}")
            print("  its copy: skip", cs_.get("skip"), "carriedBy", cs_.get("carriedBy"), "body", cs_.get("body"), "| physlock", mp_.get("physlock"), "parent", mp_.get("parent"), "rb", mp_.get("rb"),
                  "| host's record", {k: v for k, v in h(f"mp sent {net}").items() if k in ("where", "atRest", "locked", "lockFrame", "held", "stored")}, flush=True)
            print("  picks it up again:", L(f"mp pickupnet {net}"), flush=True)
            for _ in range(6):
                time.sleep(0.25); r_ = row(L, net) or {}
                print("  then holds:", hand(L, False), "owner", r_.get("owner"), "held", r_.get("held"), "pos", r_.get("pos"), "me", L("mp players")["me"], flush=True)
            print("  owner log:", h(f"mp ownerlog {net}"), flush=True)
        # into the inventory: it travels with the player (a test teleport would leave a held item behind)
        L(f"call mainscript.s.player.InvSwitchTo {(s0 + 1) % n_inv} False"); time.sleep(1.5)
        r_ = row(L, net) or {}
        print("  put away for the walk:", r_.get("stored"), "| hand", hand(L), "owner", r_.get("owner"), "proxy", r_.get("proxy"), "held", r_.get("held"),
              "selected", g(L, "mainscript.s.player.selectedInv"), flush=True)
        rq = L("roadpoint"); Fq = (math.sin(math.radians(rq["yaw"])), math.cos(math.radians(rq["yaw"])))
        r1 = clear_road(L, rq["gx"] + Fq[0] * 60, rq["gz"] + Fq[1] * 60, Fq[0], Fq[1]); yaw1 = r1["yaw"]; R1 = (math.cos(math.radians(yaw1)), -math.sin(math.radians(yaw1)))
        F1 = (math.sin(math.radians(yaw1)), math.cos(math.radians(yaw1)))
        if not car_ready(L, r1["gx"] + R1[0] * 1.8, r1["gz"] + R1[1] * 1.8, yaw1, "laptop"): verdict("C LAPTOP IN ITS CAR", False)
        else:
            L(f"call mainscript.s.player.InvSwitchTo {s0} False"); time.sleep(1.5)   # taken out in the driver's seat
            print("  laptop holds while driving:", hand(L), flush=True)
            me2 = L("mp players")["me"]
            car2 = min((e for e in L("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me2))["net"]
            # the route first (two runs lost hubcaps ~60 m along it to the game's crash rule): what stands near it
            for d in (0, 20, 40, 60, 80):
                h(f"tpground {r1['gx'] + F1[0] * d} {r1['gz'] + F1[1] * d}"); time.sleep(1.2)
                nr = h("near 12")["near"]
                print(f"  C route +{d} m: {[(n['root'], n['fromRoadCentre'], n['rb']) for n in nr if n['root'] not in ('Terrain', 'GrassMainRoad(Clone)')][:6]}", flush=True)
            # the host watches from beside the start, 14 m off the road (60 m down the road it stood in a bend's path:
            # the car clipped it and the game's crash rule threw two hubcaps — run 5)
            h(f"tpground {r1['gx'] - F1[0] * 6 - R1[0] * 14} {r1['gz'] - F1[1] * 6 - R1[1] * 14}"); time.sleep(2)
            q0 = parts(h, car2) + parts(L, car2)
            L(f"autodrive on {KMH * 0.8 / 3.6:.1f}")
            rel, st, nh, sp = watch("C", car2, net, 9, L)
            h("mp lookplayer 1"); time.sleep(0.3); h("shot heldincar_C")
            stop_gently(L)
            verdict("C DRIVING: SAME PLACE IN THE CAR ON BOTH SCREENS", rel and statistics.median(rel) < 0.3 and max(rel) < 0.75, f"median {statistics.median(rel) if rel else -1:.2f} m, max {max(rel, default=-1):.2f} m")
            verdict("C DRIVING: NEVER SHOWN AS STORED, STILL HELD", not st and not nh, f"stored {st[:3]}, not held {nh[:3]}")
            print("  C drive (t, laptop x/z, km/h):", track, flush=True)
            time.sleep(1.5)
            verdict("C NO PART LOST", parts(h, car2) + parts(L, car2) == q0, f"{q0} -> {parts(h, car2) + parts(L, car2)}")
        print("  carried frames: laptop sent", g(L, "TLDRevamp.Net.Entities.CarriedSent"), "| host switches", g(h, "TLDRevamp.Net.Entities.CarriedSwitches"), "no car", g(h, "TLDRevamp.Net.Entities.CarriedNoCar"), "release claims", g(h, "TLDRevamp.Net.Entities.CarriedReleasedClaims"), "takeovers", g(h, "TLDRevamp.Net.Entities.CarriedTakeovers"), flush=True)
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        for s_ in (h, L):
            try: s_("autodrive off")
            except Exception: pass
        for s_, v in gods.values(): s_(f"set kaposztaleves.s.settings.god {v}")
        for s_, v in shifts.values(): s_(f"set settingsscript.s.S.BAutoShift {v}")
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
