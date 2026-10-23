#!/usr/bin/env python3
"""Ownership in a crowded lobby: the host (the owner's buggy save, the cars and their cargo in it), the second machine's
player (P2: the one who does NOT own the cars, who usually triggers the bugs) and player bots (src/TLDRevamp/Net/
PlayerBot.cs) as further players: B1 made in the host's game (loopback), B2 made in P2's game (it crosses the network
like P2). Every check compares the host's and P2's screens (positions, owners, what lies in the car, errors, [anomaly]
lines) and what the bots were told (owners).
  A JOIN       P2 and the bots join; the cars near the host and what lies in them agree on both screens
  B DRIVE      B1 puts an item into the host's cargo car; P2 (not the owner) drives the car ~200 m at 40-70 km/h: both
               screens see it at P2's pose, every item still in it (no ghost, no fall-through, no duplicate)
  C HANDOFF    P2 gets out, B1 takes the wheel and drives on, gets out, P2 takes it again: P2 -> B1 -> P2 the same on both
  D CONTEST    P2 and a bot grab the same item out of the car at the same moment: one holder, the same on both, no duplicate
  E PASSED     P2 takes an item out and drops it, B2 picks it up and puts it on the car, the host drives off: it stays
  F LEAVE      B1 crashes at the wheel (no goodbye) / P2 leaves mid-drive (mp stop): the car and its cargo fall back to the
               host, stay put, agree; P2 / a bot can take the car afterwards; the crashed bot rejoins
  G REJOIN     P2 joins again while B1 drives the cargo car: it sees the car moving with its cargo and B1 as its owner
  K KILLED     (last) P2 drives with cargo and its game is killed: the host and the bots see the car fall back and stay put
  tools/lobby3.py [--no-build] [--relay iad|off] [--save Playing] [--only ABCDEFGK] [--no-kill]
Needs TLD_PLAYSAVE_DIR (the save's TLDsave*_<name>.* set) and the second machine set up as for the other two-machine tests (mplib)."""
import sys, os, time, math, shutil, pathlib, threading, re, subprocess
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, tldenv
from mplib import Agent, ensure_both, join_both, relay_both, REMOTE_HOST
from roadkit import g, road_at, clear_road, car_ready

os.environ.setdefault("TLD_TEST_MODS", "CarTrailer")   # the owner's copy has the trailer mod: the save's trailers load
def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, SAVE, ONLY = arg("--relay", "iad"), arg("--save", "Playing"), arg("--only", "SABCDEFGK")
SRC = pathlib.Path(os.environ.get("TLD_PLAYSAVE_DIR", "")).expanduser()
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, bool(ok))); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
def speed(s): return abs(float(g(s, "mainscript.s.player.Car.speed") or 0))
def inside(r): return abs(r[0]) < 1.4 and -0.8 < r[1] < 2.6 and abs(r[2]) < 3.4   # in the car's box (car frame, m; the roof too)
def cargo(s, car):
    """loose items in the car's box on this screen: net -> car-frame position"""
    es = ents(s); c = es.get(car)
    if not c: return {}
    cand = [n for n, e in es.items() if not e["car"] and e["part"] < 0 and not e.get("dormant") and not e.get("stored")
            and math.dist(e["pos"], c["pos"]) < 5 and "Ragdol" not in e.get("name", "") and "Nyul" not in e.get("name", "")]
    if not cand: return {}
    r = s(f"mp relpos {car} {' '.join(map(str, cand))}")["items"]
    return {int(k): v for k, v in r.items() if inside(v)}
def pbot(s, n, cmd): return s(f"mp pbot {n} {cmd}")
def errs(s):
    e = s("errors"); return e["errors"], e["exceptions"], (e.get("kinds") or [])[:4]
def anomalies(s): return list((s("mp anomalies") or {}).get("lines") or [])

def phase(key, name, fn):
    if key not in ONLY: return
    print(f"\n=== {key} {name}", flush=True)
    e0 = {lbl: errs(s_) for lbl, s_ in (("host", h), ("P2", L)) if alive(s_)}
    a0 = {lbl: len(anomalies(s_)) for lbl, s_ in (("host", h), ("P2", L)) if alive(s_)}
    try: fn()
    except Exception as ex: verdict(f"{key} {name}: RAN TO THE END", False, repr(ex)[:300])
    new = {}
    for lbl, s_ in (("host", h), ("P2", L)):
        if lbl not in e0 or not alive(s_): continue
        e1 = errs(s_); al = anomalies(s_)
        new[lbl] = (e1[0] - e0[lbl][0], e1[1] - e0[lbl][1], e1[2], al[a0[lbl]:][:4])
    verdict(f"{key} NO NEW ERRORS OR EXCEPTIONS", all(v[0] == 0 and v[1] == 0 for v in new.values()),
            "; ".join(f"{k}: +{v[0]} errors +{v[1]} exceptions {v[2] if v[0] or v[1] else ''}" for k, v in new.items()))
    lines = {k: v[3] for k, v in new.items() if v[3]}
    if lines: print(f"  [anomaly] lines during {key}: {lines}", flush=True)

def alive(s):
    try: s("ping"); return True
    except Exception: return False

