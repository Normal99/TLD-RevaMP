#!/usr/bin/env python3
"""A bar stool (barszek) locked in a moving bus, a player sitting on it (DevTest playtest 2026-10-24: "the friend on
the bar stool slides away backwards while I drive, the stool stays, he snaps back when I stop").
  S  the joiner sits on the stool in the host's Bus03, the host drives: the host must show the joiner seated on the
     stool, riding along in it, not trailing back while the bus moves
  P  a passenger (the joiner in a seat of the host's parked bus) looking around logs no seat leaves while it sits (the
     repro recorder makes its seat-leave actions from them; a playtest black box: 19 in 0.2 s)
  R  the other way round: the host on the stool, the joiner drives
  C  a cactus the joiner cuts down (the game's TryBreak, a katana's blow): its piece (cactigib01) must reach the host
     too, and stay visible to the host once the joiner puts it in the bus ("a cactus in the bus I can't see")
  busstool.py [--no-build] [--only S,P,R,C] [--speed 12] [--secs 10] [--old]
  (--old: Entities.ItemSeats, WorldBreakGibs and SeatLeaveOnExit off, the build before the fix)"""
import sys, os, time, math, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
ONLY = set(arg("--only", "S,P,R,C").split(","))
SPEED, SECS = arg("--speed", 12.0, float), arg("--secs", 10, int)
OLD = "--old" in sys.argv
T = "TLDRevamp.Net.Entities"
VERDICTS = []

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def ents(x): return {e["net"]: e for e in x("mp entities")["entities"]}

def spawn(x, ident, kind):
    before = set(ents(x))
    print(f"  spawn {kind}:", x(f"devspawn {ident}"), flush=True)
    new = []
    until(lambda: new.extend(n for n, e in ents(x).items() if n not in before and e["part"] < 0) or new, 20)
    if not new: sys.exit(f"NO {kind.upper()} ENTITY")
    return new[0]

def sit_driver(x, net):
    r = {}
    for _ in range(6):
        r = x(f"mp sitdriver {net}")
        if "seat" in r and not r.get("stepped"): return r   # a first call steps beside the seat, the next one sits
        time.sleep(1.5)
    return r

def beside(x, net, side=4.0):
    p = ents(h)[net]["pos"]; x(f"tpground {p[0] + side} {p[2]}"); time.sleep(2.5)

def remote_id(x):
    ps = x("mp players")["players"]
    return ps[0]["id"] if ps else None

def body_on(viewer, stool, who):
    """`who`'s body in the stool's frame on `viewer` (the same frame for both): "me" or a remote player's id"""
    r = viewer(f"mp relplayers {stool}")
    return r["me"] if who == "me" else r["remotes"].get(str(who))

def bus_speed(x, bus):
    return x(f"mount bodies {bus}")["car"]["rbVel"]

def ride(driver, rider, bus, stool, dname):
    """the driver drives SECS s; on the driver's machine the rider's body in the stool's frame, every 0.5 s"""
    print(f"  [{dname}] fuel:", driver("fuelup"), flush=True)
    rid = remote_id(driver)
    rest = body_on(driver, stool, rid); own = body_on(rider, stool, "me")
    seen = next(p for p in driver("mp players")["players"] if p["id"] == rid)
    print(f"  [{dname}] at rest: the rider's own body on the stool {own}, shown on the driver's machine {rest}; "
          f"the driver's machine has it seated {seen['seated']} (seat net {seen['seatNet']} #{seen['seatIdx']}), stool {stool}, bus {bus}", flush=True)
    worst, worst_own, rows, vmax = 0.0, 0.0, [], 0.0
    print(f"  [{dname}] autodrive:", driver(f"autodrive on {SPEED}"), flush=True)
    try:
        for i in range(SECS * 2):
            time.sleep(0.5)
            b, o, v = body_on(driver, stool, rid), body_on(rider, stool, "me"), bus_speed(driver, bus)
            if b is None or rest is None: rows.append(f"t{(i + 1) / 2:.1f} no body"); continue
            d = math.dist(b, rest); worst = max(worst, d) if v > 2 else worst; vmax = max(vmax, v)
            if o and own: worst_own = max(worst_own, math.dist(o, own))
            if i % 2 == 1: print(f"  [{dname}] t{(i + 1) / 2:4.1f}s bus {v:5.2f} m/s: the rider shown {d:5.2f} m from its place on the stool {b}", flush=True)
    finally:
        driver("autodrive on 0"); until(lambda: bus_speed(driver, bus) < 0.3, 15, 1.0); driver("autodrive off")
    time.sleep(3)
    after = body_on(driver, stool, rid)
    back = math.dist(after, rest) if after and rest else -1
    print(f"  [{dname}] stopped: the rider shown {back:.2f} m from its place; on its own machine it moved {worst_own:.2f} m on the stool", flush=True)
    seen = next(p for p in driver("mp players")["players"] if p["id"] == rid)
    verdict(f"{dname}: THE BUS DROVE", vmax > SPEED * 0.5, f"top speed {vmax:.1f} m/s (asked {SPEED})")
    verdict(f"{dname}: THE RIDER ON THE STOOL RIDES ALONG ON THE DRIVER'S SCREEN", not rows and worst < 0.5 and vmax > 2,
            f"farthest from its place on the stool while moving {worst:.2f} m{'; ' + rows[0] if rows else ''}")
    verdict(f"{dname}: THE DRIVER'S MACHINE HAS THE RIDER SEATED ON THE STOOL", seen["seated"] and seen["seatNet"] == stool,
            f"seated {seen['seated']}, seat net {seen['seatNet']} #{seen['seatIdx']} (stool {stool})")

