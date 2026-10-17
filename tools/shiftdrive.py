#!/usr/bin/env python3
"""Two players driving across floating-origin shifts (every ~2.5 km each machine moves its whole world back by ~2 km).
The copy code re-seats every copy on a shift (Entities.Physical SeatCopiesAfterRebase), the interpolator snaps beyond
100 m + 150 m/s per gap, the copy body teleports past 50 m per physics step — all made for teleports (farworld).
Does normal driving trip any of it? Over the Steam relay (iad by default): host and laptop each drive their own car01
with the test driver (autodrive) along the main road, the laptop's car behind the host's, for --secs seconds.
Every 2 s on each machine: its own car (global), its copy of the other car, its floating origin.
Verdicts:
  - shifts happened : each machine shifted at least --min-shifts times while driving
  - no copy jumps   : copy sweeps, copy teleports, interpolator snaps unchanged (normal shifts must not look like a jump)
  - copies on track : every copy within 2.5 m of the owner's own track (cross-track: the line through the owner's
                      samples; the copy runs behind by the delay, along the track is not counted)
  - nobody died, both drove ≥ 70 % of the distance at the target speed
  --passenger: the laptop rides in the host's car instead (one driver): its world shifts while the copy it sits in
               moves — it must stay seated, on the copy's seat, the copy on the host's track
  --insane: Killenger's Autopilot (test tooling port) drives instead of the test driver, in its INSANE mode (250 km/h
            target, rocks driven over, the shoulder used) — the hardest normal driving the copies will see. Needs the
            game's automatic gearbox: switched on for the test, the player's setting put back after.
            The autopilot parks by design where the road is too cluttered to plan through (buildings: "road blocked",
            state Stopped) and waits for the player: the test then does what a player does — drives past with the
            test driver for --takeover seconds and engages it again. Counted, not failed.
  --edge: start where a shift comes soon — the game shifts when the player is 2500 m from its floating origin
          (mainscript.VisszaRakas), then puts it 2000 m on the other side: the next shift needs ~4.5 km of straight
          progress. Around the seed's spawn the road loops (9 km driven, never past 1526 m — run 7). Both players
          travel to the main road ~2300 m from the origin and the drive goes outward. Default with --insane.
  --autopilot Normal|Crazy|Insane|Offroad: the autopilot in that mode (--insane = --autopilot Insane). Insane drives
            over rocks by design and beached the car on one (run 11): the shift checks use Normal.
            Takeovers never back off (DriveLab.Recover off): a back-off turned the car round (run 11, -178 deg).
  tools/shiftdrive.py [--no-build] [--insane] [--autopilot MODE] [--edge|--no-edge] [--relay iad|off] [--secs 420] [--kmh 80] [--min-shifts 3] [--passenger] [--forward]"""
import sys, time, json, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP
from roadkit import CAR01, until, g, car_ready, clear_road

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY, SECS, KMH, MIN_SHIFTS = arg("--relay", "iad"), arg("--secs", 420.0, float), arg("--kmh", 80.0, float), arg("--min-shifts", 3, int)
if RELAY == "off": RELAY = None
SPEED = KMH / 3.6
PASSENGER = "--passenger" in sys.argv
AP_MODE = arg("--autopilot", "Insane" if "--insane" in sys.argv else None)   # Normal | Crazy | Insane | Offroad
INSANE = AP_MODE is not None   # (the name: the autopilot drives)
if INSANE and "--secs" not in sys.argv: SECS = 300.0
TAKEOVER = arg("--takeover", 20.0, float)
EDGE_ANGLES = [float(a) for a in arg("--edge-angles", "0,90,180,270,45,135,225,315").split(",")]   # run 13: 0° has
# buildings ~400 m on (both drivers stuck there; the passenger run got past only with a back-off)
EDGE = ("--edge" in sys.argv or INSANE) and "--no-edge" not in sys.argv
BACK = "--forward" not in sys.argv   # seed 20260924: 1.74 km forward from spawn the road is blocked (the driver sticks at 443,-300)

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def unit(yaw): return (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))

