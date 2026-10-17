#!/usr/bin/env python3
"""Mini playtest: the 2026-10-09 playtest's mix on two machines over the relay, compared object by object (2026-10-09: the
single-feature tests pass; the playtest broke where things came together). The desktop hosts; the laptop joins.
  Home : the host's Car05Full and the laptop's car01 side by side. Each player works on the OTHER's car — a radiator/tank
         cap, the hubcap taken off and bolted back after the owner held it (an attach request), a lamp left in it, a crate
         spawned and broken by the other — then everything compared.
  Round k (--rounds): the laptop drives off (autopilot) until the host's copy of its car is gone, stops, and both
         change parts on their own cars while apart (hubcap, wheel, door off and back on), the host moves its car.
         Meeting 1: the host goes to the laptop's car — the copy comes back where the owner has it, as the owner has it;
         the host works on it (cap, hubcap, crate). Meeting 2: the laptop walks home to the host's car — the same there.
  Every check: each car's parts (prefab + slot) and wheels on both machines; every object within 40 m of the meeting
  point on both machines by model and place (`mp sitestate`, ±1 m); caps' states; errors. God mode on during the test
  (restored), autosave blocked (ProtectedSession).
  tools/playround.py [--no-build] [--relay iad|off] [--rounds 2] [--drive 40] [--focus flicker|mirror|parts] [--changes] [--old-copylock]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import clear_road, car_ready, seat_idx, g

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, ROUNDS, DRIVE_S = arg("--relay", "iad"), arg("--rounds", 2, int), arg("--drive", 40, float)
CHANGES = "--changes" in sys.argv   # focus modes: also change parts while apart (hubcap, wheel, a door off/on)
FOCUS = arg("--focus", "")   # flicker | mirror | parts: only that bug's steps, many short cycles (--rounds) per session
T = "TLDRevamp.Net.Entities"; R = 40; BOX = 62; LAMP = 320; CAR05 = 154; HUBSLOT = "DisztarcsaSlotCol"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None

def in_world(s):
    try: return s("get DataFromMenuScript.s.mainmenu")["value"] is False and s("get mainscript.s.player")["value"] is not None
    except Exception: return False

def travel(s, gx, gz, who, settle=8):
    """ground-safe teleport to a GLOBAL point (meetagain.travel)"""
    s("set mainscript.s.player.godModeTillGrounded True")
    s(f"tpg {gx} {gz} hold")
    def grounded():
        q = s("gpos"); return "y" in s(f"ground {q['ux']} {q['uz']}")
    ok = until(grounded, 90, 1.0)
    if not ok:
        s(f"tpg {gx + 300} {gz} hold"); until(grounded, 60, 1.0); s(f"tpg {gx} {gz} hold"); ok = until(grounded, 90, 1.0)
    if not ok: s("tprelease"); raise RuntimeError(f"{who}: no ground at {gx:.0f},{gz:.0f}")
    r = s(f"tpground {gx} {gz}")
    if r.get("tpground") == "hop": until(grounded, 30, 1.0); s(f"tpground {gx} {gz}")
    time.sleep(settle)
    if not in_world(s): raise RuntimeError(f"{who}: left the world")
    q = s("gpos"); print(f"  {who} at {q['gx']:.0f},{q['gz']:.0f}", flush=True)
    return q

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}

def flat_spot(s, gx, gz, within=30.0, size=3.0, flat=0.35):
    """the nearest point to (gx, gz), within `within` m, where the ground varies less than `flat` m over a car's length
    (a 2·size m square): a car parked there doesn't roll off. (gx, gz) if none is found."""
    def h_(x, z):
        try: return s(f"groundg {x} {z}")["gy"]
        except Exception: return None
    for r in [0.0] + [float(v) for v in range(4, int(within) + 1, 4)]:
        for k in range(1 if r == 0 else 8):
            a = k * math.pi / 4
            x, z = gx + math.cos(a) * r, gz + math.sin(a) * r
            hs = [h_(x + dx, z + dz) for dx in (-size, 0, size) for dz in (-size, 0, size)]
            if None in hs: continue
            if max(hs) - min(hs) < flat: return x, z
    return gx, gz
def ent(s, net): return ents(s).get(net)