def seat_leaves(x, remote):
    """seat-leave events of this machine's own player in its black box since its last seat-enter, and that seat-enter"""
    import tldenv, subprocess
    t0 = time.time(); x("blackbox"); time.sleep(8)
    if remote:
        q = tldenv.remote_quote(tldenv.REMOTE_GAME + "/BepInEx/tldrevamp-feedback/marks")
        txt = subprocess.run(tldenv.ssh_args() + [f'cat "$(ls -t {q}/*/*-bridge*.json {q}/*-bridge*.json 2>/dev/null | head -1)"'],
                             capture_output=True, text=True, timeout=60).stdout
    else:
        marks = tldenv.GAME_DIR / "BepInEx/tldrevamp-feedback/marks"
        txt = max(marks.rglob("*-bridge*.json"), key=lambda f: f.stat().st_mtime).read_text()
    ev = json.loads(txt)["events"]["rows"]; me = 0 if not remote else 1
    sat = max((e["st"] for e in ev if e["kind"] == "seat-enter" and e.get("from", 0) == me), default=None)
    return sat, sum(1 for e in ev if e["kind"] == "seat-leave" and e.get("from", 0) == me and sat is not None and e["st"] > sat)

def passenger():
    """the joiner sits in a passenger seat of the host's bus (a copy on its machine) and looks around, at the doors and
    windows, as a passenger does before getting out: the game calls GetOut every frame it shows the get-out prompt (and
    gets out on the use key's press only)"""
    for s_ in (h, L):
        try: s_("driveout")
        except RuntimeError: pass
    time.sleep(2)
    beside(L, BUS)
    for _ in range(3):
        r = L(f"mp sitpassenger {BUS}"); time.sleep(1.5)
        if L("get mainscript.s.player.Bsitting")["value"] is True: break
    print("  joiner sits in a passenger seat:", r, flush=True); time.sleep(2)
    try: p0 = L(f"get {T}.SeatIgnoredPairs")["value"]; c0 = L(f"get {T}.LocalGetOutCalls")["value"]
    except RuntimeError: p0 = c0 = None
    for yaw in (-150, -110, -80, -40, 40, 80, 110, 150, 0):
        for pitch in (0, 25):
            L(f"set mainscript.s.player.FxRot {yaw}"); L(f"set mainscript.s.player.FyRot {pitch}"); time.sleep(0.4)
    # the game's own per-frame call while the get-out prompt shows (in this hidden window it didn't show): 20 frames of it
    print("  20 frames of the game's GetOut without the use key:", L("mp getouttry 20"), flush=True)
    try: p1 = L(f"get {T}.SeatIgnoredPairs")["value"]; c1 = L(f"get {T}.LocalGetOutCalls")["value"]
    except RuntimeError: p1 = c1 = None
    print(f"  the game's GetOut calls while it sat and looked around: {c1 - c0 if c0 is not None else 'n/a'}", flush=True)
    seated = L("get mainscript.s.player.Bsitting")["value"]
    sat, n = seat_leaves(L, True)
    verdict("P: A SEATED PASSENGER LOOKING AROUND LOGS NO SEAT LEAVES (THE REPRO RECORDER MAKES ITS ACTIONS FROM THEM)", seated is True and sat is not None and n == 0,
            f"still seated {seated}; seat leaves in its black box since sitting down: {n}")
    print(f"  collider pairs its body ignores (Entities.SeatIgnoredPairs): after sitting {p0}, after looking around {p1}", flush=True)
    L("set mainscript.s.player.FxRot 0"); L("set mainscript.s.player.FyRot 0")

