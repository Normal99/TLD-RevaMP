#!/usr/bin/env python3
"""Car-to-car collisions between players — no autopilot, just an open stretch of main road and two car01s:
  rear   : a parked car rear-ended at SPEED
  tbone  : a car parked across the lane hit in the side at SPEED
  headon : both cars at SPEED into each other, same lane
The hit car holds its handbrake in both modes: in MP the laptop's player sits in it, and a seated player's automatic
gearbox holds the handbrake at low rpm (carscript.AutoHandBrake) — the single-player reference car gets the same.
In MP the laptop's player always sits in the car that gets hit (the user's call: that's the seat to watch it from), the
desktop's car rams (head-on: each drives its own). SPEED 100 km/h by default.
Each crash is first done in single player on the desktop — both cars simulated by the game itself: the REFERENCE for how
that crash should go — then in a session, each car owned (driven, simulated) by its own machine. Cars are launched at
speed (`crash launch`: rolling, no throttle) ~12 m before contact, so every run hits the same way; the game's crash
detector is told the launch speed is normal. `crash rec` on every machine records each car: speed before/after the
first car-car contact, peak speed and spin after it, how high it went, on its roof or not, the game's crash-damage
input and parts lost — each car's truth from its owner's machine, and how the other machine showed it.
Verdicts per crash (MP, each car's owner record against the reference and physics):
  - nobody flung: no car comes out faster than the closing speed (+1 m/s), none rises > 1 m or spins > 2× the reference
  - momentum: total momentum after ≤ 1.15× before (equal cars: more means impulses counted twice)
  - both hit: each car's own record shows the contact (the rammed car moves, the rammer slows)
  - the copies agree: after the crash each car rests where its owner has it (≤ 0.3 m)
Staging: each crash on the next clear stretch at least 200 m past the previous one, with no other car within 45 m
(parked world cars, earlier wrecks — a rear-end wreck once flew 356 m down the road).
  --old: the behaviour before v0.57.59 (kinematic copies + the impulse relay), for A/B
  --approach A|B|both: A = each machine simulates the crash itself (physical copies, Entities.Physical); B = the machine
  that hits first simulates the whole crash (Entities.CrashAuthority); both = every MP crash once with each
  tools/crashtest.py [--no-build] [--only rear,tbone,headon] [--mode sp|mp|both] [--relay <dc>] [--kmh 100] [--gap 20] [--old]"""
import sys, time, math, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP
from roadkit import CAR01, until, g, clear_road, car_ready

ONLY = sys.argv[sys.argv.index("--only") + 1].split(",") if "--only" in sys.argv else ["rear", "tbone", "headon"]
MODE = sys.argv[sys.argv.index("--mode") + 1] if "--mode" in sys.argv else "both"
RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else None
SPEED = (float(sys.argv[sys.argv.index("--kmh") + 1]) if "--kmh" in sys.argv else 100.0) / 3.6   # m/s
KEEP_RIGHT = 1.8
GAP = float(sys.argv[sys.argv.index("--gap") + 1]) if "--gap" in sys.argv else 20.0   # m of run-up (centre to centre): the launched car's wheels spin up before contact; low speeds need less (it rolls with no throttle: at 10 km/h it stopped after 9.4 m)
OLD = "--old" in sys.argv
APPROACHES = {"A": ["A"], "B": ["B"], "both": ["A", "B"]}[sys.argv[sys.argv.index("--approach") + 1] if "--approach" in sys.argv else "B"]
APPROACH = "A"

verdicts = {}
def verdict(name, ok, detail=""):
    if RELAY: name += f" @relay:{RELAY}"
    verdicts[name] = ok
    print(f"{name}: {'PASS' if ok else 'FAIL'}  {detail}", flush=True)

def unit(yaw): return (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
def right(yaw): return (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))

def layout(kind, P, yaw):
    """(car A, car B): (gx, gz, yaw, launch speed). A is the first car (rammer; head-on: the one driving along yaw)"""
    D, Rt = unit(yaw), right(yaw)
    lane = (P[0] + Rt[0] * KEEP_RIGHT, P[1] + Rt[1] * KEEP_RIGHT)
    if kind == "rear":
        return (lane[0] - D[0] * GAP, lane[1] - D[1] * GAP, yaw, SPEED), (lane[0], lane[1], yaw, 0.0)
    if kind == "tbone":   # B across the lane, its middle on A's line
        return (lane[0] - D[0] * GAP, lane[1] - D[1] * GAP, yaw, SPEED), (lane[0], lane[1], yaw + 90, 0.0)
    h = GAP               # head-on: both in the same lane, facing each other, each at SPEED
    return (lane[0] - D[0] * h, lane[1] - D[1] * h, yaw, SPEED), (lane[0] + D[0] * h, lane[1] + D[1] * h, yaw + 180, SPEED)