def cross_track(p, track):
    """distance from p (x, z) to the polyline through track [(t, x, z)], nearest segment"""
    best = None
    for (_, ax, az), (_, bx, bz) in zip(track, track[1:]):
        vx, vz = bx - ax, bz - az; L2 = vx * vx + vz * vz
        u = 0.0 if L2 < 1e-6 else max(0.0, min(1.0, ((p[0] - ax) * vx + (p[1] - az) * vz) / L2))
        d = math.hypot(p[0] - (ax + u * vx), p[1] - (az + u * vz))
        if best is None or d < best: best = d
    return best

AP = "TLDAutopilot.Plugin.Host."
def autopilot_on(s, who):
    """the way a player does it: drive off a few metres (our driver: auto gearbox, holding throttle starts the engine),
    then engage the autopilot rolling forward, in insane mode (engaging at a standstill it refused: 'rolling backwards')"""
    s(f"set {AP}Settings.Mode {AP_MODE}")
    p0 = s("gpos"); s("autodrive on 6")
    moved = lambda: (q := s("gpos")) and math.hypot(q["gx"] - p0["gx"], q["gz"] - p0["gz"]) > 20
    if not until(moved, 30, 0.5): print(f"  {who}: did not get going ({s('autodrive')})", flush=True)
    s("autodrive off")
    print(f"  {who} autopilot:", s(f"call {AP}Session.Toggle"), "mode", g(s, AP + "Settings.Mode"),
          "target", g(s, AP + "Settings.InsaneTargetKmh"), "state", g(s, AP + "Ap.State"), flush=True)

def autopilot_off(s):
    try:
        st = g(s, AP + "Ap.State")
        if st in ("Engaged", "Paused"): s(f"call {AP}Session.Toggle")
    except Exception: pass
    try: s(f"set {AP}Settings.Mode Normal")
    except Exception: pass

def in_world(s):
    try: return s("get DataFromMenuScript.s.mainmenu")["value"] is False and s("get mainscript.s.player")["value"] is not None
    except Exception: return False

def travel(s, gx, gz, who):
    """teleport to a GLOBAL point and stand on generated ground (as farworld.py travel)"""
    s("set mainscript.s.player.godModeTillGrounded True")
    s(f"tpg {gx} {gz} hold")
    def grounded():
        q = s("gpos")
        return "y" in s(f"ground {q['ux']} {q['uz']}")
    ok = until(grounded, 90, 1.0)
    if not ok: s("tprelease"); raise RuntimeError(f"{who}: no ground at {gx:.0f},{gz:.0f}")
    r = s(f"tpground {gx} {gz}")
    if r.get("tpground") == "hop": until(grounded, 30, 1.0); s(f"tpground {gx} {gz}")
    time.sleep(8)
    if not in_world(s): raise RuntimeError(f"{who}: left the world travelling to {gx:.0f},{gz:.0f}")
    q = s("gpos"); print(f"  {who} at global {q['gx']:.0f},{q['gz']:.0f}", flush=True)

