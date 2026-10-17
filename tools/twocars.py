#!/usr/bin/env python3
"""Two machines, two cars, each driven by its own player's autopilot (Killenger's Autopilot, beta port — test tooling,
never in the public release; made traffic-aware by TLD Revamp's AutopilotBridge).
  A following : the laptop's car starts 40 m ahead at 45 km/h, the desktop's behind at 80 → the desktop follows at a safe
                gap (no collision, the traffic layer reports following, speed converges on the leader's)
  B oncoming  : the cars start ~250 m apart facing each other at 60 km/h → they pass keeping right, both autopilots stay
                engaged, closest approach at a safe distance, no collision
  A2 emergency brake: at the end of A the leader brakes hard → the follower stops in time (on a 170 ms old picture of
                the leader over the relay), the leader's copy doesn't overshoot where it really stopped
  Desync diagnostics (every scenario, both directions): each car's owner position vs its copy on the other machine,
  sampled ~10 Hz — the copy's LAG (best time shift; expected ≈ ping/2 + the interpolator's delay), the error left
  after that shift (shape: should be cm), the error at the same instant (what a player sees), heading error; the copy's
  smoothness (jumps > 20 cm/frame, extrapolated frames); the seated remote player staying in the copy's seat; the
  network (ping, quality, Steam send queues, KB/s); at rest: position/heading/parts/engine/fuel equal on both machines.
  tools/twocars.py [--no-build] [--only A,B] [--relay <dc>] [--follow-s 60]"""
import sys, time, math, json, statistics, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP
from roadkit import CAR01, until, g, road_at, clear_road, seat_idx, car_ready

ONLY = set(sys.argv[sys.argv.index("--only") + 1].split(",")) if "--only" in sys.argv else None
RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else None
KEEP_RIGHT = 1.8
FOLLOW_S = float(sys.argv[sys.argv.index("--follow-s") + 1]) if "--follow-s" in sys.argv else 60.0

def want(p): return ONLY is None or p in ONLY
verdicts = {}
def verdict(name, ok, detail=""):
    if RELAY: name += f" @relay:{RELAY}"
    verdicts[name] = ok
    print(f"{name}: {'PASS' if ok else 'FAIL'}  {detail}", flush=True)

def vec(s): return tuple(float(x) for x in s.split(","))

def engage(side, kmh, who):
    """as a player does it (the user's advice): drive off first, engage rolling, then let go of every driving input —
    our driver writes the game's input every frame, which the autopilot would take as a manual override"""
    side("autodrive on 12")   # starts the engine (auto gearbox), releases the handbrake, pulls away
    rolling = until(lambda: abs(float(g(side, "mainscript.s.player.Car.speed") or 0)) > 10, 20, 0.25)
    r = side("call TLDAutopilot.Plugin.Host.Session.Toggle")["value"]
    side("autodrive off")
    if not rolling: print(f"  {who}: never got rolling ({g(side, 'mainscript.s.player.Car.speed')} km/h)", flush=True)
    # engaging re-reads the autopilot's settings file (ApplyConfig): the test's speed and mode go in after it
    # CRAZY mode (the user's call: these beta roads are very curvy, the normal mode is too timid on them); its cruise is
    # CrazyTargetKmh — following still caps it (the external cap joins the governor's Sim cap, which applies in every mode)
    side("set TLDAutopilot.Plugin.Host.Settings.Mode Crazy")
    side(f"set TLDAutopilot.Plugin.Host.Settings.CrazyTargetKmh {kmh}"); side(f"set TLDAutopilot.Plugin.Host.Settings.TargetKmh {kmh}")
    print(f"  {who} autopilot at {g(side, 'TLDAutopilot.Plugin.Host.Settings.CrazyTargetKmh')} km/h, mode {g(side, 'TLDAutopilot.Plugin.Host.Settings.Mode')}:", r, flush=True)
    return "ENGAGED" in str(r)

def disengage(side):
    if g(side, "TLDAutopilot.Plugin.Host.Ap.State") not in ("Off", None): side("call TLDAutopilot.Plugin.Host.Session.Toggle")
    side("autodrive off")   # our test driver too (it releases the parking brake while it drives)
    side("brakecar"); side("handbrake on")

_mynet = {}
def my_car(side):
    """GLOBAL position of the car this machine's player drives (its synced entity)"""
    return other_car_pos(side, _mynet[id(side)])

def other_car_pos(side, net):
    e = {x["net"]: x for x in side("mp entities")["entities"]}.get(net)
    return tuple(e["pos"]) if e else None