def stretch(side, x, z, yaw):
    """a clear piece of main road from (x, z) on, looking along yaw, with no car within 45 m; returns (P, road yaw along yaw)"""
    D = unit(yaw)
    for _ in range(8):
        r = clear_road(side, x, z, D[0], D[1], look=D)
        cars = side("crash cars 45")["cars"]
        if not cars: break
        print(f"    cars near the stretch: {[(c['name'], c['dist']) for c in cars][:4]} — 100 m on", flush=True)
        h = unit(r["yaw"]); h = h if h[0] * D[0] + h[1] * D[1] > 0 else (-h[0], -h[1]); D = h   # along the road
        x, z = r["gx"] + D[0] * 100, r["gz"] + D[1] * 100
    ry = r["yaw"] if unit(r["yaw"])[0] * D[0] + unit(r["yaw"])[1] * D[1] > 0 else r["yaw"] + 180
    return (r["gx"], r["gz"]), ry

def role(rows, D):
    """name the recorded cars: A = moving along D before the contact, B = the other"""
    out = {}
    for c in rows:
        if "error" in c: continue
        v = c["vBefore"]; along = v[0] * D[0] + v[2] * D[1]
        out.setdefault("A" if along > 1.0 else "B", []).append(c)
    return out

def pick(rows, D, want_proxy=None, a_label=None):
    """A/B records. A: the car moving along D (want_proxy filters own/copy). B: the car A hit — the recorder also picks up
    parked world cars nearby (a bike, a van), so B is the one in contact with A, else the one nearest where A ended"""
    rows = [c for c in rows if "error" not in c]
    r = role([c for c in rows if want_proxy is None or c["proxy"] == want_proxy], D)
    out = {}
    if "A" in r: out["A"] = r["A"][0]
    cands = [c for c in r.get("B", [])]
    if cands:
        al = out["A"]["car"] if "A" in out else a_label
        hit = [c for c in cands if al and c["contactWith"] == al]
        if hit: out["B"] = hit[0]
        elif "A" in out: out["B"] = min(cands, key=lambda c: math.dist(c["endPos"][::2], out["A"]["endPos"][::2]))
        else: out["B"] = max(cands, key=lambda c: c["movedAfterM"])
    return out

def show(tag, c):
    if not c: print(f"     {tag:34s} —", flush=True); return
    print(f"     {tag:34s} before {c['speedBefore']:5.1f}  after {c['speedAfter']:5.1f}  peak {c['peakSpeedAfter']:5.1f} m/s  spin {c['peakSpinDeg']:4.0f}°/s  "
          f"rise {c['riseM']:4.2f} m  upY {c['endUpY']:+.2f}  moved {c['movedAfterM']:5.1f} m  damage {c['damageAfterContact']:5.1f}  parts lost {c['partsLost']}  "
          f"contact {'seen' if c['contactSeen'] else 'motion'} @{c['contactT']:.2f}s impulse {c['contactImpulse']}", flush=True)

def momentum(a, b, D):
    """|total horizontal momentum| after/before (equal cars: velocities)"""
    pb = (a["vBefore"][0] + b["vBefore"][0], a["vBefore"][2] + b["vBefore"][2])
    pa = (a["vAfter"][0] + b["vAfter"][0], a["vAfter"][2] + b["vAfter"][2])
    before = math.hypot(*pb); after = math.hypot(*pa)
    return after / before if before > 0.5 else (after + 0.5) / 0.5   # head-on: ~0 before, so judge the absolute

ref = {}

def run_sp(b, kind, x, z, yaw):
    print(f"\n[SP reference] {kind}", flush=True)
    P, ry = stretch(b, x, z, yaw)
    (ax, az, ayaw, av), (bx, bz, byaw, bv) = layout(kind, P, ry)
    print("  target/second car:", b(f"spawncarg {CAR01} 0 {bx} {bz} {byaw}"), flush=True); time.sleep(2)
    ok = car_ready(b, ax, az, ayaw, "desktop (car A)")
    time.sleep(2)

    print("  recording:", b("crash rec start 60"), flush=True)
    if bv == 0: print("  hit car:", b(f"crash launch 0 {bx} {bz} hold"), flush=True)   # at rest, handbrake held (as the seated laptop player's)
    b(f"crash launch {av}")
    if bv > 0: b(f"crash launch {bv} {bx} {bz} hold")
    time.sleep(6)
    r = b("crash rec stop")
    print("  raw:", json.dumps(r), flush=True)
    D = unit(ry); cars = pick(r["cars"], D)
    show("A (rammer)", cars.get("A")); show("B", cars.get("B"))
    ref[kind] = cars
    if b("get mainscript.s.player.Bsitting")["value"] is True: b("driveout"); time.sleep(1)
    if not (ok and "A" in cars and "B" in cars): print(f"  SP {kind}: setup failed", flush=True)
    return P, ry

