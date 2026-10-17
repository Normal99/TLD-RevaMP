#!/usr/bin/env python3
"""Short floating-origin shift crossings, two players over the Steam relay (iad by default).
The game moves the world when the player is 2500 m from the floating origin (mainscript.VisszaRakas). The copy code
has jump handling made for teleports (interpolator snap, copy teleport past 50 m per physics step, copy sweeps,
SeatCopiesAfterRebase) — a normal shift while driving must not trip any of it.
tools/shiftdrive.py drove 10 minutes to reach shifts and failed mostly on the driving (buildings, rocks, loops).
A shift is crossed in a second: here each crossing starts both cars ~100 m inside the 2500 m line on a main road,
heading outward (the host's car in front, the laptop's 40 m behind), the test driver drives them on (no back-off)
until both machines shifted (or --secs), and the counters are compared over that drive only (the setup teleports
between crossings are not part of it). Repeated in --crossings directions.
Per crossing:
  - both machines shifted
  - copy sweeps, copy teleports, interpolator snaps, owner jumps unchanged; copies re-seated by the shift code (info)
  - each machine's copy of the other car within 2.5 m of that car's own track (cross-track)
  - nobody died
  tools/shiftcross.py [--no-build] [--relay iad|off] [--crossings 3] [--secs 60] [--kmh 60]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP
from roadkit import until, g, car_ready

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY, CROSSINGS, SECS, KMH = arg("--relay", "iad"), arg("--crossings", 3, int), arg("--secs", 60.0, float), arg("--kmh", 60.0, float)
if RELAY == "off": RELAY = None
# every 20° (run 1: of 8 directions only 1 had the main road in the band — the rest no road or just outside it)
ANGLES = [float(a) for a in arg("--angles", ",".join(str(a) for a in (180, 260, 100, 340, 20, 200, 280, 120, 0, 240, 60, 300, 160, 220, 40, 320, 80, 140))).split(",")]
START_R = 2400.0   # 100 m inside the shift line

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def unit(yaw): return (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))

def cross_track(p, track):
    best = None
    for (_, ax, az), (_, bx, bz) in zip(track, track[1:]):
        vx, vz = bx - ax, bz - az; L2 = vx * vx + vz * vz
        u = 0.0 if L2 < 1e-6 else max(0.0, min(1.0, ((p[0] - ax) * vx + (p[1] - az) * vz) / L2))
        d = math.hypot(p[0] - (ax + u * vx), p[1] - (az + u * vz))
        if best is None or d < best: best = d
    return best

def counters(s):
    ph = s("mp physical")
    return {k: ph.get(k, 0) for k in ("copySweeps", "copyTeleports", "ipSnaps", "copyRebaseSeats")} | {
        "deaths": s("get TLDRevamp.Diagnostics.DeathLog.Deaths")["value"],
        "ownerJumps": s("get TLDRevamp.Net.Entities.OwnerJumps")["value"]}

def in_world(s):
    try: return s("get DataFromMenuScript.s.mainmenu")["value"] is False and s("get mainscript.s.player")["value"] is not None
    except Exception: return False

def travel(s, gx, gz, who, settle=6):
    """teleport to a GLOBAL point and stand on generated ground (as farworld.py travel)"""
    if g(s, "mainscript.s.player.Bsitting") is True: s("driveout"); time.sleep(1)
    s("set mainscript.s.player.godModeTillGrounded True")
    s(f"tpg {gx} {gz} hold")
    def grounded():
        q = s("gpos")
        return "y" in s(f"ground {q['ux']} {q['uz']}")
    if not until(grounded, 90, 1.0): s("tprelease"); raise RuntimeError(f"{who}: no ground at {gx:.0f},{gz:.0f}")
    r = s(f"tpground {gx} {gz}")
    if r.get("tpground") == "hop": until(grounded, 30, 1.0); s(f"tpground {gx} {gz}")
    time.sleep(settle)
    if not in_world(s): raise RuntimeError(f"{who}: left the world travelling to {gx:.0f},{gz:.0f}")

def origin(s):
    q = s("gpos"); return q["gx"] - q["ux"], q["gz"] - q["uz"]   # global position of Unity 0

def find_start(b, angles):
    """a main road point ~START_R from the host's origin and the outward road direction"""
    ox, oz = origin(b)
    while angles:
        th = angles.pop(0)
        travel(b, ox + START_R * math.sin(math.radians(th)), oz + START_R * math.cos(math.radians(th)), f"desktop ({th:.0f}°)")
        try: rp = b("roadpoint")
        except RuntimeError: print(f"  {th:.0f}°: no road near", flush=True); continue
        rad = math.hypot(rp["gx"] - ox, rp["gz"] - oz)
        if not rp.get("main") or not 2200 <= rad <= 2480: print(f"  {th:.0f}°: road {rad:.0f} m out, main {rp.get('main')} — next", flush=True); continue
        y = rp["yaw"]; d = unit(y)
        if d[0] * (rp["gx"] - ox) + d[1] * (rp["gz"] - oz) < 0: y += 180.0
        return th, rp, y, rad
    return None

