#!/usr/bin/env python3
"""A resting car's copy that stays off its owner's pose (the open bug "a car that rolled down a slope rests 1.9 m off
on copies", NOTES 2026-10-14: playround's home point, the laptop's car rolled ~45 m down after its driver got out).
Two machines over the relay. The host keeps a car of its own by the slope, so its copy of the laptop's car is PHYSICAL
(Entities.Physical: a real body steered along the owner's path; NearEnterM/NearExitM raised for the test so the copy
stays physical for the whole roll). On --rolls slopes found near the road (a grade of 10-30 % on terrain), the laptop
spawns a car01 at the top facing downhill, gets out with the handbrake off, and the car rolls until it stops. Then
where the laptop has it and where the host's copy rests are compared. OLD (Entities.RestSnap off) and NEW, the same
slopes in turn.
  A  the host's copy is physical during the rolls
  B  OLD: the copy rests > 0.3 m off at least once (the bug; INFO only if it never shows)
  C  NEW: within 0.3 m every time
  D  no errors
  tools/restcopy.py [--no-build] [--relay iad|off] [--rolls 3]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, tp, car_ready, CAR01

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, ROLLS = arg("--relay", "iad"), arg("--rolls", 3, int)
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}

def gy(s, x, z):
    try:
        r = s(f"groundg {x} {z}")
        return (r["gy"], r["on"]) if r["gy"] is not None else (None, "")
    except Exception: return None, ""

def slopes(s, x0, z0, n, reach=150, step=25, d=12):
    """up to n spots near (x0, z0), 60 m or more apart, on terrain sloping 10-30 % (steepest first): (x, z, downhill yaw)"""
    found = []
    for i in range(-reach, reach + 1, step):
        for k in range(-reach, reach + 1, step):
            x, z = x0 + i, z0 + k
            hs = [gy(s, x + a, z + b) for a, b in ((d, 0), (-d, 0), (0, d), (0, -d), (0, 0))]
            if any(h is None or "Terrain" not in on for h, on in hs): continue
            gx_, gz_ = (hs[0][0] - hs[1][0]) / (2 * d), (hs[2][0] - hs[3][0]) / (2 * d)
            grade = math.hypot(gx_, gz_)
            if 0.10 <= grade <= 0.30: found.append((grade, x, z, math.degrees(math.atan2(-gx_, -gz_))))
    found.sort(reverse=True); out = []
    for grade, x, z, yaw in found:
        if all(math.hypot(x - a, z - b) >= 60 for a, b, _ in out): out.append((x, z, yaw)); print(f"    slope {grade:.2f} at {x:.0f},{z:.0f} downhill {yaw:.0f}°", flush=True)
        if len(out) == n: break
    return out

def roll(h, L, x, z, yaw, label):
    F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
    # the host and its own car beside the slope's foot, off the car's way
    hx, hz = x + F[0] * 25 + F[1] * 14, z + F[1] * 25 - F[0] * 14
    tp(h, hx + 4, hz); time.sleep(2)
    print(f"  {label}: host car", h(f"spawncarg {CAR01} 0 {hx} {hz} {yaw}"), flush=True); time.sleep(3)
    tp(L, x - F[0] * 6, z - F[1] * 6); time.sleep(3)
    before = set(ents(L))
    if not car_ready(L, x, z, yaw, f"{label} laptop"): return None
    L("handbrake on"); time.sleep(1)
    me = L("mp players")["me"]
    net = min((e for e in ents(L).values() if e["net"] not in before and e["part"] < 0 and e["car"] and not e["proxy"]),
              key=lambda e: math.dist(e["pos"], me), default={}).get("net")
    if net is None: return None
    if not until(lambda: net in ents(h), 15): return None
    L("handbrake off"); L("driveout"); time.sleep(1)
    tp(L, x - F[0] * 10 + F[1] * 8, z - F[1] * 10 - F[0] * 8)   # out of its way
    p0, last, still_since, t0 = ents(L)[net]["pos"], None, None, time.time()
    while time.time() - t0 < 70:
        time.sleep(1)
        p = ents(L)[net]["pos"]
        if last is not None and math.dist(p, last) < 0.05:
            still_since = still_since or time.time()
            if time.time() - still_since > 5: break
        else: still_since = None
        last = p
    time.sleep(4)
    a, b = ents(L).get(net), ents(h).get(net)
    if not a or not b: return None
    off = math.dist(a["pos"], b["pos"])
    rolled = math.dist(a["pos"], p0)
    print(f"  {label}: rolled {rolled:.1f} m in {time.time() - t0:.0f} s; laptop {[round(v, 2) for v in a['pos']]}, host's copy {[round(v, 2) for v in b['pos']]}: {off:.2f} m", flush=True)
    return round(off, 2), round(rolled, 1)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
        h("set TLDRevamp.Net.Entities.NearEnterM 150"); h("set TLDRevamp.Net.Entities.NearExitM 200")
        rp = L("roadpoint"); tp(L, rp["gx"], rp["gz"]); time.sleep(4)
        spots = slopes(L, rp["gx"], rp["gz"], ROLLS)
        if not spots: sys.exit("NO SLOPE FOUND")
        res = {"OLD": [], "NEW": []}
        ph0 = h("mp physical")
        for i, (x, z, yaw) in enumerate(spots):
            for label, on in (("OLD", False), ("NEW", True)):
                h(f"set TLDRevamp.Net.Entities.RestSnap {on}")
                # the NEW roll a little beside the OLD one's start (the OLD car rests below)
                sx, sz = (x, z) if label == "OLD" else (x + math.cos(math.radians(yaw)) * 6, z - math.sin(math.radians(yaw)) * 6)
                r = roll(h, L, sx, sz, yaw, f"{label} slope {i + 1}")
                res[label].append(r)
        ph = h("mp physical")
        print("  host physical:", {k: (ph0.get(k), ph.get(k)) for k in ("enters", "exits", "restSnaps")}, flush=True)
        verdict("A THE HOST'S COPIES WERE PHYSICAL", (ph.get("enters") or 0) - (ph0.get("enters") or 0) >= len(spots), f"enters {ph0.get('enters')} -> {ph.get('enters')}")
        old = [r[0] for r in res["OLD"] if r]; new = [r[0] for r in res["NEW"] if r]
        bug = any(o > 0.3 for o in old)
        print(f"{'PASS' if bug else 'INFO'}  B OLD: THE COPY RESTS OFF ITS OWNER'S POSE  OLD {res['OLD']} (off m, rolled m)", flush=True)
        verdict("C NEW: THE COPY RESTS WHERE ITS OWNER HAS IT", len(new) == len(spots) and all(o <= 0.3 for o in new), f"NEW {res['NEW']}, restSnaps {ph.get('restSnaps')}")
        er = [x_("errors") for x_ in (h, L)]
        verdict("D NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        h("set TLDRevamp.Net.Entities.RestSnap True")
        h("set TLDRevamp.Net.Entities.NearEnterM 12"); h("set TLDRevamp.Net.Entities.NearExitM 18")
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
print(f"RESTCOPY: {sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} PASS")