def my_car_net(side):
    """the synced car nearest to this machine's player (global positions)"""
    me = side("mp players")["me"]
    net = min((e for e in side("mp entities")["entities"] if e["part"] < 0 and e["car"]), key=lambda e: math.dist(e["pos"][::2], me[::2]))["net"]
    _mynet[id(side)] = net
    return net


# ------------------------------------------------------------------ desync diagnostics
class Track:
    """~10 Hz samples of every synced car on both machines, time-stamped on THIS machine's clock (the midpoint of each
    bridge call: the laptop's calls go through the ssh tunnel, a few ms)"""
    def __init__(self, sides):
        self.sides = sides; self.rows = {id(s): [] for s in sides}; self.net = {id(s): [] for s in sides}
        self.players = {id(s): [] for s in sides}; self.n = 0
    def sample(self):
        for s in self.sides:
            t0 = time.monotonic(); r = s("mp entities"); t1 = time.monotonic()
            r["_callMs"] = (t1 - t0) * 1000   # the sample's time is the call's midpoint: ± half this
            self.rows[id(s)].append(((t0 + t1) / 2, {e["net"]: e for e in r["entities"]}, r))
        if self.n % 10 == 0:
            for s in self.sides:
                try:
                    st = s("net stats"); pl = s("mp players")
                    self.net[id(s)].append((time.monotonic(), st)); self.players[id(s)].append((time.monotonic(), pl, self.rows[id(s)][-1][1]))
                except Exception as e: print("   net stats:", e, flush=True)
        self.n += 1
    def last(self, side): return self.rows[id(side)][-1][1]

def _interp(series, t):
    """linear interpolation of [(t, (x, y, z))] at t (None outside)"""
    if not series or t < series[0][0] or t > series[-1][0]: return None
    lo, hi = 0, len(series) - 1
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if series[mid][0] <= t: lo = mid
        else: hi = mid
    (ta, a), (tb, b) = series[lo], series[hi]
    u = 0 if tb == ta else (t - ta) / (tb - ta)
    return tuple(a[k] + (b[k] - a[k]) * u for k in range(3))

SHOWN_M = 280   # copies farther than this from the viewer's car are not compared (the game stores them at 300 m)
TIGHT_MS = 10   # a copy sample counts as precisely timed when its bridge call took ≤ 2 × this

def _pct(xs, p):
    xs = sorted(xs); return xs[min(len(xs) - 1, int(len(xs) * p))] if xs else float("nan")