def stool_scene():
    global BUS, STOOL
    rp = h("roadpoint"); h(f"tpground {rp['gx']} {rp['gz']}"); time.sleep(3)
    BUS = spawn(h, 179, "Bus03Full"); bp = ents(h)[BUS]["pos"]
    print("  bus", BUS, "at", bp, flush=True)
    L(f"tpground {bp[0] + 6} {bp[2]}"); time.sleep(3)
    if not until(lambda: BUS in ents(L), 30): sys.exit("THE JOINER NEVER GOT THE BUS")
    print("  host sits at the wheel:", sit_driver(h, BUS), flush=True); time.sleep(2)
    drv = h(f"mp relplayers {BUS}")["me"]; print("  the driver's seat in the bus's frame:", drv, flush=True)
    h("driveout"); time.sleep(2)
    beside(h, BUS)
    STOOL = spawn(h, 201, "barszek")
    print("  the stool into the aisle behind the driver:", h(f"mp putrel {BUS} {STOOL} 0 {drv[1] - 0.6:.2f} {drv[2] - 2.5:.2f} clear"), flush=True)
    if not until(lambda: "passengerRoom" in h(f"mount probe {STOOL}").get("physlock", "") or "trunk" in h(f"mount probe {STOOL}").get("physlock", ""), 15):
        print("  NOT LOCKED: ", h(f"mount probe {STOOL}"), flush=True)
    until(lambda: STOOL in ents(L), 20); time.sleep(2)
    for nm, x in (("host", h), ("joiner", L)):
        q = x(f"mount probe {STOOL}"); print(f"  stool on the {nm}: physlock {q.get('physlock')!r} parent {q.get('parent')!r} rb {q.get('rb')}", flush=True)
    verdict("SETUP: THE STOOL IS LOCKED INTO THE BUS ON BOTH", all(x(f"mount probe {STOOL}").get("physlock") for x in (h, L)))

def cactus():
    """the joiner cuts a cactus near the road; its piece must be an object both machines have"""
    global BUS
    for s_ in (h, L):
        try: s_("driveout")
        except RuntimeError: pass
    rp = h("roadpoint"); found = None
    at = [tuple(map(float, a.split(","))) for a in arg("--at", "").split(";") if a]   # where to look first (x,z;x,z)
    spots = at + [(rp["gx"] + 150 * k, rp["gz"] + 40 * (k % 3)) for k in range(12)]
    for k, (x, z) in enumerate(spots):   # until a cactus stands within 150 m
        L(f"tpground {x} {z}"); time.sleep(4)
        r = L("mp breakables 150 cact"); bl = r["breakables"]
        print(f"  breakables within 150 m of {x:.0f},{z:.0f}:", r["kinds"], flush=True)
        c = [b for b in bl if not b["destroyed"] and "cact" in (b["gib"] + b["root"]).lower()]
        if c: found = c[0]; break
    if not found: verdict("C: A CACTUS TO CUT", False, f"none within 150 m of {len(spots)} spots"); return
    print("  cactus:", found, flush=True)
    cx, cy, cz = found["pos"]
    L(f"tpground {cx + 1.5} {cz}"); h(f"tpground {cx - 2.5} {cz + 1.5}"); time.sleep(4)
    name = found["root"]
    hb = [b for b in h(f"mp breakables 10 {name}")["breakables"] if math.dist(b["pos"], found["pos"]) < 1.0]
    print("  the host's view of that cactus:", hb, flush=True)
    bh, bl0 = set(ents(h)), set(ents(L))
    print("  joiner cuts it:", L(f"mp breaknear {name} 4"), flush=True); time.sleep(4)
    newL = {n: e for n, e in ents(L).items() if n not in bl0}; newH = {n: e for n, e in ents(h).items() if n not in bh}
    print(f"  new objects on the joiner {[(n, e['name']) for n, e in newL.items()]}, on the host {[(n, e['name']) for n, e in newH.items()]}", flush=True)
    gibL = [n for n, e in newL.items() if "cact" in e["name"].lower()]
    hb2 = [b for b in h(f"mp breakables 10 {name}")["breakables"] if math.dist(b["pos"], found["pos"]) < 1.0]
    verdict("C: THE CACTUS PIECE IS SHARED (AN OBJECT ON THE JOINER)", bool(gibL), f"joiner's new objects {sorted(e['name'] for e in newL.values())}")
    verdict("C: THE HOST HAS THE CACTUS PIECE", any(n in newH for n in gibL), f"host's new objects {sorted(e['name'] for e in newH.values())}")
    verdict("C: THE CACTUS IS DOWN ON THE HOST TOO", not hb2 or all(b["destroyed"] for b in hb2), f"host's cactus there {hb2}")
    if gibL:   # a bus of its own beside the cactus: the stool scene's bus is far away, and the pieces are only kept near
        BUS = spawn(h, 179, "Bus03Full"); until(lambda: BUS in ents(L), 30)
    if gibL and BUS:
        gib = gibL[0]
        # carries it to the bus (a teleport drops what is held, so the joiner stands at the bus and takes it to hand)
        beside(L, BUS); time.sleep(2)
        print("  the joiner takes the piece:", L(f"mp pickupnet {gib}"), flush=True); time.sleep(1.5)
        print("  ... and drops it at the bus:", L("mp drop"), flush=True); time.sleep(1)
        el = ents(L).get(gib)
        print(f"  on the joiner: the bus {BUS} {'here' if BUS in ents(L) else 'MISSING'}, the piece {el and el['pos']}", flush=True)
        print("  owner of the piece now: on the joiner", el and el.get("owner"), ", on the host", (ents(h).get(gib) or {}).get("owner"), flush=True)
        try: print("  the joiner puts the piece in the bus:", L(f"mp putrel {BUS} {gib} 0 1.2 -3 clear"), flush=True)
        except RuntimeError as ex:   # dropped beside the host's bus, the piece went to the host
            print("  ", ex, "- the host puts it in:", h(f"mp putrel {BUS} {gib} 0 1.2 -3 clear"), flush=True)
        time.sleep(4)
        eh, el = ents(h).get(gib), ents(L).get(gib)
        d = math.dist(eh["pos"], el["pos"]) if eh and el else -1
        verdict("C: THE PIECE PUT IN THE BUS IS AT THE SAME SPOT ON BOTH", eh is not None and 0 <= d < 0.5, f"host {eh and eh['pos']}, joiner {el and el['pos']}")