def walk_road(b, yaw):
    """the next start along the road being driven: hop along it (≤500 m, the main road at each hop, keeping the
    direction) until ~START_R from the origin while moving outward. Run 2: after a shift the main road meets the
    2400 m circle around the new origin at one or two points — no direction search found one."""
    for hop in range(60):
        ox, oz = origin(b); q = b("gpos")
        rad = math.hypot(q["gx"] - ox, q["gz"] - oz); d = unit(yaw)
        outward = d[0] * (q["gx"] - ox) + d[1] * (q["gz"] - oz) > 0
        # 150 m: the road lookup only sees the chunks around the player (run 3: a 500 m hop off a curve lost the road)
        step = 150.0 if not outward else max(40.0, min(150.0, START_R - rad))
        travel(b, q["gx"] + d[0] * step, q["gz"] + d[1] * step, f"desktop (road hop {hop + 1})", settle=1.5)
        try: rp = b("roadpoint")
        except RuntimeError: print("  road hop: no road near — lost the road", flush=True); return None
        y = rp["yaw"]
        if unit(y)[0] * d[0] + unit(y)[1] * d[1] < 0: y += 180.0   # keep the direction of travel
        yaw = y
        rad = math.hypot(rp["gx"] - ox, rp["gz"] - oz); d = unit(yaw)
        outward = d[0] * (rp["gx"] - ox) + d[1] * (rp["gz"] - oz) > 0
        b(f"tpground {rp['gx']} {rp['gz']}")   # back on the road: the next hop follows it
        if outward and 2250 <= rad <= 2480 and rp.get("main"): return "road", rp, yaw, rad
        if outward and rad > 2480: print(f"  road hop: past the line ({rad:.0f} m) — lost it", flush=True); return None
    return None