def desync(track, owner, viewer, net, label):
    """the copy of car `net` on `viewer` against the real car on `owner`"""
    own = [(t, tuple(e[net]["pos"])) for t, e, _ in track.rows[id(owner)] if net in e and not e[net]["proxy"]]
    # only while the viewer's game shows it: past itemRemoveDist (300 m, as in single player) the game stores the copy
    # away and its row keeps the last place (twocars A, 2026-10-08: the 11-16 m "errors" were all 312-324 m out)
    def near_viewer(e):
        mine = [v["pos"] for v in e.values() if v.get("car") and not v.get("proxy") and v.get("part", -1) < 0]
        return not mine or min(math.dist(e[net]["pos"][::2], m[::2]) for m in mine) < SHOWN_M
    cop_all = [(t, tuple(e[net]["pos"])) for t, e, _ in track.rows[id(viewer)] if net in e and e[net]["proxy"]]
    cop = [(t, tuple(e[net]["pos"])) for t, e, _ in track.rows[id(viewer)] if net in e and e[net]["proxy"] and near_viewer(e)]
    far_skipped = len(cop_all) - len(cop)
    oyaw = [(t, (e[net].get("yaw", 0), 0, 0)) for t, e, _ in track.rows[id(owner)] if net in e and not e[net]["proxy"]]
    cyaw = [(t, e[net].get("yaw", 0)) for t, e, _ in track.rows[id(viewer)] if net in e and e[net]["proxy"]]
    if len(own) < 10 or len(cop) < 10: return {"label": label, "error": f"too few samples (owner {len(own)}, copy {len(cop)})"}
    # the copy's samples whose time is known within ±TIGHT_MS (the laptop's calls cross the ssh tunnel over Wi-Fi: a
    # 90 ms call is ±45 ms, at 22 m/s ±1 m of "error" that is only the measurement)
    cms = {t: r_.get("_callMs", 0) for t, e, r_ in track.rows[id(viewer)]}
    cop_tight = [(t, q) for t, q in cop if cms.get(t, 0) <= 2 * TIGHT_MS]
    def errs(tau, cop=cop):
        out = []
        for t, q in cop:
            p = _interp(own, t - tau)
            if p is not None: out.append(math.dist(p[::2], q[::2]))
        return out
    best = min((statistics.median(errs(tau / 1000)), tau) for tau in range(0, 801, 5) if len(errs(tau / 1000)) > 5)
    tau = best[1] / 1000
    res, inst = errs(tau), errs(0)
    # in the owner's clock: the copy says which moment of the owner's clock it shows (rt), the owner's rows when they
    # were read (clk, its states' clock) — exact, whatever the bridge calls' timing
    own_clk = sorted((e[net]["clk"], tuple(e[net]["pos"])) for _, e, _ in track.rows[id(owner)] if net in e and not e[net]["proxy"] and "clk" in e[net])
    clk_err = []
    for _, e, _ in track.rows[id(viewer)]:
        if net in e and e[net]["proxy"] and "rt" in e[net] and near_viewer(e):
            p = _interp(own_clk, e[net]["rt"])
            if p is not None: clk_err.append(math.dist(p[::2], e[net]["pos"][::2]))
    tight = errs(tau, cop_tight)
    # where the worst ones are (seconds into the track; owner speed there): real glitches, or the test's own moments
    t00 = track.rows[id(viewer)][0][0]
    worst = []
    for t, q in cop:
        p = _interp(own, t - tau)
        if p is None: continue
        pb = _interp(own, t - tau - 0.1)
        spd = math.dist(p[::2], pb[::2]) / 0.1 if pb else -1
        vrow_t = next((e for tt, e, _ in track.rows[id(viewer)] if tt == t), {})
        mine = [v["pos"] for v in vrow_t.values() if v.get("car") and not v.get("proxy") and v.get("part", -1) < 0]
        dv = round(min(math.dist(q[::2], m[::2]) for m in mine)) if mine else -1
        worst.append((round(math.dist(p[::2], q[::2]), 2), round(t - t00, 1), round(spd, 1), round(cms.get(t, 0)), dv))
    worst.sort(reverse=True)
    calls = sorted(cms.values())
    yerr = []
    for t, y in cyaw:
        p = _interp(oyaw, t - tau)
        if p is not None: yerr.append(abs((y - p[0] + 180) % 360 - 180))
    vrow = track.rows[id(viewer)][-1][1].get(net, {})
    delay = statistics.median([e[net]["delayMs"] for _, e, _ in track.rows[id(viewer)] if net in e and "delayMs" in e[net]] or [float("nan")])
    jit = statistics.median([e[net].get("jitterMs", 0) for _, e, _ in track.rows[id(viewer)] if net in e and "delayMs" in e[net]] or [float("nan")])
    return {"label": label, "lagMs": round(tau * 1000), "interpDelayMs": round(delay), "jitterMs": round(jit),
            "shapeErr_m": (round(statistics.median(res), 2), round(_pct(res, 0.95), 2), round(max(res), 2)),
            "sameInstantErr_m": (round(statistics.median(inst), 2), round(_pct(inst, 0.95), 2), round(max(inst), 2)),
            "headingErr_deg": (round(statistics.median(yerr), 1), round(_pct(yerr, 0.95), 1), round(max(yerr), 1)) if yerr else None,
            "shapeErrTight_m": (round(statistics.median(tight), 2), round(_pct(tight, 0.95), 2), round(max(tight), 2), len(tight)) if len(tight) > 5 else None,
            "worst(err m, t s, owner m/s, call ms, m from viewer's car)": worst[:6],
            "farSkipped": far_skipped,
            "clockErr_m": (round(statistics.median(clk_err), 2), round(_pct(clk_err, 0.95), 2), round(max(clk_err), 2), len(clk_err)) if len(clk_err) > 5 else None,
            "viewerCallMs": (round(_pct(calls, 0.5)), round(_pct(calls, 0.95)), round(max(calls))) if calls else None,
            "jumps": vrow.get("jumps"), "maxDevCm": vrow.get("maxDevCm"), "extrapFrames": vrow.get("extrapFrames"), "frames": vrow.get("frames")}

def net_summary(track, side, who):
    rows = track.net[id(side)]
    if len(rows) < 2: return {"who": who, "error": "no samples"}
    pings = [c["ping"] for _, st in rows for c in st["conns"] if c["ping"] >= 0]
    ql = [c["qualityLocal"] for _, st in rows for c in st["conns"]]
    pend = [c["pendingReliable"] + c["pendingUnreliable"] for _, st in rows for c in st["conns"]]
    unack = [c["unackedReliable"] for _, st in rows for c in st["conns"]]
    q = [c["queueUs"] for _, st in rows for c in st["conns"]]
    (t0, a), (t1, z) = rows[0], rows[-1]; dt = max(1e-3, t1 - t0)
    return {"who": who, "ping": (min(pings), round(statistics.mean(pings)), max(pings)) if pings else None,
            "worstQuality": round(min(ql), 3) if ql else None, "maxPendingBytes": max(pend) if pend else None,
            "maxUnackedReliable": max(unack) if unack else None, "maxQueueMs": round(max(q) / 1000, 1) if q else None,
            "down_KBps": round((z["bytesIn"] - a["bytesIn"]) / 1024 / dt, 1), "up_KBps": round((z["bytesOut"] - a["bytesOut"]) / 1024 / dt, 1),
            "sendErrors": z["sendErrors"] - a["sendErrors"]}