def same_car(owner, other, net, label, wtol=0.05):
    ref, got = owner(f"mp carparts {net}"), other(f"mp carparts {net}")
    if got.get("dormant") or "error" in got or "error" in ref: verdict(label, False, f"owner {ref.get('error') or ''} other {got.get('error') or ('dormant' if got.get('dormant') else '')}"); return
    rp, gp = set(ref["parts"]), set(got["parts"])
    rw = {w[0]: w[1:] for w in ref["wheels"]}; gw = {w[0]: w[1:] for w in got["wheels"]}
    off = [(k, round(math.dist(rw[k], gw[k]), 3)) for k in rw if k in gw and math.dist(rw[k], gw[k]) > wtol]
    bad = sorted(rp - gp), sorted(gp - rp), sorted(set(rw) - set(gw)), sorted(set(gw) - set(rw)), off
    verdict(label, not any(bad), f"parts {len(rp)} vs {len(gp)}: missing {bad[0]}, extra {bad[1]}; wheels missing {bad[2]}, extra {bad[3]}, off {bad[4]}")
    if off:
        print(f"    wheels (car frame, owner | other): {[(k, rw[k], gw.get(k)) for k, _ in off]}", flush=True)
        try: print(f"    wheel poses owner {owner(f'mp wheelpose {net}')} | other {other(f'mp wheelpose {net}')}", flush=True)
        except Exception as ex: print(f"    wheelpose: {ex}", flush=True)
    if any(bad):
        print(f"    owner log of the car: {h(f'mp ownerlog {net}')['log'][-12:]}", flush=True)
        K = ("name", "owner", "proxy", "attached", "parent", "part", "dormant", "stored", "held", "pos")
        for nm, s2 in (("host", h), ("laptop", L)):
            for e in s2("mp entities")["entities"]:
                if e.get("parent") == net or (e.get("attached") and e.get("parent") == net):
                    print(f"    {nm}: net {e['net']} {({k: e.get(k) for k in K})}", flush=True)
        for nm, s2 in (("host", owner), ("other", other)):
            try: print(f"    {nm} attachedparts: {[(p_['i'], p_['name']) for p_ in s2(f'mp attachedparts {net}')['parts'] if 'Door' in p_['name'] or 'Mirror' in p_['name']]}", flush=True)
            except Exception as ex: print(f"    {nm} attachedparts: {ex}", flush=True)

def site(s, gx, gz):
    return [it for it in s(f"mp sitestate {gx} {gz} {R}")["items"] if "Ragdol" not in it[5] and "tumbleweed" not in it[5]
            and not it[5].startswith("Nyul")]   # rabbits run, spawn and die on their own (tools/rabbits.py covers them)

def same_world(gx, gz, label, tol=1.0):
    a, b = site(h, gx, gz), site(L, gx, gz)
    left = list(b); missing = []
    for it in a:
        best, bi = None, -1
        for i, o in enumerate(left):
            if o[0] != it[0]: continue
            d = math.dist(it[1:4], o[1:4])
            if best is None or d < best: best, bi = d, i
        if best is None or best > tol: missing.append((it[5], best and round(best, 2), [round(v, 1) for v in it[1:4]]))
        else: left.pop(bi)
    extra = [(o[5], [round(v, 1) for v in o[1:4]]) for o in left]
    verdict(label, not missing and not extra, f"host {len(a)} objects, laptop {len(b)}: only on the host {missing[:10]}, only on the laptop {extra[:10]}")
    # whose are they? the shared object at that place on that machine, and the same object on the other machine
    K = ("pos", "dormant", "owner", "proxy", "stored", "held", "attached", "parent", "physical", "sentRest", "lastIn", "recv")
    for nm, s2, o2, rows in (("host", h, L, [(m[0], m[2]) for m in missing[:4]]), ("laptop", L, h, [(x[0], x[1]) for x in extra[:4]])):
        if not rows: continue
        es = s2("mp entities")["entities"]; eo = {e["net"]: e for e in o2("mp entities")["entities"]}
        for name, pos in rows:
            near = [e for e in es if not e["dormant"] and math.dist(e["pos"], pos) < 0.8]
            for e in near:
                o = eo.get(e["net"])
                print(f"    {name} on the {nm}: net {e['net']} {({k: e.get(k) for k in K})}\n"
                      f"      on the other: {o and {k: o.get(k) for k in K}}", flush=True)
            if not near: print(f"    {name} on the {nm} at {pos}: no shared object there (a local one)", flush=True)

def cap_key(c): return (c["cap"], c["usable"].split(" x ")[0] if c["usable"] else None)