BUS = STOOL = None
L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    plock = None
    try:
        if not join_both(h, L): sys.exit("JOINER NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L):
            s_("set kaposztaleves.s.settings.god True")
            for f in ("ItemSeats", "WorldBreakGibs", "SeatLeaveOnExit"):
                try: s_(f"set {T}.{f} {not OLD}")
                except RuntimeError as ex: print(f"  {f}:", ex, flush=True)   # not in the build before the fix
        h("set settingsscript.s.S.BAutoShift True")
        plock = h("get settingsscript.s.S.IPhysicsLock")["value"]
        if not plock: h("set settingsscript.s.S.IPhysicsLock 1")
        print("mode", "old" if OLD else "new", "physics lock", plock, flush=True)
        if ONLY & {"S", "P", "R"}:
            stool_scene()
        if "S" in ONLY:
            beside(L, BUS); print("  joiner sits on the stool:", L(f"mp sitobj {STOOL}"), flush=True); time.sleep(1)
            print("  host sits at the wheel:", sit_driver(h, BUS), flush=True); time.sleep(3)
            ride(h, L, BUS, STOOL, "S HOST DRIVES, JOINER ON THE STOOL")
        if "P" in ONLY:
            passenger()
        if "R" in ONLY:
            for s_ in (h, L):
                try: s_("driveout")
                except RuntimeError as ex: print("  driveout:", ex, flush=True)
            time.sleep(3)
            beside(L, BUS); print("  joiner sits at the wheel:", sit_driver(L, BUS), flush=True)
            until(lambda: ents(h)[BUS]["owner"] != 0, 15)
            beside(h, BUS); print("  host sits on the stool:", h(f"mp sitobj {STOOL}"), flush=True); time.sleep(3)
            ride(L, h, BUS, STOOL, "R JOINER DRIVES, HOST ON THE STOOL")
        if "C" in ONLY:
            cactus()
    finally:
        for s_ in (h, L):
            try: s_("autodrive off"); s_("set kaposztaleves.s.settings.god False")
            except Exception: pass
        if plock is not None and not plock:
            try: h(f"set settingsscript.s.S.IPhysicsLock {plock}")
            except Exception: pass
        L("mp stop"); h("mp stop")
print("RESULT:", "ALL PASS" if VERDICTS and all(ok for _, ok in VERDICTS) else "FAILED: " + "; ".join(n for n, ok in VERDICTS if not ok), flush=True)
