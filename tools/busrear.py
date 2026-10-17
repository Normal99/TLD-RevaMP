#!/usr/bin/env python3
"""A passenger in the back of a bus (Bus01: the rear section is its own body on a joint, outside the bus's hierarchy,
33 of its 81 seats). The host drives, the laptop sits in a rear seat: the host must see the laptop seated in the bus
and riding along — not standing where they got in. Then the other way round (the host in the back, the laptop drives).
  --cargo: instead, the laptop's lamp dropped in the back of the host's bus, then the host drives
  --stand: instead, the laptop stands in the back (not seated) while the host drives [--speed m/s] [--limbs-off: the old behaviour]
           [--tow: nobody drives, the host's machine tows the bus] [--baseline: the host stands in its own bus, towed]
  tools/busrear.py [--no-build] [--relay iad|off] [--secs 15] [--ab: first a run with the fix off - expected to FAIL]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

LIMBS = "--limbs-off" not in sys.argv   # A/B: Entities.VehicleLimbsByBody (v0.65.53)
RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
SECS = int(sys.argv[sys.argv.index("--secs") + 1]) if "--secs" in sys.argv else 15
VERDICTS = []
WHEELS = []

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

def ent(x, net):
    return next((e for e in x("mp entities")["entities"] if e["net"] == net), None)

def other(x):
    ps = x("mp players")["players"]
    return ps[0] if ps else None

def sit_driver(x, net):
    for _ in range(6):
        r = x(f"mp sitdriver {net}")
        if "seat" in r and not r.get("stepped"): return r
        time.sleep(1.5)
    return r

def sit_rear(x, net, seat0):
    """walk up to the bus and sit in the first free rear seat (index >= seat0 in the vehicle's seat list)"""
    p = ent(x, net)["pos"]; x(f"tpground {p[0] + 4} {p[2]}"); time.sleep(2)
    sl = x(f"seats {net}"); rows = sl["seats(i,name,body,driver,redirect,free,active)"]
    print("    outside slots:", sl.get("outside"), flush=True)
    print(f"    seats: root {sl['root']} own {sl['own']} slots outside {sl['slotsOutside']}, bodies {sorted({r[2] for r in rows})}, "
          f"free {sum(r[5] for r in rows)}/{len(rows)}", flush=True)
    rear = [r[0] for r in rows if r[2] != sl["root"] and r[5]]
    print(f"    free seats on another body: {rear[:10]}{'...' if len(rear) > 10 else ''}", flush=True)
    r = {"error": "no free rear seat"}
    for i in rear:
        try: r = x(f"sitseat {i} net={net}")
        except RuntimeError as ex: r = {"error": str(ex)}; continue
        if "sat" in r: return r
    return r

def ride(driver, rider, tag):
    """drive SECS s; each second where the driver's machine shows the rider vs the bus"""
    driver("autodrive on 10")
    worst, seated_all, rows = 0.0, True, []
    p0 = ent(driver, NET)["pos"]
    try:
        for i in range(SECS):
            time.sleep(1)
            o = other(driver)   # the rider as the driver's machine shows them
            bus = ent(driver, NET)["pos"]
            if o is None: rows.append(f"t{i+1} rider not shown"); seated_all = False; continue
            d = math.dist((o["shown"][0], o["shown"][2]), (bus[0], bus[2]))
            worst = max(worst, d)
            if not o["seated"]: seated_all = False
            print(f"  {tag} t{i+1:>2}s rider seated {o['seated']} seatNet {o['seatNet']} idx {o['seatIdx']} | {d:.1f} m from the bus's root", flush=True)
            if i == 6:
                ws = wheelspin(h, L); WHEELS.append(ws)
                print(f"  {tag} wheel turn in 0.5 s (deg, synced): {ws}", flush=True)
    finally:
        driver("autodrive on 0"); until(lambda: driver(f"mount bodies {NET}")["car"]["rbVel"] < 0.3, 12, 1.0); driver("autodrive off")
    moved = math.dist(p0, ent(driver, NET)["pos"])
    return seated_all, worst, moved

def ents(x): return {e["net"]: e for e in x("mp entities")["entities"]}

def wheelspin(h, L, secs=0.5):
    """every wheel of the bus on the laptop's copy and on the host: how far it turned in `secs` while driving (degrees)"""
    def q_angle(a, b):
        d = abs(sum(x * y for x, y in zip(a, b))); return math.degrees(2 * math.acos(min(1.0, d)))
    out = {}
    for name, x in (("host", h), ("laptop", L)):
        w0 = {w[0]: w for w in x(f"mp outsidebodies {NET}")["wheels(name,synced,rotInVehicle)"]}
        time.sleep(secs)
        w1 = {w[0]: w for w in x(f"mp outsidebodies {NET}")["wheels(name,synced,rotInVehicle)"]}
        out[name] = {k: (round(q_angle(w0[k][2], w1[k][2]), 1), w0[k][1]) for k in w0 if k in w1}
    return out

def stand(h, L):
    """a player stands in the back of the host's bus (not seated) while it drives: where they are in the bus's frame.
    Default: the laptop stands (on a copy), the host drives (autodrive). --tow: nobody at the wheel, the host's machine
    tows the bus (mp tow: the same ramp every run). --baseline (with --tow): the HOST stands in its own bus — the game's
    own physics, what the copy is measured against."""
    import statistics
    base = "--baseline" in sys.argv; tow = "--tow" in sys.argv or base
    S, sname = (h, "host") if base else (L, "laptop")
    me = lambda x: x(f"mp relplayers {NET}")["late"].get("-1")
    if not tow: print("  host sits:", sit_driver(h, NET), flush=True)
    p = ent(h, NET)["pos"]
    if S is L or tow: h(f"tpground {p[0] - 8} {p[2]}")
    S(f"tpground {p[0] + 4} {p[2]}"); time.sleep(2)
    for s_ in (h, L): s_(f"set TLDRevamp.Net.Entities.VehicleLimbsByBody {LIMBS}")
    time.sleep(1)
    r = S(f"mp standon {NET} 0.15 rear"); print(f"  {sname} stands in the back:", r, flush=True)
    track = []
    for k in range(12):
        time.sleep(0.25); track.append((round((k + 1) * 0.25, 2), S(f"mp relplayers {NET}").get("me"), (S("get TLDRevamp.Net.Entities.FootWhy").get("value") or "")[:28]))
    print(f"  {sname} in the bus's frame after standing (t, local, footing):", track[::3], flush=True)
    print("  outside bodies — laptop:", L(f"mp outsidebodies {NET}"), "| limb body moves", L("get TLDRevamp.Net.Entities.LimbBodyMoves").get("value"), flush=True)
    S(f"shot rear_inside_{sname}")
    fw = S("get TLDRevamp.Net.Entities.FootWhy").get("value") or ""
    if "error" in r or f"on {NET}" not in fw or not (0.5 < r.get("local", [0, 9, 0])[1] < 1.2):
        verdict(f"SETUP: THE {sname.upper()} STANDS INSIDE THE BACK OF THE BUS", False, f"{r} footing {fw!r} — INVALID, nothing driven"); return
    verdict(f"SETUP: THE {sname.upper()} STANDS INSIDE THE BACK OF THE BUS", True, f"footing {fw!r}")
    lid = h("mp players")["players"][0]["id"]
    spd = float(sys.argv[sys.argv.index("--speed") + 1]) if "--speed" in sys.argv else 10.0
    if tow: print("  tow:", h(f"mp tow {NET} {spd} 2"), flush=True)
    else: h(f"autodrive on {spd}")
    diffs, samples = [], []
    try:
        for i in range(SECS * 2):
            time.sleep(0.5)
            a = me(S)
            b = h(f"mp relplayers {NET}")["late"].get(str(lid)) if not base else a
            if a and b: diffs.append(math.dist(a, b)); samples.append((i / 2, a, b))
            if i == 8: S(f"shot rear_stand_{sname}")
            if i in (2, 4, 6):
                mb = S(f"mount bodies {NET}")
                rows_ = [(r_.get("name"), r_.get("kin"), r_.get("v")) for r_ in mb.get("bodies", [])]
                print(f"    t{i/2}: {sname}'s bus bodies {rows_} | bus v {mb.get('car', {}).get('rbVel')} | player v {S('get mainscript.s.player.RB.velocity').get('value')} | footing {S('get TLDRevamp.Net.Entities.FootWhy').get('value')}", flush=True)
                print(f"           interior {S('get mainscript.s.player.interior').get('value')} sensed {S('get mainscript.s.player.interiorsens.sensed').get('value')} T {S('get mainscript.s.player.interiorsens.T').get('value')} | interiorRB {S('get mainscript.s.player.interiorRB').get('value')}", flush=True)
    finally:
        if tow: h(f"mp tow {NET} 0 2"); until(lambda: h(f"mount bodies {NET}")["car"]["rbVel"] < 0.3, 12, 1.0); h("mp tow off")
        else: h("autodrive on 0"); until(lambda: h(f"mount bodies {NET}")["car"]["rbVel"] < 0.3, 12, 1.0); h("autodrive off")
    print(f"  {sname}'s footing at the end:", S("get TLDRevamp.Net.Entities.FootWhy").get("value"), flush=True)
    print(f"  samples (t, {sname}, host's view):", samples[::4], flush=True)
    # along the bus (z: sliding to the back - the bug) apart from across it (x: sway as the bus pulls out and turns)
    along = max((abs(a_[2] - samples[0][1][2]) for _, a_, _ in samples), default=-1)
    across = max((abs(a_[0] - samples[0][1][0]) for _, a_, _ in samples), default=-1)
    fw_end = S("get TLDRevamp.Net.Entities.FootWhy").get("value") or ""
    verdict(f"STANDING IN THE BACK: THE {sname.upper()} RIDES ALONG, NOT SLID TO THE BACK, STILL IN THE BUS", samples and along < 0.5 and f"on {NET}" in fw_end,
            f"farthest along the bus from where it stood {along:.2f} m, across {across:.2f} m (sway, reported), on the bus at the end {f'on {NET}' in fw_end}" + (" (BASELINE)" if base else ""))
    if not base:
        med = statistics.median(diffs) if diffs else -1
        verdict("STANDING IN THE BACK: THE HOST SHOWS THE LAPTOP WHERE IT IS IN THE BUS", diffs and med < 0.3 and max(diffs) < 1.0, f"median {med:.2f} m, max {max(diffs, default=-1):.2f} m, n {len(diffs)}")

def cargo(h, L):
    """the laptop's own lamp dropped at its feet in the back of the host's bus, then the host drives: the lamp must
    become the host's (it rides in the host's bus) and stay in the back on both machines; the bus takes no hit from it"""
    T = "TLDRevamp.Net.Entities"
    p = ent(h, NET)["pos"]; L(f"tpground {p[0] + 12} {p[2]}"); time.sleep(2)   # spawned outside the bus's claim margin
    lb = set(ents(L))
    print("  laptop spawns a lamp:", L("devspawn regizseblampa"), flush=True)
    until(lambda: any(n not in lb and "regizseblampa" in e["name"] for n, e in ents(L).items()), 20)
    lamp = next(n for n, e in ents(L).items() if n not in lb and "regizseblampa" in e["name"])
    print("  lamp", lamp, "owner on the laptop's view", ents(L)[lamp]["owner"], flush=True)
    until(lambda: lamp in ents(h), 20); time.sleep(1)
    print("  host sits:", sit_driver(h, NET), flush=True)
    print("  laptop sits in the back:", sit_rear(L, NET, 0), flush=True)
    L("set mainscript.s.player.FxRot 0"); L("set mainscript.s.player.FyRot 50"); time.sleep(0.5)
    print("  laptop picks the lamp up:", L(f"mp pickupnet {lamp}"), flush=True); time.sleep(2)
    near0, cont0 = h(f"get {T}.CargoClaimedNear")["value"], h(f"get {T}.CargoClaimedOnContact")["value"]
    h("crash rec start 40"); time.sleep(0.3)
    print("  laptop drops it:", L("mp drop"), flush=True); time.sleep(4)
    shot = lambda tag: (h(f"shot rear_{tag}"), L(f"shot rear_{tag}"))
    eh, el = ents(h)[lamp], ents(L)[lamp]
    print(f"  after the drop: host sees owner {eh['owner']} proxy {eh['proxy']}, laptop sees owner {el['owner']}; claims near "
          f"{h(f'get {T}.CargoClaimedNear')['value'] - near0} on contact {h(f'get {T}.CargoClaimedOnContact')['value'] - cont0}", flush=True)
    print(f"  lamp probe host {h(f'mount probe {lamp}')}\n             laptop {L(f'mount probe {lamp}')}", flush=True)
    shot("dropped")
    # either the host's (a loose body in the host's bus), or locked into the bus's passenger room by the game (parented
    # to it on both machines: it rides the bus, no body of its own to shove it - 2026-10-08: that's what happens)
    ph, pl = h(f"mount probe {lamp}"), L(f"mount probe {lamp}")
    locked = all(q.get("physlock", "").endswith("passengerRoom") and q.get("rb") is None and "BusBack" in q.get("parent", "") for q in (ph, pl))
    verdict("CARGO: THE LAMP DROPPED IN THE BACK IS THE HOST'S OR LOCKED INTO THE BUS ON BOTH", (eh["owner"] == 0 and not eh["proxy"] and el["owner"] == 0) or locked,
            f"host owner {eh['owner']} proxy {eh['proxy']}, laptop sees {el['owner']}; locked into the back on both {locked}")
    # the host drives: where the lamp is relative to the bus, on both machines
    def rel(x):
        e = ents(x); return math.dist(e[lamp]["pos"], e[NET]["pos"])
    d0h, d0l = rel(h), rel(L)
    h("autodrive on 10"); worst = 0.0
    try:
        for i in range(15):
            time.sleep(1); dh, dl = rel(h), rel(L)
            worst = max(worst, abs(dh - d0h), abs(dl - d0l))
            if i % 3 == 2: print(f"  t{i+1:>2}s lamp from the bus's root: host {dh:.1f} m (start {d0h:.1f}), laptop {dl:.1f} m (start {d0l:.1f})", flush=True)
    finally:
        h("autodrive on 0"); until(lambda: h(f"mount bodies {NET}")["car"]["rbVel"] < 0.3, 12, 1.0); h("autodrive off")
    shot("driven")
    rc = h("crash rec stop"); cc = next((c for c in rc["cars"] if c.get("net") == NET), {})
    print(f"  bus while the lamp rode: damage {cc.get('damageMaxV')} spin {cc.get('peakSpinDeg')} parts lost {cc.get('partsLost')}", flush=True)
    verdict("CARGO: THE LAMP RIDES IN THE BACK ON BOTH MACHINES", worst < 3, f"worst change of its distance from the bus's root {worst:.1f} m")
    verdict("CARGO: THE BUS TAKES NO HIT FROM IT", (cc.get("damageMaxV") or 0) < 3 and (cc.get("partsLost") or 0) == 0,
            f"damage {cc.get('damageMaxV')} spin {cc.get('peakSpinDeg')} parts lost {cc.get('partsLost')}")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    gods = {}
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, s_("get kaposztaleves.s.settings.god").get("value")); s_("set kaposztaleves.s.settings.god True")
        rp = h("roadpoint"); h(f"tpground {rp['gx']} {rp['gz']}"); time.sleep(3)
        before = {e["net"] for e in h("mp entities")["entities"]}
        print("bus:", h("devspawn 106"), flush=True)
        if not until(lambda: any(e["car"] and e["net"] not in before for e in h("mp entities")["entities"]), 20): sys.exit("NO BUS ENTITY")
        NET = next(e["net"] for e in h("mp entities")["entities"] if e["car"] and e["net"] not in before)
        p = ent(h, NET)["pos"]; L(f"tpground {p[0] + 6} {p[2]}"); time.sleep(3)
        if not until(lambda: ent(L, NET) is not None, 30): sys.exit("THE LAPTOP NEVER GOT THE BUS")
        seat0 = h(f"mount bodies {NET}")["rootSeats"]   # the rear section's seats come after the bus's own
        print("bus net", NET, "own seats", seat0, flush=True)
        if "--cargo" in sys.argv: cargo(h, L)
        if "--stand" in sys.argv: stand(h, L)
        runs = [] if "--cargo" in sys.argv or "--stand" in sys.argv else [(h, L, "host", "laptop", True), (L, h, "laptop", "host", True)]
        if "--ab" in sys.argv and runs: runs.insert(0, (h, L, "host", "laptop", False))   # the old behaviour first, for reference
        for driver, rider, dn, rn, fix in runs:
            for s_ in (h, L): s_(f"set TLDRevamp.Net.Entities.RearSeats {fix}")
            tag = f"{dn.upper()} DRIVES, {rn.upper()} IN THE BACK" + ("" if fix else " (FIX OFF, the old behaviour)")
            for s_ in (h, L):
                try: s_("driveout")
                except RuntimeError: pass
            time.sleep(2)
            print(f"  {dn} sits:", sit_driver(driver, NET), flush=True)
            print(f"  {rn} sits in the back:", sit_rear(rider, NET, seat0), "seat", rider("seatlook").get("seat"), flush=True)
            time.sleep(3)
            seated, worst, moved = ride(driver, rider, tag)
            verdict(f"{tag}: SHOWN SEATED, RIDING ALONG", seated and worst < 25 and moved > 40, f"seated every second {seated}, worst {worst:.1f} m from the bus's root, bus moved {moved:.0f} m")
    finally:
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        for s_ in (h, L): s_("set TLDRevamp.Net.Entities.RearSeats True")
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