COUNTERS = ("cargoNear", "cargoOnContact", "gameFrozenReleased", "handoversSent", "handoversApplied", "handoversLate", "handoversCrashSkipped", "claimsSent", "attachReapplied")
def counters(s_):
    found = {}
    def walk(o):
        if isinstance(o, dict):
            for k, v in o.items():
                if k in COUNTERS and isinstance(v, (int, float)): found[k] = v
                elif k == "handoverLateWhy": found["_late"] = v
                elif isinstance(v, (dict, list)) and k != "entities": walk(v)
        elif isinstance(o, list):
            for v in o: walk(v)
    walk(s_("mp entities")); return found

def run_mp(b, L, kind, x, z, yaw):
    print(f"\n[MP] {kind}", flush=True)
    P, ry = stretch(b, x, z, yaw)
    # the laptop's world generated there too — standing 30 m back, not where a car is about to be spawned (the desktop's
    # T-bone car arrived on top of the laptop player standing at P and killed them)
    D0 = unit(ry)
    L(f"tpground {P[0] - D0[0] * 30} {P[1] - D0[1] * 30}"); time.sleep(4)
    (ax, az, ayaw, av), (bx, bz, byaw, bv) = layout(kind, P, ry)
    # the laptop's player sits in the car that gets hit; the desktop's car rams (head-on: each drives its own)
    ram, tgt = b, L
    names = {id(b): "desktop", id(L): "laptop"}
    ok_t = car_ready(tgt, bx, bz, byaw, f"{names[id(tgt)]} (car B)")
    ok_r = car_ready(ram, ax, az, ayaw, f"{names[id(ram)]} (car A)")
    tgt("handbrake on")   # parked until the moment of the launch (off earlier, it rolled away on a slope: 3 m/s at contact)
    # both machines see both new cars (each shared when its player sat down; the copy arrives through the server)
    def sat_in(side):
        me = side("mp players")["me"]
        cars = [e for e in side("mp entities")["entities"] if e["car"] and e["part"] < 0 and not e["proxy"]]
        return min(cars, key=lambda e: math.dist(e["pos"][::2], me[::2]))["net"] if cars else None
    until(lambda: sat_in(b) is not None and sat_in(L) is not None, 15)
    nb, nl = sat_in(b), sat_in(L)
    def has(side, net): return any(e["net"] == net and e["proxy"] for e in side("mp entities")["entities"])
    seen = until(lambda: has(b, nl) and has(L, nb), 20)
    print(f"  cars: desktop's #{nb}, laptop's #{nl}; each shown on the other machine: {seen}", flush=True)
    time.sleep(3)
    claims0 = {id(s_): s_("mp crashauth").get("claims", 0) for s_ in (b, L)} if APPROACH == "B" else {}
    cnt0 = {id(s_): counters(s_) for s_ in (b, L)}
    rb_, rl = b("crash rec start 60"), L("crash rec start 60")
    print("  recording: desktop", rb_, "| laptop", rl, flush=True)
    # the parked car's handbrake OFF at the launch, as in the reference: left on, the game swaps its wheels for friction
    # boxes after a while (carscript.Lock) — a different crash depending on timing
    if bv == 0: print("  hit car:", tgt("crash launch 0 hold"), flush=True)
    ram(f"crash launch {av}")
    if bv > 0: tgt(f"crash launch {bv} hold")
    time.sleep(7)
    # both drivers pull the handbrake (a real brake — brakecar's instant stop would read as another crash): the cars come to
    # rest, so where each copy rests is comparable with its car
    for s_ in (b, L): s_("handbrake on")
    time.sleep(3)
    # approach B: the machine that took the crash over simulates it; the other shows it as two display copies (no
    # collision events there) — its records are timed from the simulating machine's crash moment plus the display delay.
    # Which machine that is: whose claims rose (head-on: either car can hit first — v0.57.82 the laptop did)
    sim, other = b, L
    if APPROACH == "B" and L("mp crashauth").get("claims", 0) > claims0[id(L)] and b("mp crashauth").get("claims", 0) == claims0[id(b)]:
        sim, other = L, b
    r_sim = sim("crash rec stop")
    seen_t = [c["contactT"] for c in r_sim["cars"] if c.get("contactSeen")]
    r_other = other(f"crash rec stop {min(seen_t) + 0.12:.2f}") if APPROACH == "B" and seen_t else other("crash rec stop")
    if APPROACH == "B": print(f"  crash simulated on the {names[id(sim)]}", flush=True)
    rd, rlp = (r_sim, r_other) if sim is b else (r_other, r_sim)
    print("  raw desktop:", json.dumps(rd), flush=True); print("  raw laptop:", json.dumps(rlp), flush=True)
    D = unit(ry)
    # by network id: car A is the rammer's own car (head-on: the desktop's), B the other; truth from its owner's machine
    net_a, net_b = (nb, nl) if ram is b else (nl, nb)
    def rec(rows, net, proxy): return next((c for c in rows["cars"] if c.get("net") == net and c["proxy"] == proxy and "error" not in c), None)
    side_rows = {id(b): rd, id(L): rlp}
    truth = {"A": rec(side_rows[id(ram)], net_a, False), "B": rec(side_rows[id(tgt)], net_b, False)}
    copy = {"A": rec(side_rows[id(tgt)], net_a, True), "B": rec(side_rows[id(ram)], net_b, True)}
    rf = ref.get(kind, {})
    for k, lab in (("A", "A (rammer)"), ("B", "B")):
        show(f"{lab} reference (SP)", rf.get(k))
        show(f"{lab} truth (its owner)", truth.get(k))
        show(f"{lab} as the other machine saw it", copy.get(k))
    print(f"  contact relay: desktop sent {rd['contactSent']} (max {rd['impulseMaxSent']} Ns) applied {rd['contactApplied']} (max {rd['impulseMaxApplied']} Ns) | "
          f"laptop sent {rlp['contactSent']} (max {rlp['impulseMaxSent']} Ns) applied {rlp['contactApplied']} (max {rlp['impulseMaxApplied']} Ns)", flush=True)
    a, bb = truth.get("A"), truth.get("B")
    if not (ok_t and ok_r and seen and a and bb):
        verdict(f"{kind.upper()} MP", False, f"setup: seated {ok_t}/{ok_r}, both seen {seen}, records A {bool(a)} B {bool(bb)}"); return P, ry
    closing = math.hypot(a["vBefore"][0] - bb["vBefore"][0], a["vBefore"][2] - bb["vBefore"][2])
    problems = []
    for k, c in (("A", a), ("B", bb)):
        r0 = rf.get(k)
        # flung: faster after the hit than either its own speed before or the closing speed (the peak window starts at
        # the contact, so a rammer's own approach speed is in it)
        # (the reference itself peaks ~2 m/s over its approach at the moment of impact, and at 100 km/h rises up to 1.5 m)
        lim = max(closing, c["speedBefore"], r0["peakSpeedAfter"] if r0 else 0) + 3.0
        if c["peakSpeedAfter"] > lim: problems.append(f"{k} flung ({c['peakSpeedAfter']:.1f} m/s > {lim - 3:.1f} + 3)")
        if c["riseM"] > max(1.5, (r0["riseM"] + 0.75) if r0 else 1.5): problems.append(f"{k} airborne ({c['riseM']:.2f} m)")
        if r0 and c["peakSpinDeg"] > 2 * r0["peakSpinDeg"] + 60: problems.append(f"{k} spins {c['peakSpinDeg']:.0f}°/s (reference {r0['peakSpinDeg']:.0f})")
        if c["endUpY"] < 0.3 and not (r0 and r0["endUpY"] < 0.3): problems.append(f"{k} ended on its side/roof")
    m = momentum(a, bb, D)
    if kind != "headon" and m > 1.15: problems.append(f"momentum ×{m:.2f} (created)")
    # (a slow bump into a car with its handbrake held doesn't move it in the reference either: --kmh 10, B moved 0.0 m —
    # then the hit shows as the rammer stopping, checked below)
    rb_moved = rf["B"]["movedAfterM"] if rf.get("B") else 1.0
    if bb["movedAfterM"] < 0.3 and rb_moved >= 0.3 and kind != "headon": problems.append("B never moved on its owner's machine (the hit didn't reach it)")
    if a["speedAfter"] > a["speedBefore"] - 0.5 and kind != "headon": problems.append("A didn't slow (hit nothing on its owner's machine)")
    for k in ("A", "B"):
        c, t = copy.get(k), truth.get(k)
        if c and t:
            d = math.dist(c["endPos"][::2], t["endPos"][::2])
            if d > 0.3: problems.append(f"{k}'s copy rests {d:.2f} m from the car")
            # what the other player saw during the crash: the copy's own local crash may differ for a moment, but not wildly
            if c["peakSpinDeg"] > 2 * t["peakSpinDeg"] + 90: problems.append(f"{k}'s copy spun {c['peakSpinDeg']:.0f}°/s (the car {t['peakSpinDeg']:.0f})")
    for s_ in (b, L):   # today's ownership features during the crash (claims of loose items near cars, hand-over records)
        c1 = counters(s_); print(f"  {names[id(s_)]} during the crash:", {k: c1[k] - cnt0[id(s_)].get(k, 0) for k in c1 if k != "_late"}, "late:", c1.get("_late"), flush=True)
    print("  physical copies: desktop", b("mp physical"), "| laptop", L("mp physical"), flush=True)
    if APPROACH == "B": print("  crash authority: desktop", b("mp crashauth"), "| laptop", L("mp crashauth"), flush=True)
    verdict(f"{kind.upper()} MP" + (" (old)" if OLD else f" [{APPROACH}]"), not problems, f"closing {closing:.1f} m/s, momentum after/before {m:.2f}; " + ("; ".join(problems) if problems else "physically plausible, copies agree"))
    return P, ry