def work_on(owner, other, onm, xnm, car, label):
    """the OTHER player works on the owner's car: a cap, the hubcap (via the owner's hands), a lamp left in it, a crate"""
    xid = other("mp entities")["me"]
    # a cap
    caps = owner(f"mp caps {car}").get("caps", [])
    if caps:
        try: r = other(f"mp usecap {car} 0")
        except RuntimeError as ex: r = {"error": str(ex)}
        time.sleep(3.5)
        a, b = owner(f"mp caps {car}")["caps"][0], other(f"mp caps {car}")["caps"][0]
        verdict(f"{label}: {xnm.upper()} USED A CAP ON {onm.upper()}'S CAR — THE SAME ON BOTH", "error" not in r and cap_key(a) == cap_key(b), f"{r} → {onm} {a['usable']} | {xnm} {b['usable']}")
    # the hubcap: off by the other player (in hand), dropped, the owner picks it up and drops it, the other bolts it back
    lb = set(ents(other))
    other(f"set {T}.TestAsPlayer True")
    try: off = other(f"mount off {car} {HUBSLOT}")
    except RuntimeError as ex: off = {"error": str(ex)}
    other(f"set {T}.TestAsPlayer False")
    # the part comes off on its owner's machine; it lands in our hand only near that wheel — take it either way
    hub = until(lambda: next((n for n, e in ents(other).items() if "Disztarcsa" in e["name"] and n not in lb), None), 10)
    def drop(s_):
        try: s_("mp drop")
        except RuntimeError: pass
    if hub:
        time.sleep(2)
        def pick(s_, who):
            # the part may not be loaded there yet ("no such item here": a flake that ended whole runs): wait, retry, and
            # if it never comes, say so in the verdict instead of stopping the run
            last = None
            for _ in range(6):
                try: return s_(f"mp pickupnet {hub}")
                except RuntimeError as ex: last = ex; time.sleep(1.5)
            print(f"  {label}: {who} couldn't pick up the hubcap {hub}: {last}", flush=True)
            return None
        if not until(lambda: ents(other)[hub]["owner"] == xid and not ents(other)[hub]["proxy"], 2):
            pick(other, xnm); until(lambda: ents(other)[hub]["owner"] == xid and not ents(other)[hub]["proxy"], 10)
        time.sleep(1); drop(other); time.sleep(2)
        until(lambda: hub in ents(owner), 10)
        pick(owner, onm); until(lambda: ents(other)[hub]["proxy"], 10); drop(owner); time.sleep(2)
        other(f"set {T}.TestAsPlayer True")
        try: on = other(f"mount on {hub} {car} {HUBSLOT}")
        except RuntimeError as ex: on = {"error": str(ex)}
        other(f"set {T}.TestAsPlayer False"); time.sleep(6)
        po, px = owner(f"mount pose {hub} {car} {HUBSLOT}"), other(f"mount pose {hub} {car} {HUBSLOT}")
        verdict(f"{label}: {xnm.upper()} BOLTED {onm.upper()}'S HUBCAP BACK (AFTER {onm.upper()} HELD IT) — IN THE SLOT ON BOTH", bool(po.get("inSlot")) and bool(px.get("inSlot")),
                f"off {off}, on {on}; {onm} in slot {po.get('inSlot')} {po.get('dist')} m, {xnm} in slot {px.get('inSlot')} {px.get('dist')} m")
    else: print(f"  {label}: no hubcap came off ({off})", flush=True)
    # a lamp of the other player's, left on the owner's car's back seat
    lb = set(ents(other)); u = other("gpos"); gy = other(f"ground {u['ux'] + 2} {u['uz']}").get("y")
    if gy is not None:
        other(f"spawnpile {LAMP} 1 {u['ux'] + 2} {gy + 0.5} {u['uz']} 1 1 0 3")
        lamp = until(lambda: next((n for n, e in ents(other).items() if n not in lb and e["owner"] == xid and e["part"] < 0), None), 15)
        if lamp:
            until(lambda: lamp in ents(owner), 15); time.sleep(2)
            try: print(f"  {label}: {xnm}'s lamp into {onm}'s car:", other(f"mp putincar {car} {lamp} 1").get("items"), flush=True)
            except RuntimeError as ex: print(f"  {label}: lamp: {ex}", flush=True)
    # a crate spawned by the other player, broken by the owner
    lb = set(ents(other)); u = other("gpos"); sx, sz = u["ux"] + 4, u["uz"] - 4; gy = other(f"ground {sx} {sz}").get("y")
    if gy is not None:
        other(f"spawnpile {BOX} 1 {sx} {gy + 1.0} {sz} 1 1 0 {int(time.time()) % 1000}")
        # (dropped next to the owner's car it may be the owner's at once — the car's proximity claim)
        box = until(lambda: next((n for n, e in ents(other).items() if n not in lb and e["part"] < 0 and "Box" in e["name"]), None), 15)
        if box and until(lambda: (ent(owner, box) or {}).get("dormant") is False, 20):
            time.sleep(4)
            try: print(f"  {label}: {onm} breaks {xnm}'s crate:", owner(f"mp breakent {box} 1000000"), flush=True)
            except RuntimeError as ex: print(f"  {label}: crate: {ex}", flush=True)
    time.sleep(8)

def checks(label, gx, gz, cars):
    for owner, other, net, nm in cars: same_car(owner, other, net, f"{label}: {nm.upper()}'S CAR — THE OTHER'S COPY HAS ITS PARTS")
    same_world(gx, gz, f"{label}: EVERYTHING WITHIN {R} M THE SAME ON BOTH")
    er = [s_("errors")["errors"] for s_ in (h, L)]
    verdict(f"{label}: NO ERRORS", er == [0, 0], str(er))