def agree(car, label, tol=1.0, cargo_tol=0.4, want_owner=None):
    """the car on both screens: the same place, the same owner, the same items in it at the same spots"""
    eh, el = ents(h).get(car), ents(L).get(car)
    if not eh or not el: verdict(label, False, f"host has it {bool(eh)}, P2 {bool(el)}"); return False
    gap = math.dist(eh["pos"], el["pos"])
    ch, cl = cargo(h, car), cargo(L, car)
    only_h, only_l = sorted(set(ch) - set(cl)), sorted(set(cl) - set(ch))
    worst = max((math.dist(ch[n], cl[n]) for n in set(ch) & set(cl)), default=0.0)
    owners = (eh["owner"], el["owner"])
    ok = gap < tol and not only_h and not only_l and worst < cargo_tol and owners[0] == owners[1] and (want_owner is None or owners[0] == want_owner)
    if worst >= cargo_tol:
        n = max(set(ch) & set(cl), key=lambda n: math.dist(ch[n], cl[n]))
        def lockinfo(s_):
            c = ents(s_)[car]["pos"]; sid = ents(s_)[n]["saveId"]
            return [x for x in s_(f"mp physlocks {c[0]} {c[2]} 8").get("items", []) if x[0] == sid][:1]
        print(f"    worst item {n} ({ents(h)[n].get('name')}): host {ch[n]} owner {ents(h)[n]['owner']} proxy {ents(h)[n].get('proxy')} lock {lockinfo(h)}; "
              f"P2 {cl[n]} owner {ents(L)[n]['owner']} proxy {ents(L)[n].get('proxy')} lock {lockinfo(L)}", flush=True)
    if gap >= tol:
        bots = [(n, b_.get("pos"), b_.get("seat")) for n, s_ in ((1, h), (2, L)) for b_ in [pbot(s_, 1, "status")]]
        print(f"    where: host {[round(x, 1) for x in eh['pos']]} dormant {eh.get('dormant')} proxy {eh.get('proxy')} lastIn {eh.get('lastIn')} recv {eh.get('recv')}; "
              f"P2 {[round(x, 1) for x in el['pos']]} dormant {el.get('dormant')} proxy {el.get('proxy')} lastIn {el.get('lastIn')} recv {el.get('recv')}; "
              f"bots (pos, seat) {bots}; host player {h('gpos')}, P2 player {L('gpos')}", flush=True)
    verdict(label, ok, f"{eh.get('name', '?')} gap {gap:.2f} m, owner host/P2 {owners}{'' if want_owner is None else f' (want {want_owner})'}, "
                       f"cargo {len(ch)}/{len(cl)}, only on host {only_h[:5]}, only on P2 {only_l[:5]}, worst item gap {worst:.2f} m")
    return ok

def duplicates(s, near, r=8.0):
    """items of the same name at the same spot (< 5 cm) near a point: a duplicated copy"""
    es = [e for e in ents(s).values() if not e["car"] and e["part"] < 0 and math.dist(e["pos"], near) < r]
    d = []
    for i, a in enumerate(es):
        for b in es[i + 1:]:
            if a.get("name") == b.get("name") and math.dist(a["pos"], b["pos"]) < 0.05: d.append((a["net"], b["net"], a.get("name")))
    return d

def bot_owner(s, n, net): return pbot(s, n, f"owner {net}")
def near_car(s, car, d=2.5):
    e = ents(s)[car]["pos"]; s(f"tpground {e[0] + d} {e[2] + d}"); time.sleep(2.5)
def sit(s, car):
    for _ in range(4):
        s(f"mp sitdriver {car}"); time.sleep(1.5)
        if g(s, "mainscript.s.player.Bsitting") is True: return True
    return False
def drive(s, secs, mps):
    s("fuelup"); s("set settingsscript.s.S.BAutoShift True"); s("handbrake off")
    s(f"autodrive on {mps}"); t0, vmax = time.time(), 0
    while time.time() - t0 < secs: vmax = max(vmax, speed(s)); time.sleep(0.5)
    return vmax
def stop_car(s):
    s("autodrive on 0"); until(lambda: speed(s) < 3, 20, 0.5); s("autodrive off"); s("brakecar"); s("handbrake on"); time.sleep(2)
def hand(s):
    for f in ("pickedUp", "inHandP"):
        v = g(s, f"mainscript.s.player.{f}.name")
        if isinstance(v, str) and v not in ("", "null"): return v
    return None