def seat_check(track, viewer, net_of_remote_car):
    """the remote player's body on `viewer` sits in the copy of their car (parented to its seat): distance body → car"""
    ds = []; t0 = track.players[id(viewer)][0][0] if track.players[id(viewer)] else 0
    for tt, pl, ents in track.players[id(viewer)]:
        car = ents.get(net_of_remote_car)
        for p in pl["players"]:
            # the game's own same-frame distance (v0.65.22); the old two-call measure as a fallback
            if p["seatNet"] == net_of_remote_car and p["has"] and p.get("seatCarDist", -1) >= 0: d, src = p["seatCarDist"], "frame"
            elif car and p["seatNet"] == net_of_remote_car and p["has"]: d, src = math.dist(p["shown"], car["pos"]), "two calls"
            else: continue
            ds.append(d)
            if d > 2: print(f"   SEAT outlier t+{tt - t0:.1f}s: {d:.2f} m ({src}), seated {p['seated']} seatNet {p['seatNet']} idx {p['seatIdx']}", flush=True)
    return (round(min(ds), 2), round(max(ds), 2), len(ds)) if ds else None

def rest_check(owner, viewer, net, who):
    """both at rest: the copy where the car is, same heading, same parts bolted on, same engine and fuel"""
    o = {e["net"]: e for e in owner("mp entities")["entities"]}.get(net); c = {e["net"]: e for e in viewer("mp entities")["entities"]}.get(net)
    if not o or not c: return {"who": who, "error": "missing"}
    d = {"who": who, "pos_m": round(math.dist(o["pos"], c["pos"]), 3), "yaw_deg": round(abs((o.get("yaw", 0) - c.get("yaw", 0) + 180) % 360 - 180), 2),
         "upy": (o["upy"], c["upy"]), "attached": (o["attached"], c["attached"]), "engine": (o.get("engRunning"), c.get("engRunning")),
         "fuel": (o.get("fuel"), c.get("fuel"))}
    fuel_ok = o.get("fuel") is None or c.get("fuel") is None or abs(float(o["fuel"]) - float(c["fuel"])) < 0.5
    d["ok"] = d["pos_m"] < 0.15 and d["yaw_deg"] < 1.5 and o["attached"] == c["attached"] and fuel_ok
    return d

def wheel_sample(acc, cars):
    """each car's wheel meshes on its owner vs its copy on the other machine, in the car's frame: height (suspension),
    steer, the tyre in each slot (playtest 2026-10-09: "tires desynced when both drive")"""
    for owner, viewer, net, who in cars:
        try: o, c = owner(f"mp wheelpose {net}"), viewer(f"mp wheelpose {net}")
        except Exception as ex: print("   wheelpose:", ex, flush=True); continue
        d = acc.setdefault(who, {"dy": [], "ds": [], "tyres": set(), "n": 0})
        d["n"] += 1
        for i, (wo, wc) in enumerate(zip(o.get("wheels", []), c.get("wheels", []))):
            if wo is None or wc is None:
                if wo != wc: d["tyres"].add((i, str(wo and wo[4]), str(wc and wc[4])))
                continue
            if wo[4] != wc[4]: d["tyres"].add((i, wo[4], wc[4]))
            if not wo[4]: continue   # an empty slot: nothing drawn there
            d["dy"].append(abs(wo[1] - wc[1]) * 100)
            d["ds"].append(abs((wo[3] - wc[3] + 180) % 360 - 180))
        if len(o.get("wheels", [])) != len(c.get("wheels", [])): d["tyres"].add(("count", len(o.get("wheels", [])), len(c.get("wheels", []))))

def wheel_verdict(acc, tag):
    for who, d in acc.items():
        dy, ds = sorted(d["dy"]), sorted(d["ds"])
        if not dy: verdict(f"{tag} WHEELS ({who}): THE COPY'S AS THE OWNER'S", False, "no samples"); continue
        q = lambda xs, p: xs[min(len(xs) - 1, int(p * len(xs)))]
        ok = not d["tyres"] and q(dy, 0.5) < 3 and q(dy, 0.9) < 6 and q(ds, 0.5) < 3 and q(ds, 0.9) < 8
        verdict(f"{tag} WHEELS ({who}): THE COPY'S AS THE OWNER'S", ok,
                f"{d['n']} samples, height diff median {q(dy, 0.5):.1f} p90 {q(dy, 0.9):.1f} max {dy[-1]:.1f} cm; steer diff median {q(ds, 0.5):.1f} p90 {q(ds, 0.9):.1f} max {ds[-1]:.1f} deg; tyres differ {sorted(map(str, d['tyres']))}")

