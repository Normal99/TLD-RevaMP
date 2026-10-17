#!/usr/bin/env python3
"""Two buildings close together, leased to the CLIENT moments apart (the first play session with a friend, v0.64.3:
two pois 35 m apart near the start, both granted to the client; the second grant restarted the single capture window,
items of the first were never shared - the host saw fewer - and its LeaseDone never came).
Two machines over the relay: desktop hosts, laptop joins.
  1 the laptop hops out until two unvisited buildings lie within PAIR_M of each other, then arrives there (approach)
  2 both leases must be spawned AND done by the laptop (lease table on the host)
  3 the host arrives; every item near the pair on the laptop must be on the host too (model + position)
  4 inventory (same session): the laptop picks up an item there and stores it; the host's copy must hide (stored,
    model off) - not float in the middle of the laptop's player - and show again when the laptop takes it out
  tools/leasepair.py [--no-build] [--relay iad|off]"""
import sys, os, time, math, json, collections, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
PAIR_M, R = 80.0, 110.0
VERDICTS = []

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def travel(s, gx, gz, who, settle=8, approach=False):
    """farworld.travel: held at the spot until the ground exists, then onto it; approach = 600 m short first"""
    if approach: travel(s, gx + 600, gz, who + " (approach)", settle=8)
    s("set mainscript.s.player.godModeTillGrounded True")
    s(f"tpg {gx} {gz} hold")
    def grounded():
        g = s("gpos"); return "y" in s(f"ground {g['ux']} {g['uz']}")
    ok = until(grounded, 90, 1.0)
    if not ok: s("tprelease")
    else:
        r = s(f"tpground {gx} {gz}")
        if r.get("tpground") == "hop": until(grounded, 30, 1.0); s(f"tpground {gx} {gz}")
    time.sleep(settle)
    g = s("gpos")
    print(f"  {who} at global {g['gx']:.0f},{g['gz']:.0f}", flush=True)
    if not ok: raise RuntimeError(f"{who}: no ground at {gx:.0f},{gz:.0f}")

def view(s, cx, cz):
    # alive things move on their own: bats fly (leasepair run 1: all 18 differences were RandomBat), ragdolls fall
    return [it for it in s(f"mp sitestate {cx} {cz} {R}")["items"] if "Ragdol" not in it[5] and "RandomBat" not in it[5]]