def own_car(s):
    gp, me = s("gpos"), s("mp entities")["me"]
    cs = [e for e in s("mp entities")["entities"] if e["car"] and e["owner"] == me and not e["dormant"]]
    c = min(cs, key=lambda e: math.hypot(e["pos"][0] - gp["gx"], e["pos"][2] - gp["gz"]), default=None)
    return c if c and math.hypot(c["pos"][0] - gp["gx"], c["pos"][2] - gp["gz"]) < 5 else None

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        b = seeded_both(L)
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("relay only via", RELAY, b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
        b("mp host 27070")
        if RELAY: L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("route:", L("net route"), flush=True)
        for s in (b, L): s("set TLDRevamp.DriveLab.Recover False")
        sides = {"desktop": b, "laptop": L}
        other = {"desktop": "laptop", "laptop": "desktop"}
        angles = list(ANGLES)
        done = 0
        last_yaw = None
        while done < CROSSINGS:
            st = walk_road(b, last_yaw) if last_yaw is not None else None
            if st is None: st = find_start(b, angles)
            if st is None: print("no more directions with a main road at the line", flush=True); break
            th, rp, yaw, rad = st
            where = "along the road" if th == "road" else f"{th:.0f}°"
            D = unit(yaw); R = (D[1], -D[0])
            print(f"\nCROSSING {done + 1} ({where}): main road {rad:.0f} m from the origin, driving outward (yaw {yaw:.0f})", flush=True)
            hx, hz = rp["gx"], rp["gz"]
            lx, lz = hx - D[0] * 40, hz - D[1] * 40
            # nobody stands where a car is spawned (shiftdrive runs 8/10: flung, a fall-damage death)
            travel(L, lx + R[0] * 8, lz + R[1] * 8, "laptop (beside its start)")
            b(f"tpground {hx - R[0] * 8} {hz - R[1] * 8}"); time.sleep(2)
            if not (car_ready(b, hx, hz, yaw, "desktop (front car)") and car_ready(L, lx, lz, yaw, "laptop (40 m behind)")):
                print("  cars not ready — next direction", flush=True); continue
            time.sleep(4)
            net = {}
            for n, s in sides.items():
                c = own_car(s)
                if not c: break
                net[n] = c["net"]
            if len(net) < 2: print(f"  own cars not found {net} — next direction", flush=True); continue
            o0 = {n: s("get mainscript.s.visszarakva")["value"] for n, s in sides.items()}
            c0 = {n: counters(s) for n, s in sides.items()}
            for s in sides.values(): s(f"autodrive on {KMH / 3.6}")
            track = {n: [] for n in sides}; copies = {n: [] for n in sides}
            shifted = {n: False for n in sides}; radius = {n: 0.0 for n in sides}
            t0 = time.time()
            while time.time() - t0 < SECS:
                for n, s in sides.items():
                    t = time.time() - t0
                    ents = {e["net"]: e for e in s("mp entities")["entities"]}
                    mine, theirs = ents.get(net[n]), ents.get(net[other[n]])
                    if mine and not mine["dormant"]: track[n].append((t, mine["pos"][0], mine["pos"][2]))
                    if theirs and not theirs["dormant"]: copies[n].append((t, theirs["pos"][0], theirs["pos"][2]))
                    if s("get mainscript.s.visszarakva")["value"] != o0[n]: shifted[n] = True
                    q = s("gpos"); radius[n] = max(radius[n], math.hypot(q["ux"], q["uz"]))
                if all(shifted.values()) and time.time() - t0 > 12: time.sleep(4); break   # a few seconds past it
                time.sleep(0.5)
            drove = {n: s("autodrive off") for n, s in sides.items()}
            for s in sides.values(): s("brakecar")
            c1 = {n: counters(s) for n, s in sides.items()}
            tag = f" [crossing {done + 1}, {where}]"
            verdict("BOTH SHIFTED" + tag, all(shifted.values()),
                    f"{shifted}; farthest from the origin (m) { {n: round(r) for n, r in radius.items()} }; driven (m) { {n: d.get('distance') for n, d in drove.items()} } in {time.time() - t0:.0f} s")
            jumps = {n: {k: c1[n][k] - c0[n][k] for k in ("copySweeps", "copyTeleports", "ipSnaps", "ownerJumps")} for n in sides}
            verdict("NO JUMPS" + tag, all(v == 0 for d in jumps.values() for v in d.values()),
                    f"{jumps}; copies re-seated by the shift code { {n: c1[n]['copyRebaseSeats'] - c0[n]['copyRebaseSeats'] for n in sides} }")
            errs = {}
            for n in sides:
                tr = track[other[n]]
                ds = [d for (t, x, z) in copies[n] if (d := cross_track((x, z), [p for p in tr if abs(p[0] - t) < 6])) is not None]
                errs[n] = (round(max(ds), 2) if ds else None, round(sorted(ds)[len(ds) // 2], 2) if ds else None, len(ds))
            verdict("COPIES ON THE OWNER'S TRACK" + tag, all(e[0] is not None and e[0] <= 2.5 for e in errs.values()),
                    f"cross-track max/median/samples (m): desktop's copy of the laptop's car {errs['desktop']}, laptop's copy of the host's {errs['laptop']}")
            deaths = {n: c1[n]["deaths"] - c0[n]["deaths"] for n in sides}
            verdict("NOBODY DIED" + tag, all(v == 0 for v in deaths.values()), f"{deaths}")
            done += 1
            last_yaw = yaw
        for s in (b, L):
            try: s("set TLDRevamp.DriveLab.Recover True"); s("driveout")
            except Exception: pass
        L("mp stop"); b("mp stop")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")
if not VERDICTS: print("  no crossing driven")