def edge_start(b, L, r=2300.0):
    """the main road ~r m from the floating origin, and the road direction pointing away from the origin"""
    q = b("gpos"); ox, oz = q["gx"] - q["ux"], q["gz"] - q["uz"]   # global position of Unity 0
    for th in EDGE_ANGLES:
        tx, tz = ox + r * math.sin(math.radians(th)), oz + r * math.cos(math.radians(th))
        travel(b, tx, tz, f"desktop (edge {th}°)")
        try: rp = b("roadpoint")
        except RuntimeError as ex: print(f"  ({th}°: {ex})", flush=True); continue   # run 14: no road near 90°
        if not rp.get("main"): continue
        rad = math.hypot(rp["gx"] - ox, rp["gz"] - oz)
        if not 1900 <= rad <= 2450: continue
        y = rp["yaw"]; d = unit(y)
        if d[0] * (rp["gx"] - ox) + d[1] * (rp["gz"] - oz) < 0: y += 180.0
        print(f"  start: main road {rad:.0f} m from the origin, driving outward (yaw {y:.0f})", flush=True)
        # beside the road: the host's car is spawned ON the road point (run 8: spawned on the laptop's player, which was
        # flung — a fall-damage death, the respawn then took it out of the seat it had just been given)
        R = (d[1], -d[0])
        travel(L, rp["gx"] + R[0] * 8, rp["gz"] + R[1] * 8, "laptop (beside the start)")
        # and the host off it too (run 10: the host was flung the same way — a fall-damage death, out of the seat)
        b(f"tpground {rp['gx'] - R[0] * 8} {rp['gz'] - R[1] * 8}"); time.sleep(3)
        return rp, y
    sys.exit("no main road near the origin's edge")

def track_len(tr): return sum(math.dist(a[1:], b[1:]) for a, b in zip(tr, tr[1:]))

