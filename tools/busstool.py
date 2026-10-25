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
DIAG = "--diag" in sys.argv
ONLY = set(arg("--only", "S,P,R,C,K,M").split(","))
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

def katana():
    """the joiner carries a katana in its inventory (stored, hidden) and drives the bus, the host rides along: the host's
    copy of the stored katana stays with the joiner (in the recordings it jumped 50 m every 5-12 s on the host)"""
    global BUS
    for s_ in (h, L):
        try: s_("driveout")
        except RuntimeError: pass
    rp = h("roadpoint"); h(f"tpground {rp['gx']} {rp['gz']}"); time.sleep(3)
    BUS = spawn(h, 179, "Bus03Full"); bp = ents(h)[BUS]["pos"]
    L(f"tpground {bp[0] + 4} {bp[2]}"); time.sleep(3)
    until(lambda: BUS in ents(L), 30)
    K = spawn(L, 63, "katana"); until(lambda: K in ents(h), 20); time.sleep(1)
    print("  the joiner picks the katana up:", L(f"mp pickupnet {K}"), flush=True); time.sleep(2)
    n_inv = int(L("get mainscript.s.player.inventory.Count")["value"]); s0 = int(L("get mainscript.s.player.selectedInv")["value"])
    L(f"call mainscript.s.player.InvSwitchTo {(s0 + 1) % n_inv} False"); time.sleep(2.5)
    a, b = ents(L)[K], ents(h)[K]
    verdict("K: THE KATANA IS STORED ON BOTH", a["stored"] and b["stored"], f"joiner {a['stored']}, host {b['stored']}")
    print("  joiner sits at the wheel:", sit_driver(L, BUS), flush=True)
    until(lambda: ents(h)[BUS]["owner"] != 0, 15)
    beside(h, BUS)
    for _ in range(3):
        r = h(f"mp sitpassenger {BUS}"); time.sleep(1.5)
        if h("get mainscript.s.player.Bsitting")["value"] is True: break
    print("  host sits in a passenger seat:", r, flush=True)
    print("  fuel:", L("fuelup"), flush=True)
    rid = remote_id(h)
    def gap(x, who):   # the stored katana's copy from the joiner's body, in the bus's frame, on this machine
        kp = x(f"mp relpos {BUS} {K}")["items"].get(str(K)); body = body_on(x, BUS, who)
        return math.dist(kp, body) if kp and body else None, kp
    g0, _ = gap(h, rid); print(f"  at rest: the host's katana copy {g0} m from the joiner", flush=True)
    worst, jump, last, vmax = 0.0, 0.0, None, 0.0
    tp0 = h(f"get {T}.CopyTeleports")["value"]
    print("  autodrive:", L(f"autodrive on {SPEED}"), flush=True)
    try:
        for i in range(SECS * 2):
            time.sleep(0.5)
            d, kp = gap(h, rid); v = bus_speed(L, BUS); vmax = max(vmax, v)
            if d is not None and v > 2: worst = max(worst, d)
            if kp and last: jump = max(jump, math.dist(kp, last))
            last = kp
            if i % 2 == 1:
                own, _ = gap(L, "me"); ka, kb = ents(L)[K], ents(h)[K]
                print(f"  t{(i + 1) / 2:4.1f}s bus {v:5.2f} m/s: the host's katana copy {d if d is None else round(d, 2)} m from the joiner "
                      f"(on the joiner its own {own if own is None else round(own, 2)} m; states sent {ka.get('seqOut')}, the host got {kb.get('recv')})", flush=True)
                if DIAG: print(f"      interp {h(f'mp interp {K}')} copystate {h(f'mp copystate {K}')}", flush=True)
                if DIAG: print(f"      host copy: pos {kb['pos']} lastIn {kb.get('lastIn')} physical {kb.get('physical')} byBody {kb.get('byBody')} rt {kb.get('rt')} jumps {kb.get('jumps')} extrap {kb.get('extrapFrames')}; joiner lastSent {ka.get('lastSent')} pos {ka['pos']}", flush=True)
    finally:
        L("autodrive on 0"); until(lambda: bus_speed(L, BUS) < 0.3, 15, 1.0); L("autodrive off")
    verdict("K: THE BUS DROVE", vmax > SPEED * 0.5, f"top speed {vmax:.1f} m/s")
    tps = h(f"get {T}.CopyTeleports")["value"] - tp0
    # it is hidden: what matters is that it follows the joiner's stream (about as far behind as the stream's delay while the
    # bus speeds up) and is never put 50 m on at once (CopyTeleports: the copies this machine teleported)
    verdict("K: THE HOST'S COPY OF THE STORED KATANA STAYS WITH THE JOINER", worst < 5.0 and jump < 5.0 and tps == 0,
            f"farthest {worst:.2f} m from the joiner's body while moving, biggest step between samples {jump:.2f} m (in the bus's frame), copies teleported on the host {tps}")