ensure_both("--no-build" not in sys.argv)
L = None
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        b("set TLDRevamp.TestSafety.BlockAutoSave True"); b("set kaposztaleves.s.settings.god True")
        rp = b("roadpoint"); yaw = rp["yaw"]; cur = (rp["gx"], rp["gz"])
        D = unit(yaw)
        def onward(res):
            """200 m on along the road's heading where the last crash was (the road curves: a straight line leaves it)"""
            global yaw
            P, ry = res; yaw = ry; d = unit(ry)
            return (P[0] + d[0] * 200, P[1] + d[1] * 200)
        if MODE in ("sp", "both"):
            for k in ONLY: cur = onward(run_sp(b, k, cur[0], cur[1], yaw))
            print("  cleanup:", b("clearpile"), flush=True)
        if MODE in ("mp", "both"):
            L = Agent()
            for side in (b, L): side("set TLDRevamp.TestSafety.BlockAutoSave True")
            L("set kaposztaleves.s.settings.god True")
            if RELAY: print("relay only via", RELAY, b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
            b("mp host 27070")
            if RELAY: L(f"mp joinid {b('net me')['steamId']}")
            else: L(f"mp join {DESKTOP_IP}:27070")
            if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
            L("set kaposztaleves.s.settings.god True")   # joining loads the host's world: settings are fresh after it
            if RELAY: print("route:", L("net route"), flush=True)
            for s in (b, L):
                s(f"set TLDRevamp.Net.Entities.PhysicalCopies {not OLD}"); s(f"set TLDRevamp.Net.Entities.ContactRelayOn {OLD}")
            for k, ap in [(k, ap) for k in ONLY for ap in APPROACHES]:
                APPROACH = ap
                for s in (b, L): s(f"set TLDRevamp.Net.Entities.CrashAuthority {ap == 'B'}")
                print(f"\n=== approach {ap}: " + ("each machine simulates the crash itself" if ap == "A" else "the machine that hits first simulates the whole crash"), flush=True)
                try: cur = onward(run_mp(b, L, k, cur[0], cur[1], yaw))
                except RuntimeError as e:   # one crash's setup failing must not lose the others
                    verdict(f"{k.upper()} MP [{ap}]", False, f"aborted: {e}"); d = unit(yaw); cur = (cur[0] + d[0] * 300, cur[1] + d[1] * 300)
                for s in (b, L):
                    if g(s, "mainscript.s.player.Bsitting") is True: s("driveout"); time.sleep(1)
            for s in (b, L): s("set TLDRevamp.Net.Entities.PhysicalCopies True"); s("set TLDRevamp.Net.Entities.ContactRelayOn False"); s("set TLDRevamp.Net.Entities.CrashAuthority False")
            L("mp stop"); b("mp stop")
        for s in [b] + ([L] if L else []): s("set kaposztaleves.s.settings.god False")
finally:
    try:
        if RELAY and L: b("net relay off"); L("net relay off")
    except Exception: pass
    if L: L.close()
print("\nCRASH TEST:", "ALL PASS" if verdicts and all(verdicts.values()) else "FAILURES", {k: ("PASS" if v else "FAIL") for k, v in verdicts.items()})