def report(track, pairs, sides_named):
    """pairs: (owner side, viewer side, net, label). Prints everything; returns (desync rows, net rows)"""
    ds = [desync(track, o, v, n, lab) for o, v, n, lab in pairs]
    for d in ds: print("   DESYNC", json.dumps(d), flush=True)
    ns = [net_summary(track, s, w) for s, w in sides_named]
    for n in ns: print("   NET", json.dumps(n), flush=True)
    return ds, ns

def desync_ok(d, max_lag_ms):
    # the owner's-clock error when the build reports it (exact); the bridge-timed shape error otherwise (its timing
    # noise alone is ±0.5 m at 20 m/s: calls take 26-64 ms)
    err = d["clockErr_m"][1] if d.get("clockErr_m") else d["shapeErr_m"][1]
    return "error" not in d and d["lagMs"] <= max_lag_ms and err < 0.5 and (d["jumps"] or 0) <= 3

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        for side in (b, L): side("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("relay only via", RELAY, b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
        b("mp host 27070")
        if RELAY: L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        if RELAY: print("route:", L("net route"), flush=True)
        print("mods:", b("mods")["mods"], L("mods")["mods"], "| traffic:", b("traffic"), L("traffic"), flush=True)
        shift0 = {id(s): g(s, "settingsscript.s.S.BAutoShift") for s in (b, L)}
        for s in (b, L): s("set kaposztaleves.s.settings.god True"); s("set settingsscript.s.S.BAutoShift True")
        rp = b("roadpoint"); yaw = rp["yaw"]; rp["x"], rp["z"] = rp["gx"], rp["gz"]   # global from here on
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))

        def owned_speed(side, net):
            e = track.last(side).get(net); return math.hypot(e["vel"][0], e["vel"][2]) if e and "vel" in e else float("nan")
        def pace(t_next):   # ~10 Hz sampling without a timer loop: each sample is two bridge calls
            d = t_next - time.monotonic()
            if d > 0: time.sleep(d)
            return max(t_next + 0.1, time.monotonic())

        if want("A"):
            print("A: following, then A2: the leader brakes hard", flush=True)
            rb0 = clear_road(b, rp["x"], rp["z"], F[0], F[1])
            back = (rb0["gx"] + R[0] * KEEP_RIGHT, rb0["gz"] + R[1] * KEEP_RIGHT)
            fr = clear_road(L, rb0["gx"] + F[0] * 40, rb0["gz"] + F[1] * 40, F[0], F[1])
            Ff = math.sin(math.radians(fr["yaw"])), math.cos(math.radians(fr["yaw"]))
            same = Ff[0] * F[0] + Ff[1] * F[1] > 0
            fyaw = fr["yaw"] if same else fr["yaw"] + 180
            Rf = (math.cos(math.radians(fyaw)), -math.sin(math.radians(fyaw)))
            front = (fr["gx"] + Rf[0] * KEEP_RIGHT, fr["gz"] + Rf[1] * KEEP_RIGHT)
            b(f"tpground {back[0] + 6} {back[1]}"); time.sleep(2)
            okb = car_ready(b, back[0], back[1], rb0["yaw"] if (math.sin(math.radians(rb0["yaw"])) * F[0] + math.cos(math.radians(rb0["yaw"])) * F[1]) > 0 else rb0["yaw"] + 180, "desktop")
            okl = car_ready(L, front[0], front[1], fyaw, "laptop")
            net_b, net_l = my_car_net(b), my_car_net(L)
            until(lambda: other_car_pos(b, net_l) is not None and other_car_pos(L, net_b) is not None, 15)
            t0 = b("traffic"); rep0 = b("mp entities")["partsReported"] + L("mp entities")["partsReported"]
            eng = engage(L, 45, "laptop (leader)") and engage(b, 80, "desktop (follower)")
            for s in (b, L): s("mp entstats")
            track = Track((b, L)); gaps, vb, vl = [], [], []
            wheels = {}
            t = time.monotonic(); tn = t; lp = t
            while time.monotonic() - t < FOLLOW_S and eng:
                tn = pace(tn); track.sample()
                eb, el = track.last(b).get(net_b), track.last(b).get(net_l)
                if not eb or not el: continue
                gaps.append(math.dist(eb["pos"][::2], el["pos"][::2]))
                if time.monotonic() - lp >= 1.0:
                    lp = time.monotonic()
                    wheel_sample(wheels, ((b, L, net_b, "desktop's car"), (L, b, net_l, "laptop's car")))
                    vb.append(owned_speed(b, net_b) * 3.6); vl.append(owned_speed(L, net_l) * 3.6)
                    print(f"  t={lp - t:4.1f}s gap {gaps[-1]:5.1f} m  desktop {vb[-1]:4.0f} km/h ({g(b, 'TLDAutopilot.Plugin.Host.Ap.State')})  laptop {vl[-1]:4.0f} km/h ({g(L, 'TLDAutopilot.Plugin.Host.Ap.State')})  {b('traffic').get('last', '')}", flush=True)
            t1 = b("traffic"); rep1 = b("mp entities")["partsReported"] + L("mp entities")["partsReported"]
            tail_b = sum(vb[-10:]) / max(1, len(vb[-10:])); tail_l = sum(vl[-10:]) / max(1, len(vl[-10:]))
            ok = eng and okb and okl and gaps and min(gaps) > 8 and t1["followed"] > t0["followed"] and rep1 == rep0 and abs(tail_b - tail_l) < 12
            verdict("A FOLLOWING", ok, f"min gap {min(gaps) if gaps else float('nan'):.1f} m; last-10s speeds desktop {tail_b:.0f} / laptop {tail_l:.0f} km/h; "
                    f"follow decisions {t1['followed'] - t0['followed']}; parts lost {rep1 - rep0}")
            wheel_verdict(wheels, "A")

            if eng:
                # A2: the leader brakes as hard as a player would (the autopilot let go, full brake pedal — our test
                # driver with target 0), then parks. The follower's autopilot only knows the leader through the network.
                el0 = track.last(L).get(net_l); dirv = el0["vel"] if el0 else [0, 0, 0]
                dl = math.hypot(dirv[0], dirv[2]) or 1; D = (dirv[0] / dl, dirv[2] / dl)
                print(f"  A2: the leader brakes at {owned_speed(L, net_l) * 3.6:.0f} km/h, gap {gaps[-1]:.1f} m", flush=True)
                L("call TLDAutopilot.Plugin.Host.Session.Toggle"); L("autodrive on 0"); tb = time.monotonic()
                gaps2, parked, t_leader_stop, stopped_b, parked_b = [], False, None, False, False
                while time.monotonic() - tb < 25:
                    tn = pace(tn); track.sample()
                    eb, el = track.last(b).get(net_b), track.last(L).get(net_l)
                    if eb and el: gaps2.append(math.dist(eb["pos"][::2], el["pos"][::2]))   # true gap: each car from its owner
                    # our test driver's speed hold creeps at ~2 m/s with target 0 (throttle 0.35 − 0.15·v below 3 m/s):
                    # park it before that, as a player holds the brake to a stop
                    if not parked and owned_speed(L, net_l) < 3.0:
                        L("autodrive off"); L("brakecar"); L("handbrake on"); parked = True; t_leader_stop = time.monotonic()
                        print(f"  A2: leader stopped after {t_leader_stop - tb:.1f} s", flush=True)
                    if parked and owned_speed(b, net_b) < 0.3 and time.monotonic() - t_leader_stop > 4: break
                    # the follower's autopilot passes a parked car and drives on: 300+ m later each machine has stored the
                    # other's car away (REST and SEAT then read unloaded copies). Its driver stops, as a player would.
                    # (gently, as the leader: a full brake + handbrake at 62 km/h cost it 3 parts)
                    if parked and time.monotonic() - t_leader_stop > 4 and not stopped_b:
                        if g(b, "TLDAutopilot.Plugin.Host.Ap.State") not in ("Off", None): b("call TLDAutopilot.Plugin.Host.Session.Toggle")
                        b("autodrive on 0"); stopped_b = True; print(f"  A2: the follower's driver slows it from {owned_speed(b, net_b) * 3.6:.0f} km/h", flush=True)
                    if stopped_b and not parked_b and owned_speed(b, net_b) < 3.0:
                        b("autodrive off"); b("brakecar"); b("handbrake on"); parked_b = True
                for _ in range(20): tn = pace(tn); track.sample()   # 2 s at rest: the copies settle
                rest = track.last(L)[net_l]["pos"]
                after = [(tt, e[net_l]["pos"]) for tt, e, _ in track.rows[id(b)] if tt >= tb and net_l in e and e[net_l]["proxy"]]
                along = [(q[0] - rest[0]) * D[0] + (q[2] - rest[2]) * D[1] for _, q in after]
                overshoot = max(along) if along else float("nan")
                backsteps = [along[i] - along[i + 1] for i in range(len(along) - 1) if along[i] - along[i + 1] > 0.05]
                rep2 = b("mp entities")["partsReported"] + L("mp entities")["partsReported"]
                ok2 = parked and gaps2 and min(gaps2) > 2.0 and rep2 == rep1 and overshoot < 0.3 and not backsteps
                verdict("A2 EMERGENCY BRAKE", ok2, f"leader parked {parked}; closest true gap {min(gaps2) if gaps2 else float('nan'):.1f} m; follower state {g(b, 'TLDAutopilot.Plugin.Host.Ap.State')}; "
                        f"leader's copy on the desktop went {overshoot:+.2f} m past where it really stopped, stepped back {len(backsteps)}x (max {max(backsteps, default=0):.2f} m); parts lost {rep2 - rep1}")

            ds, ns = report(track, [(L, b, net_l, "laptop's car as seen on the desktop"), (b, L, net_b, "desktop's car as seen on the laptop")],
                            [(b, "desktop (host)"), (L, "laptop (client)")])
            ping = (ns[1].get("ping") or (0, 0, 0))[1]
            seats = (seat_check(track, b, net_l), seat_check(track, L, net_b))
            print(f"   SEAT body→car distance (min, max, n): laptop player on the desktop {seats[0]}, desktop player on the laptop {seats[1]}", flush=True)
            for s in (b, L): disengage(s)
            time.sleep(2)
            rc = [rest_check(L, b, net_l, "laptop's car"), rest_check(b, L, net_b, "desktop's car")]
            for r_ in rc: print("   REST", json.dumps(r_), flush=True)
            dok = all(desync_ok(d, ping / 2 + d.get("interpDelayMs", 0) + 60) for d in ds) and all(r_.get("ok") for r_ in rc) \
                  and all(x is None or x[1] < 3.0 for x in seats)
            verdict("A DESYNC", dok, "; ".join(f"{d['label']}: lag {d.get('lagMs')} ms (ping/2 + delay {ping / 2 + d.get('interpDelayMs', 0):.0f}), shape p95 {d.get('shapeErr_m', ('?',) * 3)[1]} m, owner-clock p95 {(d.get('clockErr_m') or ('?',) * 4)[1]} m, "
                                               f"same-instant p95 {d.get('sameInstantErr_m', ('?',) * 3)[1]} m, jumps {d.get('jumps')}" for d in ds))
            for s in (b, L):
                if g(s, "mainscript.s.player.Bsitting") is True: s("driveout"); time.sleep(1)

        if want("B"):
            print("B: oncoming", flush=True)
            a = clear_road(b, rp["x"] - F[0] * 120, rp["z"] - F[1] * 120, -F[0], -F[1], look=(F[0], F[1]))       # drives +F
            c = clear_road(L, rp["x"] + F[0] * 130, rp["z"] + F[1] * 130, F[0], F[1], look=(-F[0], -F[1]))      # drives -F
            ya = a["yaw"] if (math.sin(math.radians(a["yaw"])) * F[0] + math.cos(math.radians(a["yaw"])) * F[1]) > 0 else a["yaw"] + 180
            yc = c["yaw"] + 180 if (math.sin(math.radians(c["yaw"])) * F[0] + math.cos(math.radians(c["yaw"])) * F[1]) > 0 else c["yaw"]
            Ra = (math.cos(math.radians(ya)), -math.sin(math.radians(ya))); Rc = (math.cos(math.radians(yc)), -math.sin(math.radians(yc)))
            okb = car_ready(b, a["gx"] + Ra[0] * KEEP_RIGHT, a["gz"] + Ra[1] * KEEP_RIGHT, ya, "desktop")
            okl = car_ready(L, c["gx"] + Rc[0] * KEEP_RIGHT, c["gz"] + Rc[1] * KEEP_RIGHT, yc, "laptop")
            net_b, net_l = my_car_net(b), my_car_net(L)
            until(lambda: other_car_pos(b, net_l) is not None, 15)
            t0 = (b("traffic"), L("traffic")); rep0 = b("mp entities")["partsReported"] + L("mp entities")["partsReported"]
            eng = engage(b, 60, "desktop") and engage(L, 60, "laptop")
            for s in (b, L): s("mp entstats")
            track = Track((b, L)); wheels = {}
            dmin, states, t = float("inf"), set(), time.monotonic(); tn = t; lp = t
            passed = False; seen_b, seen_l, truth = [], [], []
            while time.monotonic() - t < 50 and eng:
                tn = pace(tn); track.sample()
                eb, el = track.last(b).get(net_b), track.last(L).get(net_l)
                if not eb or not el: continue
                d = math.dist(eb["pos"][::2], el["pos"][::2]); dmin = min(dmin, d)
                if time.monotonic() - lp >= 0.5:
                    lp = time.monotonic()
                    if d < 150: wheel_sample(wheels, ((b, L, net_b, "desktop's car"), (L, b, net_l, "laptop's car")))   # copies within 150 m (both loaded)
                    sb, sl = g(b, "TLDAutopilot.Plugin.Host.Ap.State"), g(L, "TLDAutopilot.Plugin.Host.Ap.State"); states |= {sb, sl}
                    print(f"  t={lp - t:4.1f}s distance {d:6.1f} m  desktop {sb}  laptop {sl}", flush=True)
                if dmin < 60 and d > dmin + 40: passed = True; break
            # how close the pass was in truth (each car from its owner, aligned in time) vs how close it looked on each screen
            ownb = [(tt, tuple(e[net_b]["pos"])) for tt, e, _ in track.rows[id(b)] if net_b in e]
            ownl = [(tt, tuple(e[net_l]["pos"])) for tt, e, _ in track.rows[id(L)] if net_l in e]
            for tt, pb_ in ownb:
                pl_ = _interp(ownl, tt)
                if pl_: truth.append(math.dist(pb_[::2], pl_[::2]))
            seen_b = [math.dist(e[net_b]["pos"][::2], e[net_l]["pos"][::2]) for _, e, _ in track.rows[id(b)] if net_b in e and net_l in e]
            seen_l = [math.dist(e[net_b]["pos"][::2], e[net_l]["pos"][::2]) for _, e, _ in track.rows[id(L)] if net_b in e and net_l in e]
            t1 = (b("traffic"), L("traffic")); rep1 = b("mp entities")["partsReported"] + L("mp entities")["partsReported"]
            ign = (t1[0]["ignored"] - t0[0]["ignored"]) + (t1[1]["ignored"] - t0[1]["ignored"])
            ok = eng and okb and okl and passed and dmin > 2.5 and rep1 == rep0 and "Off" not in states
            verdict("B ONCOMING", ok, f"passed {passed}, closest {dmin:.1f} m, states {sorted(x for x in states if x)}, oncoming seen as not blocking {ign}, parts lost {rep1 - rep0}")
            wheel_verdict(wheels, "B")
            print(f"   PASS DISTANCE closest: true {min(truth, default=float('nan')):.2f} m, on the desktop's screen {min(seen_b, default=float('nan')):.2f} m, "
                  f"on the laptop's screen {min(seen_l, default=float('nan')):.2f} m", flush=True)
            ds, ns = report(track, [(L, b, net_l, "laptop's car as seen on the desktop"), (b, L, net_b, "desktop's car as seen on the laptop")],
                            [(b, "desktop (host)"), (L, "laptop (client)")])
            ping = (ns[1].get("ping") or (0, 0, 0))[1]
            seats = (seat_check(track, b, net_l), seat_check(track, L, net_b))
            print(f"   SEAT body→car distance (min, max, n): laptop player on the desktop {seats[0]}, desktop player on the laptop {seats[1]}", flush=True)
            for s in (b, L): disengage(s)
            time.sleep(3)
            rc = [rest_check(L, b, net_l, "laptop's car"), rest_check(b, L, net_b, "desktop's car")]
            for r_ in rc: print("   REST", json.dumps(r_), flush=True)
            dok = all(desync_ok(d, ping / 2 + d.get("interpDelayMs", 0) + 60) for d in ds) and all(r_.get("ok") for r_ in rc) and all(x is None or x[1] < 3.0 for x in seats)
            verdict("B DESYNC", dok, "; ".join(f"{d['label']}: lag {d.get('lagMs')} ms (ping/2 + delay {ping / 2 + d.get('interpDelayMs', 0):.0f}), shape p95 {d.get('shapeErr_m', ('?',) * 3)[1]} m, owner-clock p95 {(d.get('clockErr_m') or ('?',) * 4)[1]} m, "
                                               f"same-instant p95 {d.get('sameInstantErr_m', ('?',) * 3)[1]} m, jumps {d.get('jumps')}" for d in ds))

        for s in (b, L): s("set kaposztaleves.s.settings.god False"); s(f"set settingsscript.s.S.BAutoShift {shift0[id(s)]}")
        L("mp stop"); b("mp stop")
finally:
    try:
        if RELAY: b("net relay off"); L("net relay off")
    except Exception: pass
    L.close()
print("\nTWO CARS:", "ALL PASS" if verdicts and all(verdicts.values()) else "FAILURES", {k: ("PASS" if v else "FAIL") for k, v in verdicts.items()})