def creature():
    """the joiner's Munkas01 (a creature), seen by the host near and from 150 m (in the recordings the host's copy stood
    6 m off and 4 m lower than the joiner's own, 150 m from both players)"""
    for s_ in (h, L):
        try: s_("driveout")
        except RuntimeError: pass
    rp = h("roadpoint"); x, z = rp["gx"], rp["gz"]
    h(f"tpground {x} {z}"); L(f"tpground {x + 3} {z}"); time.sleep(3)
    M = spawn(L, 317, "Munkas01"); until(lambda: M in ents(h), 20)
    def apart(tag, secs):
        worst = 0.0
        for i in range(secs):
            time.sleep(1)
            a, b = ents(L).get(M), ents(h).get(M)
            if not a or not b: print(f"  [{tag}] t{i + 1}: joiner {a and a['pos']} host {b and b['pos']}", flush=True); continue
            d = math.dist(a["pos"], b["pos"]); worst = max(worst, d)
            if i % 3 == 2 or i == secs - 1:
                print(f"  [{tag}] t{i + 1:2d}s: the joiner's creature {a['pos']}, the host's copy {b['pos']}: {d:.2f} m apart (dy {b['pos'][1] - a['pos'][1]:+.2f})", flush=True)
                if DIAG: print("      host copystate:", h(f"mp copystate {M}"), flush=True)
        return worst
    near = apart("near", 15)
    cp = ents(L)[M]["pos"]
    h(f"tpground {cp[0] + 150} {cp[2]}"); L(f"tpground {cp[0] + 150} {cp[2] + 5}"); time.sleep(3)
    far = apart("150 m away", 30)
    h(f"tpground {cp[0] + 5} {cp[2]}"); L(f"tpground {cp[0] + 6} {cp[2] + 3}"); time.sleep(3)
    back = apart("back near", 9)
    verdict("M: THE HOST'S COPY OF THE CREATURE IS WHERE THE JOINER'S IS", max(near, far, back) < 2.0,   # walking: ~0.15 s behind
            f"farthest apart near {near:.2f} m, from 150 m {far:.2f} m, back near {back:.2f} m")
    # as in the recording: both in the bus, the joiner driving away from it, then stopped 150+ m on
    global BUS
    cp = ents(L)[M]["pos"]
    BUS = spawn(h, 179, "Bus03Full"); until(lambda: BUS in ents(L), 30)
    print("  joiner sits at the wheel:", sit_driver(L, BUS), flush=True)
    until(lambda: ents(h)[BUS]["owner"] != 0, 15)
    beside(h, BUS)
    for _ in range(3):
        r = h(f"mp sitpassenger {BUS}"); time.sleep(1.5)
        if h("get mainscript.s.player.Bsitting")["value"] is True: break
    L("fuelup"); print("  autodrive:", L(f"autodrive on {SPEED}")["active"], flush=True)
    try:
        until(lambda: math.dist(ents(L)[BUS]["pos"], ents(L)[M]["pos"]) > 160, 40, 1.0)
    finally:
        L("autodrive on 0"); until(lambda: bus_speed(L, BUS) < 0.3, 15, 1.0); L("autodrive off")
    print(f"  stopped {math.dist(ents(L)[BUS]['pos'], ents(L)[M]['pos']):.0f} m from the creature", flush=True)
    chase = apart("bus stopped", 30)
    verdict("M: AFTER THE BUS RAN AWAY FROM IT, THE HOST'S COPY IS WHERE THE JOINER'S IS", chase < 1.0, f"farthest apart {chase:.2f} m")
    # killed (a creature the bus hit, or shot): its body is the limbs, the root is left where it died
    for s_ in (h, L):
        try: s_("driveout")
        except RuntimeError: pass
    cp = ents(L)[M]["pos"]; h(f"tpground {cp[0] + 6} {cp[2]}"); L(f"tpground {cp[0] - 6} {cp[2]}"); time.sleep(3)
    print("  the joiner kills it:", L(f"mp ragdoll {M} kill"), flush=True); time.sleep(10)
    a, b = L(f"mp ragdoll {M}"), h(f"mp ragdoll {M}")
    def mid(r): ls = [l[:3] for l in r.get("limbs", []) if l and not l[6]]; return [sum(c) / len(ls) for c in zip(*ls)] if ls else None
    ma, mb = mid(a), mid(b)
    print(f"  dead: the joiner's root {a.get('root')} limbs' middle {ma}; the host's root {b.get('root')} limbs' middle {mb}", flush=True)
    print(f"  mp entities: joiner {ents(L)[M]['pos']} host {ents(h)[M]['pos']}", flush=True)
    dl = math.dist(ma, mb) if ma and mb else -1
    verdict("M: THE DEAD CREATURE'S BODY IS IN THE SAME PLACE ON BOTH", 0 <= dl < 0.5, f"limbs' middle {dl:.2f} m apart, roots {math.dist(a['root'], b['root']):.2f} m apart")

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
            for f in ("ItemSeats", "WorldBreakGibs", "SeatLeaveOnExit", "StoredByTransform"):
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
        if "K" in ONLY:
            katana()
        if "M" in ONLY:
            creature()
    finally:
        for s_ in (h, L):
            try: s_("autodrive off"); s_("set kaposztaleves.s.settings.god False")
            except Exception: pass
        if plock is not None and not plock:
            try: h(f"set settingsscript.s.S.IPhysicsLock {plock}")
            except Exception: pass
        L("mp stop"); h("mp stop")
print("RESULT:", "ALL PASS" if VERDICTS and all(ok for _, ok in VERDICTS) else "FAILED: " + "; ".join(n for n, ok in VERDICTS if not ok), flush=True)