# --site gx gz [--seed S]: one given building instead of a searched pair (the play session's van: seed 141641351,
# building 245,620 - the client took its lease, the host arrived later); the host waits 3 km away until then
SITE = [float(x) for x in sys.argv[sys.argv.index("--site") + 1:sys.argv.index("--site") + 3]] if "--site" in sys.argv else None
if "--seed" in sys.argv: os.environ["TLD_SEED"] = sys.argv[sys.argv.index("--seed") + 1]
import seeded; seeded.SEED = int(os.environ.get("TLD_SEED", seeded.SEED))
ensure_both("--no-build" not in sys.argv)
L = Agent()
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    # god mode on both: fresh buildings bring creatures (run 2: the host was killed by a zombie at the pair - INVALID)
    gods = {}
    for name, s_ in (("host", h), ("laptop", L)):
        gods[name] = (s_, s_("get kaposztaleves.s.settings.god").get("value"))
        s_("set kaposztaleves.s.settings.god True")
    print("god mode on both (was", {k: v[1] for k, v in gods.items()}, ")", flush=True)
    ai = {x[0] for x in h("mp aiids")["ai"]}

    # ---- 1: a fresh pair of buildings
    pair = None
    if SITE:
        hl = [r for r in h("leasetable")["leases"] if math.dist([float(v) for v in r["key"].split(",")], SITE) < 60]
        print(f"  site {SITE} (seed {seeded.SEED}); lease rows near it at the start: {hl or 'none'}", flush=True)
        travel(h, SITE[0] + 3000, SITE[1], "host (out of the way)", settle=3)
        pair = ({"gx": SITE[0], "gz": SITE[1], "name": "site"}, {"gx": SITE[0], "gz": SITE[1], "name": "site"})
    for n in range(0 if SITE else 14):
        g = L("gpos")
        fresh = [w for w in L(f"pois {g['ux']} {g['uz']} 3000")["pois"] if not w["items"] and not w["fixed"]]
        pairs = [(a, b) for i, a in enumerate(fresh) for b in fresh[i + 1:] if math.dist((a["gx"], a["gz"]), (b["gx"], b["gz"])) < PAIR_M]
        print(f"  search {n}: at {g['gx']:.0f},{g['gz']:.0f}: fresh {len(fresh)}, close pairs {len(pairs)}", flush=True)
        if pairs:
            pair = min(pairs, key=lambda p: p[0]["d"])
            break
        travel(L, g["gx"] + 2500, g["gz"], f"laptop (search hop {n + 1})", settle=3)
    if pair is None: sys.exit("no close pair of unvisited buildings found")
    a, b = pair
    cx, cz = (a["gx"] + b["gx"]) / 2, (a["gz"] + b["gz"]) / 2
    print(f"  pair: {a['name']} + {b['name']}, {math.dist((a['gx'], a['gz']), (b['gx'], b['gz'])):.0f} m apart, centre {cx:.0f},{cz:.0f}", flush=True)
    l0 = L(f"mp itemsnear {cx} {cz} 50")["leases"]
    travel(L, cx + 8, cz + 8, "laptop (pair)", settle=4, approach=True)

    # ---- 2: both leases spawned and done by the laptop
    until(lambda: L(f"mp itemsnear {cx} {cz} 50")["leases"]["done"] >= l0["done"] + (1 if SITE else 2), 40)
    l1 = L(f"mp itemsnear {cx} {cz} 50")["leases"]
    me = L("mp entities")["me"]
    table = h("leasetable")["leases"]
    near = [r for r in table if r["holder"] == me]
    print(f"  laptop lease counters: before {l0}, after {l1}; host table rows held by the laptop: {near}", flush=True)
    need = 1 if SITE else 2
    verdict("2 BOTH LEASES SPAWNED AND DONE BY THE CLIENT", l1["spawns"] - l0["spawns"] >= need and l1["done"] - l0["done"] >= need and
            len(near) >= need and all(r["done"] for r in near), f"spawns +{l1['spawns'] - l0['spawns']}, done +{l1['done'] - l0['done']}")
    time.sleep(8)
    # what keeps moving on the laptop (its own objects, still sending states) - drift1: a turd sent 749 states, ~37 s
    # of motion, while in single player every turd at this station is still 2 s after it spawns
    def owner_snap():
        return {e["net"]: (e["seqOut"], e["pos"], e["name"], e["why"]) for e in L("mp entities")["entities"]
                if not e["proxy"] and math.dist(e["pos"][::2], (cx, cz)) < R}
    def movers(tag, a_, b_):
        mv = [(b_[n][0] - a_[n][0], b_[n][2], n, a_[n][1], b_[n][1], [y - x for x, y in zip(a_[n][3], b_[n][3])]) for n in b_ if n in a_ and b_[n][0] != a_[n][0]]
        why = [sum(m[5][i] for m in mv) for i in range(4)]
        print(f"  laptop objects moving {tag}: {len(mv)}; send reasons awake/pos>1mm/rot>0.1deg/stored: {why}", flush=True)
        for k, name, n, p0, p1, w in sorted(mv, reverse=True)[:8]:
            print(f"    {name} net {n}: {k} states (why {w}), {math.dist(p0, p1):.2f} m, now {[round(x, 2) for x in p1]}", flush=True)
    m0 = owner_snap(); time.sleep(5); m1 = owner_snap(); movers("before the host comes (5 s)", m0, m1)
    # the game's physics lock on the laptop: in single player the station's planks go into the shelf (Szelveny) and
    # things onto pallets (Raklap) ~3 s after spawn and everything sleeps (lockprobe: 4 restless, all vehicles/dolls);
    # here 22-25 objects never rest - are they stored at all?
    pl_ = L(f"mp physlocks {cx} {cz} {R}")["items"]
    held = collections.Counter(f"{r[7]} {r[5]}" for r in pl_ if r[5])
    print(f"  laptop physics lock: {sum(held.values())} items held: {dict(held)}; rb: {dict(collections.Counter(r[11] for r in pl_))}", flush=True)
    mvn = {n for n in m1 if n in m0 and m1[n][0] != m0[n][0]}
    print("  kinematic/awake bodies:", [(r[1], r[11], "copy" if r[10] else "own", r[8], r[5] or "loose") for r in pl_ if r[11] in ("kin", "awake")][:30], flush=True)
    def awake_rows(tag):
        a_ = L(f"mp awake {cx} {cz} {R}")
        rows = a_["awake"]
        print(f"  laptop awake bodies {tag}: {len(rows)} (asleep {a_['asleep']})", flush=True)
        for nm, n, px, v, w, th, t_ in sorted(rows, key=lambda r_: -r_[3])[:14]:
            print(f"    {nm:<24} net {n:<4} {'COPY' if px else 'own '} v {v:.4f} w {w:.4f} thr {th:.3f} touching {t_}", flush=True)
        return rows
    awake_rows("now")
    print("  sleepall:", L(f"mp sleepall {cx} {cz} {R}"), flush=True)
    time.sleep(1); awake_rows("1 s after sleepall")
    time.sleep(5); awake_rows("6 s after sleepall")
    print("  restless objects and their lock:", [(r[1], r[5] or "loose", r[11]) for r in pl_ if r[8] in mvn and r[9] == 0][:25], flush=True)
    ref = [it for it in view(L, cx, cz) if it[0] not in ai]

    # ---- 3: the host arrives
    travel(h, cx - 8 + 600, cz - 8, "host (pair) (approach)", settle=8)
    m2 = owner_snap(); movers("while the host was 600 m out", m1, m2)
    travel(h, cx - 8, cz - 8, "host (pair)", settle=15)
    m3 = owner_snap(); movers("while the host arrived", m2, m3)
    time.sleep(10); m4 = owner_snap(); movers("10 s after", m3, m4)
    stale = ref
    ref = [it for it in view(L, cx, cz) if it[0] not in ai]   # now, not from before the host travelled (drift1)
    moved = [it for it in ref if not any(o[0] == it[0] and math.dist(o[1:4], it[1:4]) < 0.3 for o in stale)]
    print(f"  laptop items that moved since the first look: {len(moved)}", [(it[5], [round(x, 1) for x in it[1:4]]) for it in moved[:8]], flush=True)
    got = [it for it in view(h, cx, cz) if it[0] not in ai]
    left = list(got); missing = []
    for it in ref:
        m = [o for o in left if o[0] == it[0] and math.dist(it[1:4], o[1:4]) < 1.0]
        if m: left.remove(min(m, key=lambda o: math.dist(it[1:4], o[1:4])))
        else: missing.append(it)
    def twin(it, others):   # the same model nearest to it on the other machine (what it should have matched)
        c = [o for o in others if o[5] == it[5]]
        return min(c, key=lambda o: math.dist(it[1:4], o[1:4])) if c else None
    for it in missing[:12]:
        print("    only on the laptop:", it[5], [round(x, 1) for x in it[1:4]], L(f"mp itement {it[1]} {it[2]} {it[3]}"), flush=True)
        o = twin(it, got)
        if o: print(f"      nearest {o[5]} on the host {math.dist(it[1:4], o[1:4]):.2f} m:", h(f"mp itement {o[1]} {o[2]} {o[3]}"), flush=True)
    for it in left[:12]:
        print("    only on the host:  ", it[5], [round(x, 1) for x in it[1:4]], h(f"mp itement {it[1]} {it[2]} {it[3]}"), flush=True)
        o = twin(it, ref)
        if o: print(f"      nearest {o[5]} on the laptop {math.dist(it[1:4], o[1:4]):.2f} m:", L(f"mp itement {o[1]} {o[2]} {o[3]}"), flush=True)
    verdict("3 THE HOST SEES WHAT THE CLIENT SPAWNED", not missing and not left,
            f"laptop {len(ref)} items, host {len(got)}: only on the laptop {len(missing)}, only on the host {len(left)}")

    # ---- 3c: loose objects off on the host copy - where does the difference come from? (the play session: a ball 20 m
    # off, a Linzer 1 m; leasepair: a turd 1.4 m). Per object: the owner's place, the last state it sent and whether it
    # sent its rest state; the copy's place, how many states it got and the last one's place.
    import math
    le = {e["net"]: e for e in L("mp entities")["entities"]}; he = {e["net"]: e for e in h("mp entities")["entities"]}
    off = []
    for n, a_ in le.items():
        b_ = he.get(n)
        if not b_ or a_["proxy"] or not b_["proxy"] or a_["car"] or a_["part"] >= 0: continue
        if math.dist(a_["pos"][::2], (cx, cz)) > R: continue
        d = math.dist(a_["pos"], b_["pos"])
        if d > 0.3: off.append((d, a_, b_))
    for d, a_, b_ in sorted(off, key=lambda t: -t[0])[:12]:
        print(f"    {a_['name']} net {a_['net']}: {d:.2f} m off | owner at {a_['pos']} sent {a_['seqOut']} last {a_['lastSent']} rest-sent {a_['sentRest']}"
              f" | copy at {b_['pos']} got {b_['recv']} last {b_['lastIn']} dormant {b_['dormant']} physical {b_['physical']}", flush=True)
    verdict("3c LOOSE OBJECTS: THE HOST COPY WHERE THE OWNER HAS IT (< 0.3 m)", not off, f"{len(off)} off")

    # ---- 3b: cars part by part (the van "attached but in the wrong direction" on the host - its copy of a car the
    # client's building spawned); then vans the laptop spawns itself
    def qang(a, b):
        import math as m
        def q(e):
            x, y, z = (m.radians(v) for v in e)
            cx_, sx = m.cos(x / 2), m.sin(x / 2); cy, sy = m.cos(y / 2), m.sin(y / 2); cz_, sz = m.cos(z / 2), m.sin(z / 2)
            # Unity euler order: Z, X, Y (q = qy * qx * qz)
            return (cy * sx * cz_ + sy * cx_ * sz, sy * cx_ * cz_ - cy * sx * sz, cy * cx_ * sz - sy * sx * cz_, cy * cx_ * cz_ + sy * sx * sz)
        qa, qb = q(a), q(b)
        d = abs(sum(i * j for i, j in zip(qa, qb)))
        return m.degrees(2 * m.acos(min(1.0, d)))
    def cmp_parts(net, label):
        pl, ph = L(f"mp partposes {net}"), h(f"mp partposes {net}")
        if "error" in pl or "error" in ph: print(f"    {label} net {net}: laptop {pl.get('error', 'ok')}, host {ph.get('error', 'ok')}", flush=True); return None
        bad = []
        nm = lambda x: x.replace("(Clone)", "").strip()
        if len(pl["items"]) != len(ph["items"]): bad.append((f"GROUP SIZE laptop {len(pl['items'])} host {len(ph['items'])}", 0, 0, "-", "-", "", ""))
        for a_, b_ in zip(pl["items"], ph["items"]):
            # same index = the same part on both machines (detach / resync go by index): the names must match
            # by item id (tosave.id): built-in parts are named differently from their item (Car09full's
            # "Car09FrontDoor" is item 382 Car09Gril, "Wheel" is TireFelni) - names would cry wolf
            if len(a_) >= 11 and len(b_) >= 11 and a_[10] != b_[10] and a_[0] != 0: bad.append((f"INDEX {a_[0]}: laptop {a_[1]} (item {a_[10]}) host {b_[1]} (item {b_[10]})", 0, 0, "-", "-", "", "")); continue
            if len(a_) < 3 or len(b_) < 3: continue
            dp = math.dist(a_[2:5], b_[2:5]); da = qang(a_[5:8], b_[5:8])
            if dp > 0.05 or da > 3.0 or a_[8] != b_[8]: bad.append((a_[1], round(dp, 3), round(da, 1), a_[8], b_[8], a_[9], b_[9]))
        print(f"    {label} {pl['root']} net {net} ({len(pl['items'])} items, host copy proxy {ph['proxy']}): {len(bad)} parts differ", flush=True)
        for x in bad[:14]: print(f"      {x[0]}: {x[1]} m, {x[2]} deg, attached laptop {x[3]} host {x[4]}, parent laptop {x[5]!r} host {x[6]!r}", flush=True)
        return len(bad)
    cars = [e for e in L("mp entities")["entities"] if e["car"] and not e["dormant"] and math.dist((e["pos"][0], e["pos"][2]), (cx, cz)) < R]
    print(f"  cars at the pair: {len(cars)}", flush=True)
    site_bad = [cmp_parts(e["net"], "site car") for e in cars]
    vans_bad = []
    for vid in (223, 372, 338):   # Car06Full, Car09Full, Car10Full
        before_ = {e["net"] for e in L("mp entities")["entities"]}
        print(f"  laptop spawns {vid}:", L(f"devspawn {vid}"), flush=True); time.sleep(8)
        new = [e for e in L("mp entities")["entities"] if e["net"] not in before_ and e["car"]]
        for e in new:
            nb = cmp_parts(e["net"], "spawned"); vans_bad.append(nb)
            if nb:   # wheels off: the owner's suspension (physics, mesh) vs what the copy got and shows
                ee = {x["net"]: x for x in L("mp entities")["entities"]}.get(e["net"], {}); he_ = {x["net"]: x for x in h("mp entities")["entities"]}.get(e["net"], {})
                print(f"      car states: owner sent {ee.get('seqOut')} rest-sent {ee.get('sentRest')} vel {ee.get('vel')} | copy got {he_.get('recv')}", flush=True)
                for who, s_ in (("laptop", L), ("host", h)):
                    for c in s_("wheelinfo 40")["cars"]:
                        if c["net"] == e["net"]:
                            print(f"      {who}: got {c['got']} wheels " + " ".join(f"{w[0]}(phys {w[6]:.2f} mesh {w[7]:.2f} gnd {w[4]})" for w in c["wheels"]), flush=True)
    verdict("3b CARS: EVERY PART WHERE IT IS ON THE OWNER (pose relative to the car)",
            all(b == 0 for b in site_bad + vans_bad) and len(site_bad + vans_bad) > 0,
            f"site cars {site_bad}, spawned {vans_bad}")

    # ---- 4: inventory - a katana (the item the play session's log shows going into an inventory), spawned by the laptop
    def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
    before = set(ents(L))
    print("  katana:", L("devspawn 63"), flush=True)
    until(lambda: any("katana" in e["name"] for n, e in ents(L).items() if n not in before), 15)
    net = [n for n, e in ents(L).items() if n not in before and "katana" in e["name"]][0]
    until(lambda: net in ents(h), 15)
    print("  pick up:", L(f"mp pickupnet {net}"), flush=True); time.sleep(2)
    print("  held 2 s later:", L("get mainscript.s.player.pickedUp").get("value"), flush=True)
    def both(tag):
        lr, hr = ents(L).get(net, {}), ents(h).get(net, {})
        st = (lr.get("stored"), hr.get("stored"), hr.get("shown"))
        print(f"  {tag}: laptop stored {st[0]} | host copy stored {st[1]} shown {st[2]}", flush=True)
        return st
    seen_hidden = seen_back = False
    for n, slot in enumerate((1, 0, 1, 0)):
        L(f"call mainscript.s.player.InvSwitchTo {slot}"); time.sleep(3)
        ls, hs, hshown = both(f"inventory slot {slot}")
        if not ls:   # why didn't the laptop store it? (3 --site runs in a row: never stored; runs 3, 5 did)
            pl = "mainscript.s.player"
            print("    laptop player:", {k: L(f"get {pl}.{k}").get("value") for k in ("selectedInv", "pickedUp", "inHandP", "Bpickedup")},
                  "inventory", L(f"get {pl}.inventory").get("value"), flush=True)
        if ls and hs and hshown is False: seen_hidden = True
        elif seen_hidden and ls is False and hs is False and hshown is True: seen_back = True
    verdict("4 A STORED ITEM IS HIDDEN ON THE OTHER MACHINE, SHOWN WHEN TAKEN OUT", seen_hidden and seen_back,
            f"hidden while stored {seen_hidden}, shown again {seen_back}")

    for name, (s_, v) in gods.items():
        try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
        except Exception as ex: print("god restore", name, ex)
    L("mp stop"); h("mp stop")
    if RELAY != "off": h("net relay off"); L("net relay off")
L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