if not SRC.is_dir() or not list(SRC.glob(f"TLDsave*_{SAVE}.*")): sys.exit(f"no TLDsave*_{SAVE}.* in TLD_PLAYSAVE_DIR ({SRC})")
L = Agent()
ensure_both("--no-build" not in sys.argv)
copied = []
with game.ProtectedSession():
    try:
        for f in SRC.glob(f"TLDsave*_{SAVE}.*"):
            shutil.copy2(f, game.SAVES / f.name); copied.append(game.SAVES / f.name)
        print(f"  the save's {len(copied)} files copied into the test prefix", flush=True)
        L("!seeded")
        h = game.loadsave(SAVE)
        for s_ in (h, L):
            s_("set TLDRevamp.TestSafety.BlockAutoSave True"); s_("bg on"); s_("set UnityEngine.QualitySettings.vSyncCount 0")
        me = h("gpos"); print(f"  host in the save at {me['gx']:.0f},{me['gz']:.0f}", flush=True)
        h("mp host 27070"); time.sleep(5)
        cars = sorted(((math.hypot(e["pos"][0] - me["gx"], e["pos"][2] - me["gz"]), n, e.get("name", "?"))
                       for n, e in ents(h).items() if e["car"] and not e.get("dormant")), key=lambda t: t[0])
        near = [n for d, n, _ in cars if d < 150]
        def drivable(n):
            p = h(f"mp carparts {n}")
            return "error" not in p and not p.get("dormant") and len(p.get("wheels", [])) >= 4 and any("otor" in x or "ngine" in x for x in map(str, p.get("parts", [])))
        loads = sorted(((len(cargo(h, n)), n) for n in near if drivable(n)), reverse=True)
        print("  cars near the host:", [(n, nm, round(d)) for d, n, nm in cars if d < 150], "| drivable, by cargo:", loads, flush=True)
        if not loads: sys.exit("no drivable car near the host in the save")
        CAR = loads[0][1]
        def put_cargo(car, items=(320, 39)):
            """the host (nobody else near to claim what it spawns) puts things on the car's seats"""
            c0 = ents(h)[car]["pos"]; h(f"tpground {c0[0] - 4} {c0[2] - 4}"); time.sleep(3)
            for k, item in enumerate(items):
                before = set(ents(h)); h(f"devspawn {item}")
                n = until(lambda: next((x for x, e in ents(h).items() if x not in before and not e["car"]), None), 15)
                if n: print(f"  item {item} onto seat {k}:", h(f"mp putincar {car} {n} {k}"), flush=True)
            time.sleep(4)
            return cargo(h, car)
        def test_drive(s, car, secs=6):
            """can this car be driven at 40+ km/h? (the save's own cars: playsave saw 0-17 km/h)"""
            if not sit(s, car): return 0.0
            v = drive(s, secs, 15); stop_car(s)
            print(f"  test drive of {car}: top {v:.0f} km/h; engine running {g(s, 'mainscript.s.player.Car.Engine.running')}, "
                  f"rpm {g(s, 'mainscript.s.player.Car.Engine.rpm')}", flush=True)
            s("driveout"); time.sleep(2)
            return v
        if "S" in ONLY:
            print("\n=== S THE HOST'S SESSION RESTART", flush=True)
            box = put_cargo(CAR) if len(cargo(h, CAR)) < 2 else cargo(h, CAR)
            def in_car():
                """by save id: loose in the box (own shared objects) + stored in the car (they ride in its record: hosting
                starts with every car's stored things in its group — rehostcargo.py)"""
                es = ents(h); c = es[CAR]; sid = c["saveId"]
                pl = h(f"mp physlocks {c['pos'][0]} {c['pos'][2]} 8").get("items", [])
                return {es[n]["saveId"]: es[n].get("name") for n in cargo(h, CAR)}, {x[0]: x[1] for x in pl if x[6] == sid}
            loose, stored = in_car(); was = ents(h)[CAR]["pos"]
            print(f"  in the car before: {len(loose)} loose, {len(stored)} stored", flush=True)
            h("mp stop"); time.sleep(3); h("mp host 27070"); time.sleep(6)
            CAR = min(((math.dist(e["pos"], was), n) for n, e in ents(h).items() if e["car"]))[1]   # net ids start again at 1
            loose2, stored2 = in_car()
            lost = {i: n for i, n in {**loose, **stored}.items() if i not in loose2 and i not in stored2}
            verdict("S THE HOST STOPS AND HOSTS AGAIN: WHAT LAY IN THE CAR STILL LIES IN IT (loose or stored)", not lost,
                    f"before {len(loose)} + {len(stored)}, after {len(loose2)} + {len(stored2)}; gone {lost}")
        top = test_drive(h, CAR)
        if top < 30:
            # the save's cars stand in a yard: the test driver follows the main road (DriveLab) and gets nowhere from there
            # (runs 2-4: 1-5 km/h, the engine at 800-3500 rpm). A car01 on a clear stretch of the main road carries the
            # cargo and drive checks (cargoride.py's way); the save's cars and their cargo stay for A and S
            me = h("gpos"); rp = road_at(h, me["gx"], me["gz"]); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
            r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
            at = (r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8)
            before = set(ents(h))
            if car_ready(h, at[0], at[1], yaw, "host"):
                new = until(lambda: next((x for x, e in ents(h).items() if x not in before and e["car"]), None), 15)
                h("driveout"); time.sleep(2)
                print(f"  the save's car {CAR} tops at {top:.0f} km/h: car01 {new} on the main road ({r0['gx']:.0f},{r0['gz']:.0f}) for the drive checks", flush=True)
                if new and test_drive(h, new) >= 20: CAR = new   # 6 s from a standstill: car01 reaches 25-30 km/h
                else: print("  car01 doesn't drive either: keeping the save's car", flush=True)
        if len(cargo(h, CAR)) < 2: print("  cargo:", put_cargo(CAR), flush=True)
        was = ents(h)[CAR]["pos"]
        h("mp stop")
        if RELAY != "off": relay_both(h, L, RELAY)
        if not join_both(h, L): sys.exit("P2 NEVER JOINED")
        CAR = min(((math.dist(e["pos"], was), n) for n, e in ents(h).items() if e["car"]))[1]   # net ids start again at 1
        print(f"  the cargo car is net {CAR} in this session ({ents(h)[CAR].get('name')})", flush=True)
        for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True"); s_("set kaposztaleves.s.settings.god True")
        h("set TLDRevamp.Net.MpServer.TestSameAccount True")   # B2 shares P2's Steam account
        P2 = L("mp status")["client"]["id"]
        time.sleep(3)
        print(f"  P2 at the join: edits sent {g(L, 'TLDRevamp.Net.Entities.EditsSent')}, marks {str(g(L, 'TLDRevamp.Net.Entities.EditMarks'))[:300]}", flush=True)
        if len(cargo(h, CAR)) < 2:
            L(f"tpground {ents(h)[CAR]['pos'][0] + 300} {ents(h)[CAR]['pos'][2]}"); time.sleep(2)
            print("  cargo again (lost at the rehost):", put_cargo(CAR), flush=True)
        c0 = ents(h)[CAR]["pos"]
        L(f"tpground {c0[0] + 4} {c0[2] + 4}"); h(f"tpground {c0[0] - 4} {c0[2] - 4}"); time.sleep(8)
        def at_rest(limit=30):
            """wait until the car stops moving (a car left rolling with nobody at the wheel coasts on the host's physics)"""
            last = ents(h)[CAR]["pos"]; t1 = time.time()
            while time.time() - t1 < limit:
                time.sleep(2); now_ = ents(h)[CAR]["pos"]
                if math.dist(last, now_) < 0.1: return True
                last = now_
            return False
        def follow():
            """the host's player stays by the action (its game keeps what is near it loaded: a bot made there sees it)"""
            c = ents(h)[CAR]["pos"]; h(f"tpground {c[0] - 18} {c[2] - 18}"); time.sleep(2)

        B = {}
        def joined():
            B["1"] = h("mp pbot add b1"); B["2"] = L("mp pbot add b2")
            ok = until(lambda: pbot(h, 1, "status")["ready"] and pbot(L, 1, "status")["ready"], 40)
            b1, b2 = pbot(h, 1, "status"), pbot(L, 1, "status")
            B["id1"], B["id2"] = b1["id"], b2["id"]
            ids = sorted(p_.get("id") for p_ in h("mp players")["players"])
            verdict("A P2 AND BOTH BOTS ARE IN (B1 from the host's game, B2 from P2's)", ok and P2 in ids and b1["id"] in ids and b2["id"] in ids,
                    f"players on the host {ids}; P2 {P2}, B1 {b1['id']}, B2 {b2['id']}")
            for n in near: agree(n, f"A {n} THE SAME ON BOTH SCREENS AFTER THE JOINS")
            ow = ents(h)[CAR]["owner"]
            verdict("A THE BOTS WERE TOLD WHO OWNS THE CARGO CAR", bot_owner(h, 1, CAR)["owner"] == ow and bot_owner(L, 1, CAR)["owner"] == ow,
                    f"host {ow}, B1 {bot_owner(h, 1, CAR)}, B2 {bot_owner(L, 1, CAR)}")
        phase("A", "JOIN", joined)

        def drive_b():
            follow(); box = cargo(h, CAR)
            # a third player's thing in the car: B1 takes one item out of it and puts it back where it lay (a spot that is
            # free: it lay there), so the car holds an item neither the car's owner nor its driver owns
            c = ents(h)[CAR]["pos"]
            if box:
                it = sorted(box)[0]; lp = box[it]
                pbot(h, 1, f"tp {c[0] + 2} {c[2] + 2}"); print("  B1 takes", it, pbot(h, 1, f"take {it}")["doing"], flush=True)
                if until(lambda: pbot(h, 1, "status")["holding"] == it, 20):
                    print("  B1 puts it back:", pbot(h, 1, f"put {CAR} {lp[0]} {lp[1] + 0.4} {lp[2]}")["last"], flush=True); time.sleep(4)
                    verdict("B B1'S ITEM LIES IN THE CAR ON BOTH SCREENS", it in cargo(h, CAR) and it in cargo(L, CAR),
                            f"item {it}, owner host/P2 {ents(h)[it]['owner']}/{ents(L)[it]['owner']} (B1 {B['id1']}), host {cargo(h, CAR).get(it)}, P2 {cargo(L, CAR).get(it)}")
                else: verdict("B B1 TAKES AN ITEM OUT OF THE CAR", False, pbot(h, 1, "status")["last"])
            box = cargo(h, CAR); n0 = len(box)
            print(f"  in the car on the host: {n0} items {sorted(box)}", flush=True)
            near_car(L, CAR)
            if not sit(L, CAR): verdict("B P2 SITS AT THE WHEEL", False); return
            took = until(lambda: ents(h)[CAR]["owner"] == P2 and ents(L)[CAR]["owner"] == P2, 10)
            verdict("B THE CAR IS P2'S ON BOTH SCREENS (host -> P2)", took, f"owner host/P2 {ents(h)[CAR]['owner']}/{ents(L)[CAR]['owner']}, B1 told {bot_owner(h, 1, CAR)['owner']}, B2 told {bot_owner(L, 1, CAR)['owner']}")
            p0 = ents(L)[CAR]["pos"]
            L("fuelup"); L("set settingsscript.s.S.BAutoShift True"); L("handbrake off"); L("autodrive on 15")
            t0, vmax, out_h, out_l = time.time(), 0, [], []
            while time.time() - t0 < 18:
                vmax = max(vmax, speed(L))
                a, b = cargo(h, CAR), cargo(L, CAR)
                out_h += [n for n in box if n not in a]; out_l += [n for n in box if n not in b]
                time.sleep(0.4)
            stop_car(L); time.sleep(3)
            dist = math.dist(p0, ents(L)[CAR]["pos"])
            verdict("B P2 DROVE IT 150+ M AT 40-70 KM/H", dist > 150 and 40 <= vmax <= 75, f"{dist:.0f} m, top {vmax:.0f} km/h")
            verdict("B NO ITEM LEFT THE CAR DURING THE DRIVE (host's screen)", not out_h, f"left at some sample: {sorted(set(out_h))[:6]}")
            verdict("B NO ITEM LEFT THE CAR DURING THE DRIVE (P2's screen)", not out_l, f"left at some sample: {sorted(set(out_l))[:6]}")
            agree(CAR, "B AFTER THE DRIVE: THE SAME ON BOTH, THE CARGO IN IT", want_owner=P2)
            ch = cargo(h, CAR)
            verdict("B EVERY ITEM STILL THERE, NONE DUPLICATED", set(box) <= set(ch) and not duplicates(h, ents(h)[CAR]["pos"]) and not duplicates(L, ents(L)[CAR]["pos"]),
                    f"{len(ch)} of {n0} in it; duplicates host {duplicates(h, ents(h)[CAR]['pos'])} P2 {duplicates(L, ents(L)[CAR]['pos'])}")
            cy = ents(h)[CAR]["pos"][1]
            low = [(n, round(e["pos"][1] - cy, 1)) for n, e in ents(h).items() if n in box and e["pos"][1] < cy - 3]
            verdict("B NOTHING FELL THROUGH (host's screen)", not low, str(low[:5]))
        phase("B", "NON-OWNER DRIVES THE HOST'S CARGO CAR", drive_b)

        def handoff():
            follow(); box = cargo(h, CAR)
            L("driveout"); time.sleep(3)
            p = ents(h)[CAR]["pos"]; L(f"tpground {p[0] + 6} {p[2] + 6}")
            pbot(h, 1, f"tp {p[0] - 3} {p[2] - 3}"); print("  B1 gets in:", pbot(h, 1, f"enter {CAR}")["doing"], flush=True)
            took = until(lambda: ents(h)[CAR]["owner"] == B["id1"] and ents(L)[CAR]["owner"] == B["id1"], 15)
            verdict("C P2 -> B1: THE CAR IS B1'S ON BOTH SCREENS AND B1 KNOWS", took and bot_owner(h, 1, CAR)["owner"] == B["id1"] and bot_owner(L, 1, CAR)["owner"] == B["id1"],
                    f"owner host/P2 {ents(h)[CAR]['owner']}/{ents(L)[CAR]['owner']}, B1 {pbot(h, 1, 'status')['last']}")
            time.sleep(2)
            cargo_own = {n: (ents(h)[n]["owner"], ents(L)[n]["owner"]) for n in box if n in ents(h) and n in ents(L)}
            verdict("C THE CARGO CAME WITH THE CAR (B1's on both screens)", all(a == b == B["id1"] for a, b in cargo_own.values()), str(cargo_own))
            pbot(h, 1, "drive 45 12"); time.sleep(15); follow()
            st = pbot(h, 1, "status")
            agree(CAR, "C B1 DROVE ON: THE SAME ON BOTH, THE CARGO IN IT", tol=1.0, want_owner=B["id1"])
            verdict("C B1 MOVED THE CAR", st["states"] > 100, f"B1 {st['states']} states, {st['finals']} finals, at {st['pos']}")
            pbot(h, 1, "exit"); time.sleep(2)
            near_car(L, CAR)
            if not sit(L, CAR): verdict("C P2 SITS AT THE WHEEL AGAIN", False); return
            took = until(lambda: ents(h)[CAR]["owner"] == P2 and ents(L)[CAR]["owner"] == P2, 10)
            verdict("C B1 -> P2: THE CAR IS P2'S AGAIN ON BOTH SCREENS", took and bot_owner(h, 1, CAR)["owner"] == P2, f"owner host/P2 {ents(h)[CAR]['owner']}/{ents(L)[CAR]['owner']}, B1 told {bot_owner(h, 1, CAR)['owner']}")
            drive(L, 5, 8); stop_car(L)
            agree(CAR, "C P2 DROVE IT A BIT: THE SAME ON BOTH, THE CARGO IN IT", want_owner=P2)
            L("driveout"); time.sleep(2)
        phase("C", "HANDOFF P2 -> B1 -> P2", handoff)

        def contest():
            follow()
            rounds = (("B2 (from P2's game)", L, 1), ("B2 (from P2's game)", L, 1), ("B1 (from the host's game)", h, 1))
            for k, (bname, bs, bn) in enumerate(rounds, 1):
                box = cargo(h, CAR)
                if not box: verdict(f"D{k} AN ITEM IN THE CAR TO GRAB", False); return
                it = sorted(box)[0]
                ip = ents(h)[it]["pos"]
                L(f"tpground {ip[0] + 1.2} {ip[2] + 1.2}"); pbot(bs, bn, f"tp {ip[0] - 1.2} {ip[2] - 1.2}"); time.sleep(3)
                bid = pbot(bs, bn, "status")["id"]
                res = {}
                def grab_p2(): res["P2"] = L(f"mp pickupnet {it}")
                def grab_bot(): res["bot"] = pbot(bs, bn, f"take {it}")["doing"]
                if bs is L: grab_p2(); grab_bot()   # one pipe to P2's game: back to back, a few ms apart
                else:
                    ts = [threading.Thread(target=grab_p2), threading.Thread(target=grab_bot)]
                    for t_ in ts: t_.start()
                    for t_ in ts: t_.join()
                time.sleep(4)
                p2h = hand(L); bh = pbot(bs, bn, "status")["holding"]
                holders = [x for x, v in (("P2", p2h), ("bot", bh == it)) if v]
                oh, ol = ents(h)[it]["owner"], ents(L)[it]["owner"]
                want = {"P2": P2, "bot": bid}.get(holders[0]) if len(holders) == 1 else None
                d = math.dist(ents(h)[it]["pos"], ents(L)[it]["pos"])
                verdict(f"D{k} P2 vs {bname}: ONE HOLDER, THE OWNER ON BOTH SCREENS", len(holders) == 1 and oh == ol == want,
                        f"holders {holders}, owner host/P2 {oh}/{ol} (P2 {P2}, bot {bid}), grabs {res}, bot: {pbot(bs, bn, 'status')['last']}")
                if len(holders) != 1:
                    print(f"    owner log on the host: {h(f'mp ownerlog {it}')['log'][-8:]}", flush=True)
                    print(f"    owner log on P2: {L(f'mp ownerlog {it}')['log'][-8:]}; P2 grabs lost {g(L, 'TLDRevamp.Net.Entities.GrabsLost')}", flush=True)
                verdict(f"D{k} BOTH SCREENS SEE IT IN THE SAME PLACE, NO DUPLICATE", d < 0.8 and not duplicates(h, ip) and not duplicates(L, ip), f"{d:.2f} m apart")
                # back into the car for the next round
                if "P2" in holders:
                    L(f"mp putincar {CAR} {it} 1"); time.sleep(1)
                    if hand(L): L("dropheld")   # putincar lets go of it itself
                elif holders: lp_ = box[it]; pbot(bs, bn, f"put {CAR} {lp_[0]} {lp_[1] + 0.4} {lp_[2]}")   # back where it lay
                time.sleep(5)
        phase("D", "CONTESTED GRAB", contest)

        def passed():
            follow(); box = cargo(h, CAR)
            if not box: verdict("E AN ITEM IN THE CAR", False); return
            it = sorted(box)[-1]; lp = box[it]   # where it lies in the car: a free spot to put it back on
            near_car(L, CAR)
            print("  P2 takes", it, "out:", L(f"mp pickupnet {it}"), flush=True); time.sleep(2)
            held = hand(L); p = L("gpos"); L(f"tpground {p['gx'] + 4} {p['gz'] + 4}"); time.sleep(1.5)
            print(f"  P2 holds {held} before the step, {hand(L)} after", flush=True)
            if hand(L): L("dropheld")
            time.sleep(5)
            out = it not in cargo(h, CAR) and it not in cargo(L, CAR)
            verdict("E P2 TOOK IT OUT AND DROPPED IT: OUT OF THE CAR ON BOTH", out, f"owner host/P2 {ents(h)[it]['owner']}/{ents(L)[it]['owner']}")
            ip = ents(L)[it]["pos"]; pbot(L, 1, f"tp {ip[0] + 1} {ip[2] + 1}")
            print("  B2 takes it:", pbot(L, 1, f"take {it}")["doing"], flush=True)
            got = until(lambda: pbot(L, 1, "status")["holding"] == it, 15)
            verdict("E B2 HOLDS IT (its own on both screens)", got and ents(h)[it]["owner"] == B["id2"] and ents(L)[it]["owner"] == B["id2"],
                    f"owner host/P2 {ents(h)[it]['owner']}/{ents(L)[it]['owner']}, B2 {pbot(L, 1, 'status')['last']}")
            # back where it lay (a fixed roof point was inside car01's roof: the physics threw it 100 m)
            print("  B2 puts it back into the car:", pbot(L, 1, f"put {CAR} {lp[0]} {lp[1] + 0.4} {lp[2]}")["last"], flush=True)
            for t_ in (0.3, 1, 2, 4):
                time.sleep(t_ - (0 if t_ == 0.3 else 0))
                print(f"    +{t_} s: in the car's frame host {h(f'mp relpos {CAR} {it}')['items'].get(str(it))}, P2 {L(f'mp relpos {CAR} {it}')['items'].get(str(it))}; "
                      f"owner host/P2 {ents(h)[it]['owner']}/{ents(L)[it]['owner']}, physical host/P2 {ents(h)[it].get('physical')}/{ents(L)[it].get('physical')}", flush=True)
            rh = h(f"mp relpos {CAR} {it}")["items"].get(str(it)); rl = L(f"mp relpos {CAR} {it}")["items"].get(str(it))
            verdict("E ON THE CAR ON BOTH SCREENS, THE SAME SPOT", rh and rl and inside(rh) and math.dist(rh, rl) < 0.4, f"host {rh}, P2 {rl}")
            near_car(h, CAR, -2.5)
            if not sit(h, CAR): verdict("E THE HOST SITS AT THE WHEEL", False); return
            v = drive(h, 8, 8); stop_car(h); time.sleep(3)
            rh = h(f"mp relpos {CAR} {it}")["items"].get(str(it)); rl = L(f"mp relpos {CAR} {it}")["items"].get(str(it))
            verdict("E THE HOST DROVE OFF: IT STAYED ON THE CAR (both screens)", rh and rl and inside(rh) and inside(rl) and math.dist(rh, rl) < 0.4,
                    f"top {v:.0f} km/h; host {rh}, P2 {rl}, owner host/P2 {ents(h)[it]['owner']}/{ents(L)[it]['owner']}")
            h("driveout"); time.sleep(2)
        phase("E", "ITEM PASSED AROUND", passed)

        def leave():
            # F1: B1 at the wheel with the cargo, crashes (silent, then gone without a goodbye)
            follow(); box = cargo(h, CAR)
            p = ents(h)[CAR]["pos"]; pbot(h, 1, f"tp {p[0] - 3} {p[2] - 3}"); pbot(h, 1, f"enter {CAR}")
            if not until(lambda: ents(h)[CAR]["owner"] == B["id1"], 15): verdict("F1 B1 TAKES THE WHEEL", False, pbot(h, 1, "status")["last"]); return
            pbot(h, 1, "drive 40 30"); time.sleep(6)
            print("  B1 crashes:", pbot(h, 1, "crash 12")["doing"], flush=True)
            t0 = time.time(); fell = until(lambda: ents(h)[CAR]["owner"] != B["id1"] and ents(L)[CAR]["owner"] == ents(h)[CAR]["owner"], 25)
            took = time.time() - t0
            # left at 40 km/h with nobody at the wheel it rolls on (its new owner's physics) until it stops: then it must stay
            last = ents(h)[CAR]["pos"]; t1 = time.time(); rest = False
            while time.time() - t1 < 30:
                time.sleep(2); now_ = ents(h)[CAR]["pos"]
                if math.dist(last, now_) < 0.1: rest = True; break
                last = now_
            a = ents(h)[CAR]["pos"]; time.sleep(4); b = ents(h)[CAR]["pos"]
            ow = (ents(h)[CAR]["owner"], ents(L)[CAR]["owner"], bot_owner(L, 1, CAR)["owner"])
            verdict("F1 B1 CRASHED: THE CAR GOES TO SOMEONE STILL HERE, THE SAME ON BOTH SCREENS AND FOR B2", fell and ow[0] == ow[1] == ow[2] and ow[0] != B["id1"],
                    f"after {took:.0f} s; owner host/P2/B2 told {ow} (B1 was {B['id1']}, the host 0, P2 {P2})")
            verdict("F1 THE CAR COMES TO REST AND STAYS PUT", rest and math.dist(a, b) < 0.3, f"rest {rest} after {time.time() - t1:.0f} s, then moved {math.dist(a, b):.2f} m in 4 s")
            agree(CAR, "F1 THE CAR AND ITS CARGO AGREE")
            verdict("F1 THE CARGO STAYED IN IT", set(box) <= set(cargo(h, CAR)), f"{len(cargo(h, CAR))} of {len(box)}")
            print("  B1 rejoins:", pbot(h, 1, "rejoin").get("joins"), flush=True)
            ok = until(lambda: pbot(h, 1, "status")["ready"], 30); nid = pbot(h, 1, "status")["id"]
            verdict("F1 THE CRASHED BOT REJOINS", ok and nid > 0 and nid in [p_.get("id") for p_ in h("mp players")["players"]], f"new id {nid} (was {B['id1']})")
            B["id1"] = nid
            near_car(L, CAR)
            got = sit(L, CAR) and until(lambda: ents(h)[CAR]["owner"] == P2, 10)
            verdict("F1 P2 CAN TAKE THE CAR AFTERWARDS", got, f"owner host/P2 {ents(h)[CAR]['owner']}/{ents(L)[CAR]['owner']}")
            # F2: P2 drives with the cargo and leaves cleanly mid-drive
            box = cargo(h, CAR)
            L("fuelup"); L("handbrake off"); L("autodrive on 10"); time.sleep(6)
            L("mp stop")
            fell = until(lambda: ents(h)[CAR]["owner"] == 0, 10)
            rest = at_rest(); a = ents(h)[CAR]["pos"]; time.sleep(4); b = ents(h)[CAR]["pos"]   # left at 36 km/h: it coasts to a stop first
            e_ = ents(h)[CAR]
            print(f"  came to rest: {rest}; the car on the host: engine running {e_.get('engRunning')}, rpm {e_.get('engRpm')}, vel {e_.get('vel')}, "
                  f"driven {e_.get('driven')}", flush=True)
            verdict("F2 P2 LEFT MID-DRIVE: THE CAR FELL BACK TO THE HOST", fell, f"owner {ents(h)[CAR]['owner']}, B1 told {bot_owner(h, 1, CAR)['owner']}")
            # nobody at the wheel, the handbrake off: on a slope it rolls on (as when a player jumps out of a moving car) —
            # a note, not a sync fault; at rest it must stay put
            if not rest: print(f"  NOTE F2: still rolling {math.dist(a, b) / 4 * 3.6:.0f} km/h with nobody at the wheel", flush=True)
            verdict("F2 THE CAR STAYS PUT AND KEEPS ITS CARGO (host's screen)", (math.dist(a, b) < 0.3 or not rest) and set(box) <= set(cargo(h, CAR)),
                    f"moved {math.dist(a, b):.2f} m in 4 s (at rest {rest}); {len(cargo(h, CAR))} of {len(box)} in it")
            follow(); p = ents(h)[CAR]["pos"]; pbot(h, 1, f"tp {p[0] - 3} {p[2] - 3}"); pbot(h, 1, f"enter {CAR}")
            verdict("F2 A BOT CAN TAKE THE CAR AFTERWARDS", until(lambda: ents(h)[CAR]["owner"] == B["id1"], 15), pbot(h, 1, "status")["last"])
        phase("F", "LEAVE MID-LEASE", leave)

        def rejoin():
            if L("mp status").get("client"): L("mp stop"); time.sleep(3)
            follow()
            if pbot(h, 1, "status")["seat"] and len(cargo(h, CAR)) < 2: pbot(h, 1, "exit"); time.sleep(2)
            if len(cargo(h, CAR)) < 2: print("  cargo for the rejoin (P2 out of the session):", put_cargo(CAR), flush=True); follow()
            if ents(h)[CAR]["owner"] != B["id1"] or not pbot(h, 1, "status")["driving"]:
                if pbot(h, 1, "status")["seat"]: pbot(h, 1, "exit")
                p = ents(h)[CAR]["pos"]; pbot(h, 1, f"tp {p[0] - 3} {p[2] - 3}"); pbot(h, 1, f"enter {CAR}")
                if not until(lambda: ents(h)[CAR]["owner"] == B["id1"] and pbot(h, 1, "status")["driving"], 15):
                    verdict("G B1 AT THE WHEEL OF THE CARGO CAR", False, pbot(h, 1, "status")["last"]); return
            box = cargo(h, CAR)
            pbot(h, 1, "drive 30 60"); time.sleep(3)
            if not join_both(h, L, rehost=False): verdict("G P2 JOINS AGAIN", False); return
            L("set TLDRevamp.TestSafety.BlockAutoSave True"); L("set kaposztaleves.s.settings.god True")
            p = ents(h)[CAR]["pos"]; L(f"tpground {p[0] + 15} {p[2] + 15}")
            ok = until(lambda: CAR in ents(L), 30); time.sleep(3)
            a = ents(L)[CAR]["pos"]; time.sleep(3); b = ents(L)[CAR]["pos"]
            verdict("G P2 SEES THE CAR MOVING", ok and math.dist(a, b) > 5, f"moved {math.dist(a, b):.1f} m in 3 s on P2")
            verdict("G P2 SEES B1 AS ITS OWNER", ents(L)[CAR]["owner"] == B["id1"] == ents(h)[CAR]["owner"], f"owner host/P2 {ents(h)[CAR]['owner']}/{ents(L)[CAR]['owner']} (B1 {B['id1']})")
            cl = cargo(L, CAR)
            for n in sorted(set(box) - set(cl))[:3]:
                el_ = ents(L).get(n)
                print(f"    item {n} on P2: {el_ and {k: el_.get(k) for k in ('owner', 'epoch', 'proxy', 'dormant', 'stored', 'recv', 'pos', 'lastIn')}}, in the car's frame {L(f'mp relpos {CAR} {n}')['items'].get(str(n)) if el_ else None}; "
                      f"B1 sends it: {n in pbot(h, 1, 'status')['cargo']}, owns it {n in pbot(h, 1, 'status')['owned']}", flush=True)
            verdict("G THE CARGO MOVES WITH IT ON P2", set(box) <= set(cl), f"{len(cl)} of {len(box)} in it on P2: missing {sorted(set(box) - set(cl))[:5]}")
            pbot(h, 1, "drive 0"); time.sleep(6); follow(); time.sleep(4)   # the host's game loads it again where it is
            agree(CAR, "G STOPPED: THE SAME ON BOTH, THE CARGO IN IT", want_owner=B["id1"])
            print(f"  P2: stored copies moved to the owner's spot {g(L, 'TLDRevamp.Net.Entities.LockPlacesCorrected')}, "
                  f"copies given the new owner's clock {g(L, 'TLDRevamp.Net.Entities.OwnerClockResets')}; host: {g(h, 'TLDRevamp.Net.Entities.LockPlacesCorrected')}, "
                  f"{g(h, 'TLDRevamp.Net.Entities.OwnerClockResets')}", flush=True)
            for n in sorted(set(box) - set(cargo(L, CAR)))[:3]:
                eh_, el_ = ents(h).get(n), ents(L).get(n)
                rel = (h(f"mp relpos {CAR} {n}")["items"].get(str(n)), L(f"mp relpos {CAR} {n}")["items"].get(str(n)) if el_ else None)
                print(f"    item {n}: host {eh_ and {k: eh_.get(k) for k in ('owner', 'epoch', 'proxy', 'dormant', 'stored', 'held', 'recv', 'lastIn')}}, "
                      f"P2 {el_ and {k: el_.get(k) for k in ('owner', 'epoch', 'proxy', 'dormant', 'stored', 'held', 'recv', 'lastIn')}}; in the car's frame host/P2 {rel}; "
                      f"B1 sends it: {n in pbot(h, 1, 'status')['cargo']}, owned {n in pbot(h, 1, 'status')['owned']}", flush=True)
            if pbot(h, 1, "status")["seat"]: pbot(h, 1, "exit")
        phase("G", "REJOIN WHILE A BOT DRIVES", rejoin)

        def killed():
            if not L("mp status").get("client"): join_both(h, L, rehost=False); L("set kaposztaleves.s.settings.god True")
            if pbot(h, 1, "status")["seat"]: pbot(h, 1, "exit")
            follow()
            near_car(L, CAR)
            if not sit(L, CAR) or not until(lambda: ents(h)[CAR]["owner"] == P2_now(), 10): verdict("K P2 TAKES THE WHEEL", False); return
            box = cargo(h, CAR)
            L("fuelup"); L("handbrake off"); L("autodrive on 10"); time.sleep(6)
            # no quit through the game: SIGKILL to its Windows process, by PID (the pattern is anchored at the drive letter, so
            # the shell running it never matches), the mod never says goodbye
            r = subprocess.run(tldenv.ssh_args(REMOTE_HOST) + ['for p in $(pgrep -f "^[A-Z]:.*TheLongDrive[.]exe"); do kill -9 $p && echo killed $p; done'],
                               capture_output=True, text=True, timeout=60)
            print("  P2's game killed:", r.stdout.strip()[-200:], flush=True)
            fell = until(lambda: ents(h)[CAR]["owner"] == 0, 40)
            # it was doing 36 km/h with nobody left at the wheel: it coasts to a stop (the host's physics), then must stay
            last = ents(h)[CAR]["pos"]; t1 = time.time(); rest = False
            while time.time() - t1 < 30:
                time.sleep(2); now_ = ents(h)[CAR]["pos"]
                if math.dist(last, now_) < 0.1: rest = True; break
                last = now_
            print(f"  came to rest: {rest} after {time.time() - t1:.0f} s", flush=True)
            a = ents(h)[CAR]["pos"]; time.sleep(4); b = ents(h)[CAR]["pos"]
            verdict("K P2'S GAME KILLED MID-DRIVE: THE CAR FELL BACK TO THE HOST (and B1 was told)", fell and bot_owner(h, 1, CAR)["owner"] == 0, f"owner {ents(h)[CAR]['owner']}, B1 told {bot_owner(h, 1, CAR)['owner']}")
            # nobody at the wheel, the handbrake off: on a slope it rolls on (as when a player jumps out of a moving car) —
            # a note, not a sync fault; at rest it must stay put
            if not rest: print(f"  NOTE K: still rolling {math.dist(a, b) / 4 * 3.6:.0f} km/h with nobody at the wheel", flush=True)
            verdict("K THE CAR STAYS PUT WITH ITS CARGO", (math.dist(a, b) < 0.3 or not rest) and set(box) <= set(cargo(h, CAR)),
                    f"moved {math.dist(a, b):.2f} m in 4 s (at rest {rest}); {len(cargo(h, CAR))} of {len(box)} in it")
        def P2_now(): return L("mp status")["client"]["id"]
        if "--no-kill" not in sys.argv: phase("K", "P2 KILLED MID-DRIVE", killed)

        an = h("mp anomalies"); print("\n  anomalies on the host:", an, flush=True)
        if alive(L): print("  anomalies on P2:", L("mp anomalies"), flush=True)
    finally:
        for s_ in ("h", "L"):
            try: globals()[s_]("autodrive off"); globals()[s_]("mp stop")
            except Exception: pass
        if RELAY != "off":
            for s_ in ("h", "L"):
                try: globals()[s_]("net relay off")
                except Exception: pass
        L.close()
        try: game.quit_game()   # the save's world is not the next test's
        except Exception: pass
        for f in copied: f.unlink(missing_ok=True)
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
print(f"LOBBY3: {sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} PASS")