def counters(s):
    ph = s("mp physical")
    return {k: ph.get(k, 0) for k in ("copySweeps", "copyTeleports", "ipSnaps", "copyRebaseSeats")} | {
        "deaths": s("get TLDRevamp.Diagnostics.DeathLog.Deaths")["value"],
        "ownerJumps": s("get TLDRevamp.Net.Entities.OwnerJumps")["value"]}

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        b = seeded_both(L)
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        shift0 = {n: g(s, "settingsscript.s.S.BAutoShift") for n, s in (("desktop", b), ("laptop", L))}
        if INSANE:
            for s in (b, L): s("set settingsscript.s.S.BAutoShift True")
        if RELAY: print("relay only via", RELAY, b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
        b("mp host 27070")
        if RELAY: L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("route:", L("net route"), flush=True)
        time.sleep(5)
        if EDGE: rp, yaw = edge_start(b, L)
        else: rp = b("roadpoint"); yaw = rp["yaw"] + (180.0 if BACK else 0.0)
        D = unit(yaw)
        hx, hz = rp["gx"], rp["gz"]
        lx, lz = hx - D[0] * 40, hz - D[1] * 40
        ok_h = car_ready(b, hx, hz, yaw, "desktop (car A)")
        me = {"desktop": b("mp entities")["me"], "laptop": L("mp entities")["me"]}
        def own_car(s, owner):
            """the car this machine's player sits in: its car nearest the player (not the first one it owns — that is the
            spawn house's starter car, net 1: passenger runs 2–5 sat the laptop in it, parked, while the host drove)"""
            gp = s("gpos")
            cs = [e for e in s("mp entities")["entities"] if e["car"] and e["owner"] == owner and not e["dormant"]]
            c = min(cs, key=lambda e: math.hypot(e["pos"][0] - gp["gx"], e["pos"][2] - gp["gz"]), default=None)
            if c and math.hypot(c["pos"][0] - gp["gx"], c["pos"][2] - gp["gz"]) > 5: c = None
            return c
        if PASSENGER:
            time.sleep(3)
            hc = own_car(b, me["desktop"])
            if not ok_h or not hc: sys.exit("host car not ready")
            b("brakecar"); b("handbrake on"); time.sleep(2)   # run 9: the car rolled 28 m down the slope while the laptop stepped in
            for _ in range(3):
                print("  passenger:", L(f"mp sitpassenger {hc['net']}"), flush=True); time.sleep(2)
                if g(L, "mainscript.s.player.Bsitting") is True: break
            if g(L, "mainscript.s.player.Bsitting") is not True: sys.exit("laptop not seated as passenger")
            net = {"desktop": hc["net"], "laptop": hc["net"]}
        else:
            ok_l = car_ready(L, lx, lz, yaw, "laptop (car B, 40 m behind)")
            if not (ok_h and ok_l): sys.exit("cars not ready")
            time.sleep(3)
            cars = {n: own_car(s, me[n]) for n, s in (("desktop", b), ("laptop", L))}
            if not all(cars.values()): sys.exit(f"own cars not found: {cars}")
            net = {n: c["net"] for n, c in cars.items()}
        print("cars:", net, "player seated:", g(b, "mainscript.s.player.Bsitting"), g(L, "mainscript.s.player.Bsitting"), flush=True)
        c0 = {"desktop": counters(b), "laptop": counters(L)}
        print("counters before:", c0, flush=True)
        if INSANE:
            autopilot_on(b, "desktop")
            if not PASSENGER: autopilot_on(L, "laptop")
        else:
            print("drive:", b(f"autodrive on {SPEED}"), "" if PASSENGER else L(f"autodrive on {SPEED}"), flush=True)
        seat_off, unseated = [], 0
        drivers = {"desktop": b} if PASSENGER else {"desktop": b, "laptop": L}
        takeover_until, takeovers = {n: 0.0 for n in drivers}, {n: [] for n in drivers}
        sides = {"desktop": b, "laptop": L}
        other = {"desktop": "laptop", "laptop": "desktop"}
        track = {"desktop": [], "laptop": []}     # owner's own car samples (t, gx, gz)
        copies = {"desktop": [], "laptop": []}    # (t, gx, gz) of the OTHER car as this machine shows it
        origins = {"desktop": set(), "laptop": set()}
        radius = {"desktop": 0.0, "laptop": 0.0}   # farthest the player got from its floating origin (shift at 2500 m)
        t0 = time.time()
        while time.time() - t0 < SECS:
            for n, s in sides.items():
                t = time.time() - t0
                ents = {e["net"]: e for e in s("mp entities")["entities"]}
                mine, theirs = ents.get(net[n]), ents.get(net[other[n]])
                if mine and not mine["dormant"]: track[n].append((t, mine["pos"][0], mine["pos"][2]))
                if theirs and not theirs["dormant"]: copies[n].append((t, theirs["pos"][0], theirs["pos"][2]))
                o = s("get mainscript.s.visszarakva")["value"]
                origins[n].add(str(o))
                try:
                    up = [float(v) for v in str(s("get mainscript.s.player.transform.position")["value"]).split(",")]
                    radius[n] = max(radius[n], math.hypot(up[0], up[2]))
                except Exception: pass
                if PASSENGER and n == "laptop" and theirs:
                    gp = s("gpos")
                    seat_off.append(math.hypot(gp["gx"] - theirs["pos"][0], gp["gz"] - theirs["pos"][2]))
                    if g(s, "mainscript.s.player.Bsitting") is not True: unseated += 1
            if INSANE:
                for n, s in drivers.items():
                    now = time.time() - t0
                    own = track["desktop" if PASSENGER else n]
                    if takeover_until[n] and now >= takeover_until[n]:
                        s("autodrive off"); takeover_until[n] = 0.0
                        e = next((e for e in s("mp entities")["entities"] if e["net"] == net[n]), None)
                        turn = ((e["yaw"] - takeovers[n][-1][3] + 180) % 360 - 180) if e and takeovers[n][-1][3] is not None else None
                        takeovers[n][-1] = takeovers[n][-1][:3] + (turn,)
                        if turn is not None and abs(turn) > 120: print(f"  !! {n}: TURNED AROUND during the takeover ({turn:.0f} deg)", flush=True)
                        autopilot_on(s, n + " (again)")
                    elif not takeover_until[n] and g(s, AP + "Ap.State") == "Stopped":
                        why = g(s, AP + "Ap._reason")
                        e = next((e for e in s("mp entities")["entities"] if e["net"] == net[n]), None)
                        takeovers[n].append((round(now), why, own[-1][1:] if own else None, e["yaw"] if e else None))
                        print(f"  t={now:.0f}s {n}: autopilot parked ({why}) — the player drives past", flush=True)
                        s(f"call {AP}Session.Toggle"); s("set TLDRevamp.DriveLab.Recover False"); s("autodrive on 8"); takeover_until[n] = now + TAKEOVER
            time.sleep(2)
        if INSANE:
            for s in sides.values(): autopilot_off(s)
            for s in sides.values():
                try: s("brakecar")
                except Exception: pass
        drive = {n: s("autodrive off") for n, s in sides.items()}
        if PASSENGER: drive["laptop"] = drive["desktop"]
        print("drive:", drive, flush=True)
        c1 = {"desktop": counters(b), "laptop": counters(L)}
        dump = pathlib.Path("/tmp/claude-1000/shiftdrive-track.json")
        try:
            dump.parent.mkdir(parents=True, exist_ok=True)
            dump.write_text(json.dumps({"track": track, "copies": copies, "takeovers": takeovers if INSANE else None}))
            print("track written to", dump, flush=True)
        except Exception as ex: print("track not written:", ex, flush=True)
        print("counters after:", c1, flush=True)

        shifts = {n: len(origins[n]) - 1 for n in sides}
        verdict("SHIFTS HAPPENED", all(v >= MIN_SHIFTS for v in shifts.values()), f"origin shifts while driving: {shifts}; farthest from the origin (m): { {n: round(r) for n, r in radius.items()} } (a shift at 2500)")
        jumps = {n: {k: c1[n][k] - c0[n][k] for k in ("copySweeps", "copyTeleports", "ipSnaps", "ownerJumps")} for n in sides}
        verdict("NO COPY JUMPS ACROSS SHIFTS", all(v == 0 for d in jumps.values() for v in d.values()),
                f"{jumps}; copies re-seated on shifts: { {n: c1[n]['copyRebaseSeats'] - c0[n]['copyRebaseSeats'] for n in sides} }")
        errs = {}
        for n in sides:
            if PASSENGER and n == "desktop": continue   # the host owns the only car
            tr = track["desktop"] if PASSENGER else track[other[n]]
            ds = [d for (t, x, z) in copies[n] if (d := cross_track((x, z), [p for p in tr if abs(p[0] - t) < 8])) is not None]
            errs[n] = (round(max(ds), 2) if ds else None, round(sorted(ds)[len(ds) // 2], 2) if ds else None, len(ds))
        verdict("COPIES ON THE OWNER'S TRACK", all(e[0] is not None and e[0] <= 2.5 for e in errs.values()),
                f"cross-track max/median/samples (m) {errs}")
        if PASSENGER:
            verdict("PASSENGER STAYS IN ITS SEAT ACROSS SHIFTS", unseated == 0 and seat_off and max(seat_off) < 3.0,
                    f"unseated samples {unseated}, player to car (m) max {round(max(seat_off), 2) if seat_off else None} over {len(seat_off)} samples")
        dist = {n: json.loads(json.dumps(drive[n])).get("distance") for n in sides}
        if INSANE: dist = {n: round(track_len(track["desktop" if PASSENGER else n])) for n in sides}
        deaths = {n: c1[n]["deaths"] - c0[n]["deaths"] for n in sides}
        exp = SPEED * SECS if not INSANE else 2500 * MIN_SHIFTS / 0.7   # insane: enough for the shifts asked for
        if INSANE: print(f"  autopilot parked and the player drove past: { {n: len(v) for n, v in takeovers.items()} } {takeovers}", flush=True)
        verdict("DROVE, NOBODY DIED", all(deaths[n] == 0 and float(dist[n] or 0) >= 0.7 * exp for n in sides),
                f"distance {dist} (expected ~{exp:.0f} m), deaths {deaths}")
        for s in (b, L):
            try: s("driveout")
            except Exception: pass
        L("mp stop"); b("mp stop")
        for n, s in (("desktop", b), ("laptop", L)):
            if shift0[n] is not None: s(f"set settingsscript.s.S.BAutoShift {shift0[n]}")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")