def change_parts(s, net, who):
    """a hubcap off, a wheel off, a door off and bolted back (meetagain.change_parts)"""
    done = []
    parts = s(f"mp attachedparts {net}")["parts"]
    door = next((p for p in parts if "Door" in p["name"]), None)
    hubp = next((p for p in parts if "Disztarcsa" in p["name"]), None)
    if hubp: done.append(("hubcap off", s(f"mp falloff {net} {hubp['i']}"))); time.sleep(2)
    wheel = next((p for p in s(f"mp attachedparts {net}")["parts"] if "Felni" in p["name"] or p["name"].startswith("Wheel")), None)
    if wheel: done.append(("wheel off", s(f"mp falloff {net} {wheel['i']}"))); time.sleep(2)
    if door:
        before = set(ents(s))
        done.append(("door off", s(f"mp falloff {net} {door['i']}"))); time.sleep(3)
        new = [n for n, e in ents(s).items() if n not in before and "Door" in e.get("name", "")]
        if new: done.append(("door back on", s(f"mp attachto {new[0]} {net} -1")))
    time.sleep(3)
    for d in done: print(f"  {who} change: {d[0]} {str(d[1])[:120]}", flush=True)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    GOD = {}
    for n_, s_ in (("h", h), ("L", L)):
        s_("set TLDRevamp.TestSafety.BlockAutoSave True"); GOD[n_] = s_("get kaposztaleves.s.settings.god").get("value"); s_("set kaposztaleves.s.settings.god True")
    SHIFT = g(L, "settingsscript.s.S.BAutoShift")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True"); L("set kaposztaleves.s.settings.god True")
        L("set settingsscript.s.S.BAutoShift True")
        for s_ in (h, L): s_(f"set {T}.StoreOutTrace 60")   # log every shared root streamed out (where, the call stack)
        if "--old-copylock" in sys.argv:   # A/B: the container release rule as in v0.66.7
            for s_ in (h, L): s_(f"set {T}.CopyLockSafe False"); s_(f"set {T}.CopyLockByContainer False"); s_(f"set {T}.DriveClaimRootOnly False")
            print("  OLD COPYLOCK (v0.66.7)", flush=True)
        time.sleep(8)
        hid, lid = h("mp entities")["me"], L("mp entities")["me"]
        # the laptop's car01 on the road (it drives off later); the host's Car05Full next to it
        rp = until(lambda: L("roadpoint"), 90, 2.0)   # a slow laptop (power saving) is still loading the world after the join
        if not rp: sys.exit("LAPTOP'S WORLD NOT READY (roadpoint)")
        F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(L, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]; Rv = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        if not car_ready(L, r0["gx"] + Rv[0] * 1.8, r0["gz"] + Rv[1] * 1.8, yaw, "laptop"): sys.exit("LAPTOP NOT IN ITS CAR")
        me = L("mp players")["me"]
        lcar = min((e for e in L("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
        L("driveout"); time.sleep(1)
        home = (me[0] + Rv[0] * 12, me[2] + Rv[1] * 12)
        travel(h, home[0] + 3, home[1] + 3, "desktop (home)")
        hb = set(ents(h))
        print("  host car:", h(f"spawncarg {CAR05} 0 {home[0]} {home[1]} {yaw}"), flush=True)
        hcar = until(lambda: next((n for n, e in ents(h).items() if n not in hb and e["car"] and e["owner"] == hid), None), 20)
        if not hcar: sys.exit("the host's car was not shared")
        until(lambda: (ent(L, hcar) or {}).get("dormant") is False, 30); until(lambda: (ent(h, lcar) or {}).get("dormant") is False, 30); time.sleep(6)
        print(f"  cars: host's {hcar} (Car05Full), laptop's {lcar} (car01)", flush=True)
        CARS = ((h, L, hcar, "host"), (L, h, lcar, "laptop"))

        def drive_off(tag):
            # the laptop drives off until the host's copy of its car is gone
            lc = ent(L, lcar)["pos"]; travel(L, lc[0] + 2, lc[2] + 2, "laptop (to its car)", settle=3)
            for _ in range(4):
                try: L(f"sitseat {seat_idx(L)} redirect")
                except Exception as ex: print("  sit:", ex, flush=True)
                time.sleep(1)
                if g(L, "mainscript.s.player.Bsitting") is True: break
            # the host may have taken the car over while the laptop was away (the server's hand-off to the nearer player,
            # meeting 1): the driver seat claims it back (DriveClaimTick, every 1.5 s) — wait for it, or the laptop fuels
            # and drives a display copy ("fuelup: no car/engine", R2, 2026-10-17)
            mine = until(lambda: (lambda e_: e_ and not e_["proxy"] and e_["owner"] == lid)(ent(L, lcar)), 15, 0.5)
            cp_ = L(f"mp carparts {lcar}")
            print(f"  {tag}: seated {g(L, 'mainscript.s.player.Bsitting')}, the laptop's again {bool(mine)}; the car's body: up {cp_.get('up')}, kinematic {cp_.get('kin')}, asleep {cp_.get('asleep')}, {cp_.get('agl')} m over the ground", flush=True)
            if not mine:
                verdict(f"{tag}: THE LAPTOP CLAIMS ITS CAR BACK FROM THE DRIVER SEAT", False, f"laptop's copy {ent(L, lcar)}, owner log {h(f'mp ownerlog {lcar}')['log'][-6:]}")
                raise RuntimeError(f"{tag}: the laptop's car stayed the host's")
            L("fuelup"); L("autodrive on 19.4")
            gone = until(lambda: (ent(h, lcar) or {}).get("dormant") is True, 120, 1.0)
            time.sleep(DRIVE_S / 2)
            # stop before getting out: a car left coasting at 70 km/h rolled 16-45 m down a slope after the driver got
            # out (playround R1, 2026-10-17)
            L("autodrive on 0"); until(lambda: abs(float(g(L, "mainscript.s.player.Car.speed") or 0)) < 2, 20, 0.3)
            L("autodrive off"); time.sleep(1)
            try: L("handbrake on")
            except RuntimeError as ex: print("  handbrake:", ex, flush=True)
            L("driveout"); time.sleep(1)
            # parked where it stays? (a car rolling off down a slope after its driver got out is the site, not the mod)
            p0 = ent(L, lcar)["pos"]; time.sleep(5); lc = ent(L, lcar)["pos"]
            print(f"  {tag}: parked — moved {math.dist(p0, lc):.2f} m in 5 s after the driver got out", flush=True)
            print(f"  {tag}: the laptop drove off — host's copy streamed out {bool(gone)}; its car at {lc[0]:.0f},{lc[2]:.0f}, "
                  f"{math.hypot(lc[0] - home[0], lc[2] - home[1]):.0f} m from home", flush=True)
            return lc, gone

        def meet1(tag, lc):
            # meeting 1: the host goes to the laptop's car
            ph1 = h("mp physical")
            travel(h, lc[0] - 25, lc[2] - 25, f"{tag} desktop (to the laptop's car)")
            placed = until(lambda: (ent(h, lcar) or {}).get("dormant") is False, 30)
            fr = h(f"mp farrec {lcar}")
            verdict(f"{tag} M1: THE HOST PLACED ITS COPY OF THE LAPTOP'S CAR (not left in the far store)", bool(placed), "" if placed else f"host farrec {fr}")
            time.sleep(3)
            if placed:
                stay = not until(lambda: (ent(h, lcar) or {}).get("dormant") is True, 12, 0.3)
                verdict(f"{tag} M1: AND IT STAYS (not stored again within 12 s, the host 35 m away)", stay,
                        f"laptop's car on the laptop {[round(v, 1) for v in ent(L, lcar)['pos']]} (parked at {[round(v, 1) for v in lc]})" +
                        ("" if stay else f"; stored again — host at {h('gpos')}, host farrec {h(f'mp farrec {lcar}')}"))
            return placed, ph1

        def door_entity(s_, car_, name):
            c_ = ent(s_, car_)["pos"]
            cand = [e for e in s_("mp entities")["entities"] if name in e["name"] and not e["attached"] and not e["car"] and not e["dormant"]]
            return min(cand, key=lambda e: math.dist(e["pos"], c_))["net"] if cand else None

        def crash_off(tag, car_, want=3, tries=6):
            """the laptop's car rammed into a car spawned in front of it until `want` parts came off; their objects"""
            ap0 = {p_["name"] for p_ in L(f"mp attachedparts {car_}")["parts"]}
            for k_ in range(tries):
                e_ = ent(L, car_); c_, yw = e_["pos"], math.radians(e_.get("yaw", 0.0))
                fx, fz = math.sin(yw), math.cos(yw)
                ob = set(ents(L))
                L(f"spawncarg {CAR05} 0 {c_[0] + fx * 16} {c_[2] + fz * 16} {math.degrees(yw) + 90}"); time.sleep(3)
                obst = next((n for n, e in ents(L).items() if n not in ob and e["car"]), None)
                v = 22 + 6 * k_
                print(f"  {tag}: crash {k_ + 1} at {v} m/s: {L(f'crash launch {v} {c_[0] - fx * 2} {c_[2] - fz * 2}')}", flush=True)
                time.sleep(6)
                if obst: L(f"mp moveitem {obst} 0 400"); time.sleep(1)   # the obstacle out of the way (dormant everywhere)
                gone = ap0 - {p_["name"] for p_ in L(f"mp attachedparts {car_}")["parts"]}
                if len(gone) >= want: break
            gone = sorted(ap0 - {p_["name"] for p_ in L(f"mp attachedparts {car_}")["parts"]})
            c_ = ent(L, car_)["pos"]
            loose = [e for e in L("mp entities")["entities"] if not e["car"] and not e["attached"] and not e["dormant"] and e["owner"] == lid
                     and math.dist(e["pos"], c_) < 40 and any(e["name"].split("(")[0] in g_ for g_ in gone)]
            print(f"  {tag}: {len(gone)} parts off {gone[:12]}; loose objects {[(e['net'], e['name']) for e in loose][:12]}", flush=True)
            up = L(f"mp carparts {car_}").get("up"); print(f"  {tag}: the car's up {up}", flush=True)
            if up is not None and up < 0.9:   # on its side or roof after the crashes: the owner puts it back on its wheels
                u = L("gpos"); x, z = u["ux"] + (c_[0] - u["gx"]), u["uz"] + (c_[2] - u["gz"])
                y = L(f"ground {x} {z}").get("y", 0.0); yw_ = ent(L, car_).get("yaw", 0.0)
                print(f"  {tag}: the car lies with up {up}: {L(f'carface {x} {y} {z} {yw_}')}", flush=True)
                time.sleep(4); print(f"  {tag}: up now {L(f'mp carparts {car_}').get('up')}", flush=True)
            return gone, loose

        if FOCUS == "parts":
            def move_with(dx, dz, who):
                """the laptop's car moved (dx, dz) and the laptop with it — what driving it there does. The server's hand-off
                is held meanwhile: between the car's move and the laptop's arrival its owner is 1 km away and, coming home,
                the host next to it (P1 of the first runs: the car became the host's, round 2 found nothing to move)"""
                h(f"set {T}.HandoffEnabled False")
                try:
                    L(f"mp moveitem {lcar} {dx} {dz}"); time.sleep(2)
                    c_ = ent(L, lcar)["pos"]
                    travel(L, c_[0] + 3, c_[2] + 3, who, settle=3)
                    until(lambda: (ent(L, lcar) or {}).get("dormant") is False, 30)
                    # onto the ground there (the move kept the height it had: under the terrain on higher ground)
                    print(f"  {who}: onto the ground: {until(lambda: L(f'mp moveitem {lcar} 0 0 ground'), 30, 2.0)}", flush=True)
                    time.sleep(4)
                finally: h(f"set {T}.HandoffEnabled True")
                cp_ = L(f"mp carparts {lcar}")
                print(f"  {who}: the car's body: up {cp_.get('up')}, kinematic {cp_.get('kin')}, asleep {cp_.get('asleep')}, {cp_.get('agl')} m over the ground", flush=True)
                return ent(L, lcar)["pos"]
            # playtest 2026-10-09/10 (a): a friend's car crashed ~1 km away, parts came off, bolted back, the car came back
            # into view with ~20 parts missing on the host. The laptop drives off ~1 km (the host's copy stored away), crashes
            # until parts come off, bolts them all back, comes home → both censuses and the 40 m site on arrival.
            for k in range(1, ROUNDS + 1):
                tag = f"P{k}"
                try: lc, gone = drive_off(tag)
                except RuntimeError as ex: print(f"  {tag}: stopped: {ex}", flush=True); break
                d = math.hypot(lc[0] - home[0], lc[2] - home[1])
                if d < 1000:   # on to ~1 km (straight on: a teleport of the car along its way, the host's copy is stored)
                    e_ = ent(L, lcar); yw = math.radians(e_.get("yaw", 0.0))
                    lc = move_with(math.sin(yw) * (1000 - d), math.cos(yw) * (1000 - d), f"{tag} laptop (at its car, ~1 km from home)")
                else: travel(L, lc[0] + 4, lc[2] + 4, f"{tag} laptop (at its car)", settle=3)
                verdict(f"{tag}: THE HOST'S COPY OF THE LAPTOP'S CAR STORED AWAY", (ent(h, lcar) or {}).get("dormant") is True)
                off, loose = crash_off(tag, lcar)
                verdict(f"{tag}: PARTS CAME OFF IN THE CRASHES", len(off) > 0, str(off[:12]))
                for e in loose:
                    r_ = L(f"mp attachto {e['net']} {lcar} -1"); time.sleep(1.5)
                    if "error" in r_: print(f"  {tag}: bolt {e['net']} {e['name']}: {r_}", flush=True)
                time.sleep(3)
                left = sorted(set(off) - {p_["name"] for p_ in L(f"mp attachedparts {lcar}")["parts"]})
                print(f"  {tag}: bolted back; still off on the laptop {left}", flush=True)
                # back home: the car next to the host's (moved: the drive back), the laptop with it — on flat ground (on the
                # slope by the home point it rolled ~45 m away and stopped at the 40 m circle's edge: NOTES 2026-10-14)
                lc = ent(L, lcar)["pos"]
                px, pz = flat_spot(L, home[0] - 14, home[1])
                lc = move_with(px - lc[0], pz - lc[2], f"{tag} laptop (home with its car)")
                placed = until(lambda: (ent(h, lcar) or {}).get("dormant") is False, 30)
                verdict(f"{tag}: THE HOST PLACED ITS COPY OF THE LAPTOP'S CAR", bool(placed))
                verdict(f"{tag}: STILL THE LAPTOP'S CAR (on both)", all((ent(s_, lcar) or {}).get("owner") == lid for s_ in (h, L)),
                        f"owner host {(ent(h, lcar) or {}).get('owner')}, laptop {(ent(L, lcar) or {}).get('owner')}")
                time.sleep(8)
                checks(f"{tag} HOME", lc[0], lc[2], ((L, h, lcar, "laptop"),))
                rel = h(f"get {T}.CopyLocksReleased")["value"]; dro = h(f"get {T}.CopyLocksDroppedAttached")["value"]
                print(f"  {tag}: host copies taken out of containers {rel}, locks dropped on bolted parts {dro}", flush=True)
        elif FOCUS == "flicker":
            # only what the flicker needs: drive off, the host walks up to the car — placed, and it stays — and back home
            for k in range(1, ROUNDS + 1):
                tag = f"F{k}"
                try: lc, gone = drive_off(tag)
                except RuntimeError as ex: print(f"  {tag}: stopped: {ex}", flush=True); break
                if CHANGES: change_parts(L, lcar, f"{tag} laptop"); lc = ent(L, lcar)["pos"]
                placed, ph1 = meet1(tag, lc)
                if placed: same_car(L, h, lcar, f"{tag}: LAPTOP'S CAR — THE HOST'S COPY HAS ITS PARTS")
                travel(h, home[0] + 3, home[1] + 3, f"{tag} desktop (home)", settle=3)
        elif FOCUS == "mirror":
            # playround R2's sequence (the laptop's copy came back with the host's door mirror in the car): the laptop away,
            # the host's FR door off and bolted back (an object of its own from then on); the host away, the laptop at the
            # car (the car handed to it, the door with it); the laptop away, the host back (the car handed back); the
            # host takes that door off again and leaves it, moves the car; the laptop back → both cars' censuses, 40 m.
            for k in range(1, ROUNDS + 1):
                tag = f"X{k}"
                hc = ent(h, hcar)["pos"]
                travel(L, hc[0] - 600, hc[2], f"{tag} laptop (600 m away)", settle=3)
                until(lambda: (ent(L, hcar) or {}).get("dormant") is True, 30)
                if CHANGES: change_parts(h, hcar, f"{tag} host")
                door = next((p_ for p_ in h(f"mp attachedparts {hcar}")["parts"] if "FRDoor" in p_["name"]), None)
                if not door: verdict(f"{tag} SETUP: THE FR DOOR ON THE HOST'S CAR", False); break
                print(f"  {tag} host: door off {h('mp falloff %s %s' % (hcar, door['i']))}", flush=True); time.sleep(3)
                dn = door_entity(h, hcar, "FRDoor")
                print(f"  {tag} host: door {dn} back on {dn and h(f'mp attachto {dn} {hcar} -1')}", flush=True); time.sleep(3)
                travel(h, hc[0] + 600, hc[2], f"{tag} desktop (600 m away)", settle=3)
                travel(L, hc[0] + 3, hc[2] - 3, f"{tag} laptop (at the host's car)", settle=3)
                got = until(lambda: ent(h, hcar)["owner"] == lid and ent(L, hcar)["owner"] == lid, 30)
                print(f"  {tag}: the car handed to the laptop {bool(got)}; door {dn} owner {(ent(h, dn) or {}).get('owner')}", flush=True)
                time.sleep(5)
                travel(L, hc[0] - 600, hc[2], f"{tag} laptop (600 m away)", settle=3)
                travel(h, hc[0] + 3, hc[2] + 3, f"{tag} desktop (back at its car)", settle=3)
                back = until(lambda: ent(h, hcar)["owner"] == hid, 30)
                print(f"  {tag}: the car back to the host {bool(back)}; door {dn} owner {(ent(h, dn) or {}).get('owner')}", flush=True)
                door = next((p_ for p_ in h(f"mp attachedparts {hcar}")["parts"] if "FRDoor" in p_["name"]), None)
                print(f"  {tag} host: the door off again, left off {door and h('mp falloff %s %s' % (hcar, door['i']))}", flush=True); time.sleep(3)
                h(f"mp moveitem {hcar} 5 0"); time.sleep(6)
                c2 = ent(h, hcar)["pos"]
                travel(L, c2[0] - 4, c2[2] + 6, f"{tag} laptop (back)", settle=3)
                until(lambda: (ent(L, hcar) or {}).get("dormant") is False, 30); time.sleep(5)
                checks(tag, c2[0], c2[2], ((h, L, hcar, "host"),))
                # the door back on for the next cycle
                dn = door_entity(h, hcar, "FRDoor")
                if dn: h(f"mp attachto {dn} {hcar} -1"); time.sleep(3)
        else:
            # ---- home: each works on the other's car
            hp = h("gpos"); L(f"tpground {hp['gx'] + 2} {hp['gz'] - 2}"); time.sleep(3)
            work_on(h, L, "host", "laptop", hcar, "H")
            hc = ent(h, lcar)["pos"]; h(f"tpground {hc[0] + 2.5} {hc[2] + 1}"); time.sleep(3)
            work_on(L, h, "laptop", "host", lcar, "H")
            q = h("gpos"); checks("H", q["gx"], q["gz"], CARS)

            for k in range(1, ROUNDS + 1):
                tag = f"R{k}"
                try: lc, gone = drive_off(tag)
                except RuntimeError as ex: print(f"  {tag}: stopped: {ex}", flush=True); break
                # apart: both change their own cars
                change_parts(h, hcar, f"{tag} host"); h(f"mp moveitem {hcar} 5 0")
                change_parts(L, lcar, f"{tag} laptop")
                time.sleep(3)
                placed, ph1 = meet1(tag, lc)
                ph2 = h("mp physical")
                gap = math.dist(ent(h, lcar)["pos"], ent(L, lcar)["pos"])
                def sw(o):
                    if isinstance(o, dict):
                        if "copySweeps" in o: return o["copySweeps"]
                        for v in o.values():
                            r_ = sw(v)
                            if r_ is not None: return r_
                    return None
                verdict(f"{tag} M1: THE LAPTOP'S CAR WHERE ITS OWNER HAS IT (host's copy)", gap < 0.5, f"{gap:.2f} m; host copy sweeps {sw(ph1)} → {sw(ph2)}")
                checks(f"{tag} M1", lc[0], lc[2], ((L, h, lcar, "laptop"),))
                if placed:
                    hc = ent(h, lcar)["pos"]; h(f"tpground {hc[0] + 2.5} {hc[2] + 1}"); time.sleep(3)
                    ld = L("gpos"); L(f"tpground {ld['gx'] + 1} {ld['gz'] + 1}")
                    work_on(L, h, "laptop", "host", lcar, f"{tag} M1")
                    checks(f"{tag} M1 after work", lc[0], lc[2], ((L, h, lcar, "laptop"),))
                # meeting 2: the laptop walks home to the host's car
                hcp = ent(h, hcar)["pos"]
                travel(L, hcp[0] + 3, hcp[2] - 3, f"{tag} laptop (home, to the host's car)")
                placed = until(lambda: (ent(L, hcar) or {}).get("dormant") is False, 30)
                fr = L(f"mp farrec {hcar}")
                verdict(f"{tag} M2: THE LAPTOP PLACED ITS COPY OF THE HOST'S CAR (not left in the far store)", bool(placed), "" if placed else f"laptop farrec {fr}")
                time.sleep(3)
                gap = math.dist(ent(h, hcar)["pos"], ent(L, hcar)["pos"])
                verdict(f"{tag} M2: THE HOST'S CAR WHERE ITS OWNER HAS IT (laptop's copy)", gap < 0.5, f"{gap:.2f} m")
                travel(h, hcp[0] + 4, hcp[2] + 4, f"{tag} desktop (home)")
                checks(f"{tag} M2", hcp[0], hcp[2], ((h, L, hcar, "host"),))
                if placed:
                    L(f"tpground {hcp[0] + 2.5} {hcp[2] - 1.5}"); time.sleep(3)
                    work_on(h, L, "host", "laptop", hcar, f"{tag} M2")
                    checks(f"{tag} M2 after work", hcp[0], hcp[2], ((h, L, hcar, "host"),))
    finally:
        for n_, s_ in (("L", L), ("h", h)):
            try:
                if GOD.get(n_) is not None: s_(f"set kaposztaleves.s.settings.god {GOD[n_]}")
                s_(f"set {T}.TestAsPlayer False")
            except Exception: pass
        try: L("autodrive off"); L(f"set settingsscript.s.S.BAutoShift {SHIFT}")
        except Exception: pass
        for nm, s_ in (("host", h), ("laptop", L)):
            try:
                an = s_("mp anomalies"); print(f"  anomaly watch, {nm}: {an['count']} {an['lines'][:6]}", flush=True)
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"PLAYROUND: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")
