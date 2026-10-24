#!/usr/bin/env python3
"""Mounted parts and network traffic (2026-10-24 playtest: "wheels, the engine and the radiator sending traffic while
mounted"). Two games: the host owns car H, the joiner owns car J (both Car05Full). Parts go the way players move them:
  the joiner takes a wheel, the engine and the radiator off H (the friend's car); the host takes another wheel off H
  (its own); the joiner takes the engine and the radiator off J (its own). Each is carried a moment and dropped.
  Fitted back: the joiner puts both wheels on H (one it took, one the host took), H's engine into J and J's engine
  into H; the host puts the radiators in (H's back into H, J's into J).
Then, for every one of those parts (now its own object on the network), states sent and received are counted on both
machines: parked, while both cars drive, the joiner leaving and joining again, H's owner changing, and another rejoin. Each
part must also sit in its slot on both screens.
  tools/mountedtraffic.py [--no-build] [--relay off|iad] [--old: Entities.ReplayPartAttaches False]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "off"), "--old" in sys.argv
T = "TLDRevamp.Net.Entities"; CAR05 = 154
HUB, ENGINE = "DisztarcsaSlotCol", "EngineSlotCollider"
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

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
def ent(s, net): return ents(s).get(net)

def travel(s, gx, gz, who, settle=6):
    s("set mainscript.s.player.godModeTillGrounded True")
    s(f"tpg {gx} {gz} hold")
    def grounded():
        q = s("gpos"); return "y" in s(f"ground {q['ux']} {q['uz']}")
    if not until(grounded, 90, 1.0): s("tprelease"); raise RuntimeError(f"{who}: no ground at {gx:.0f},{gz:.0f}")
    r = s(f"tpground {gx} {gz}")
    if r.get("tpground") == "hop": until(grounded, 30, 1.0); s(f"tpground {gx} {gz}")
    time.sleep(settle)

def slots(s, net):
    try: return s(f"mount slots {net}").get("slots") or []
    except RuntimeError: return None

def on(s, net, slot):
    rows = slots(s, net)
    return None if rows is None else sum(1 for r in rows if r[0] == slot and r[1] and r[2])

def as_player(s, cmd):
    s(f"set {T}.TestAsPlayer True")
    try: return s(cmd)
    except RuntimeError as ex: return {"error": str(ex)}
    finally: s(f"set {T}.TestAsPlayer False")

def drop(s):
    try: s("mp drop")
    except RuntimeError: pass

def new_ent(s, before, pred, timeout=12):
    return until(lambda: next((n for n, e in ents(s).items() if n not in before and pred(e)), None), timeout)

def take(s, who, car, slot, what):
    """the player takes the part in `slot` off `car` into its hands, carries it 2 s and drops it: its entity, or None"""
    before = set(ents(s))
    r = as_player(s, f"mount off {car} {slot}")
    part = new_ent(s, before, lambda e: not e["car"] and e["part"] >= 0, 8)
    print(f"  {who} takes {what} ({slot}) off {car}: {r} -> {part}", flush=True)
    time.sleep(2); drop(s); time.sleep(2)
    return part

def take_wheel(s, who, car, skip=()):
    """a hubcap off, then the first wheel slot whose wheel comes off (one under a hubcap stays): (wheel, slot)"""
    take(s, who, car, HUB, "a hubcap")
    for w in [r[0] for r in slots(s, car) if "WheelSlot" in r[0] and r[1] and r[0] not in skip]:
        part = take(s, who, car, w, "a wheel")
        if part: return part, w
    return None, None

def fit(s, who, part, car, slot, what):
    drop(s); time.sleep(1)
    r = as_player(s, f"mount on {part} {car} {slot}")
    print(f"  {who} fits {what} {part} into {car} {slot}: {r}", flush=True)
    got = until(lambda: on(h, car, slot) == 1 and on(L, car, slot) == 1, 15)
    verdict(f"{who.upper()} FITTED {what.upper()} INTO {car} — IN ON BOTH", bool(got), f"{slot} on {car}: host {on(h, car, slot)}, joiner {on(L, car, slot)}")

def in_slots(label, fitted):
    bad = []
    for part, car, slot, what in fitted:
        for who, s in (("host", h), ("joiner", L)):
            try: p = s(f"mount pose {part} {car} {slot}")
            except RuntimeError as ex: p = {"error": str(ex)}
            if not p.get("inSlot"):
                bad.append((who, what, part, p.get("dist"), p.get("error")))
                # what that slot holds here, and every object of that part's name: a second copy of it in the slot?
                e_ = ent(s, part) or {}
                print(f"    {who} {what} {part}: {e_.get('name')} owner {e_.get('owner')} attached {e_.get('attached')} "
                      f"parent {e_.get('parent')} part {e_.get('part')}; slot {[r for r in slots(s, car) or [] if r[0] == slot]}; "
                      f"same name {[(n, x['part'], x.get('attached')) for n, x in ents(s).items() if x['name'] == e_.get('name')]}", flush=True)
    verdict(f"{label}: EVERY FITTED PART SITS IN ITS SLOT ON BOTH", not bad, f"{len(fitted)} parts; not in slot {bad}")

def traffic(label, fitted, secs):
    nets = [f[0] for f in fitted]
    a = {"host": ents(h), "joiner": ents(L)}
    time.sleep(secs)
    b = {"host": ents(h), "joiner": ents(L)}
    rows, missing = [], []
    for who in a:
        for part, car, slot, what in fitted:
            if part not in a[who] or part not in b[who]: missing.append((who, what, part)); continue
            x, y = a[who][part], b[who][part]
            rows.append((who, what, part, y["owner"], y["seqOut"] - x["seqOut"], y["recv"] - x["recv"], y.get("attached")))
    busy = [r for r in rows if r[4] > 2 or r[5] > 2]
    print(f"  {label} per part (who, part, net, owner, sent, received, attached): {rows}", flush=True)
    verdict(f"{label}: NO FITTED PART SENDS OR GETS STATES IN {secs} S", not busy and not missing, f"busy {busy}; missing {missing}")

def rejoin(label, fitted):
    L("mp stop"); time.sleep(4)
    if not join_both(h, L, rehost=False): verdict(f"{label}: THE JOINER JOINED AGAIN", False); return
    L("set kaposztaleves.s.settings.god True")
    until(lambda: all(ent(L, f[0]) for f in fitted), 40); time.sleep(6)
    traffic(label, fitted, 10)
    in_slots(label, fitted)

def drive(s, car, who):
    c = ent(s, car)["pos"]; travel(s, c[0] - 3, c[2] - 3, who, settle=2)
    c = ent(s, car)["pos"]; o = s(f"groundg {c[0]} {c[2]}")["origin"]   # unity = global + this machine's origin
    print(f"  {who} drives {car}:", s(f"carmove {c[0] + o[0]} {c[1] + o[1] + 0.3} {c[2] + o[2]} 0 0 6"), flush=True)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    SAVED = {}
    for n_, s_ in (("h", h), ("L", L)):
        s_("set TLDRevamp.TestSafety.BlockAutoSave True")
        SAVED[n_] = s_("get kaposztaleves.s.settings.god").get("value")
        s_("set kaposztaleves.s.settings.god True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("JOINER NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True"); L("set kaposztaleves.s.settings.god True")
        for s_ in (h, L):
            try: s_(f"set {T}.ReplayPartAttaches {not OLD}")
            except RuntimeError as ex: print(f"  no switch ReplayPartAttaches in this build: {ex}", flush=True)
        print(f"  {'OLD (ReplayPartAttaches off)' if OLD else 'CURRENT BUILD'}", flush=True)
        hid, lid = h("mp entities")["me"], L("mp entities")["me"]
        time.sleep(6)
        q = h("gpos"); gx, gz = q["gx"] + 8, q["gz"] + 8
        hb = set(ents(h))
        print("  car H:", h(f"spawncarg {CAR05} 0 {gx} {gz} 0"), flush=True)
        H = new_ent(h, hb, lambda e: e["car"] and e["owner"] == hid, 20)
        travel(L, gx - 6, gz - 3, "joiner")
        lb = set(ents(L))
        print("  car J:", L(f"spawncarg {CAR05} 0 {gx - 10} {gz} 0"), flush=True)
        J = new_ent(L, lb, lambda e: e["car"] and e["owner"] == lid, 20)
        if not H or not J: sys.exit(f"cars not shared (H {H}, J {J})")
        until(lambda: ent(L, H) and not ent(L, H)["dormant"] and ent(h, J) and not ent(h, J)["dormant"], 30)
        time.sleep(4)
        COOL = next((r[0] for r in slots(h, H) if "oolant" in r[0] or "adiator" in r[0]), None)
        print(f"  H {H} (host {hid}), J {J} (joiner {lid}); radiator slot {COOL}", flush=True)

        # off: the joiner from the friend's car, the host from its own, the joiner from its own
        w1, ws1 = take_wheel(L, "joiner", H)
        e1 = take(L, "joiner", H, ENGINE, "the engine")
        r1 = take(L, "joiner", H, COOL, "the radiator") if COOL else None
        w2, ws2 = take_wheel(h, "host", H, skip=(ws1,))
        e2 = take(L, "joiner", J, ENGINE, "the engine")
        r2 = take(L, "joiner", J, COOL, "the radiator") if COOL else None
        got = {"wheel 1": w1, "wheel 2": w2, "H engine": e1, "J engine": e2, "H radiator": r1, "J radiator": r2}
        verdict("ALL SIX PARTS CAME OFF AS THEIR OWN OBJECTS", all(got.values()), str(got))

        # on: by the one who took it off, by the other player, onto the friend's car
        fitted = []
        for s_, who, part, car, slot, what in ((L, "joiner", w1, H, ws1, "wheel 1"), (L, "joiner", w2, H, ws2, "wheel 2"),
                                               (h, "host", e1, J, ENGINE, "H's engine"), (L, "joiner", e2, H, ENGINE, "J's engine"),
                                               (h, "host", r1, H, COOL, "H's radiator"), (h, "host", r2, J, COOL, "J's radiator")):
            if not part or not slot: continue
            fit(s_, who, part, car, slot, what)
            fitted.append((part, car, slot, what))
        time.sleep(4)
        in_slots("FITTED", fitted)
        traffic("PARKED", fitted, 15)
        drive(h, H, "host"); drive(L, J, "joiner")
        traffic("DRIVING", fitted, 8)
        time.sleep(4)
        in_slots("AFTER DRIVING", fitted)
        rejoin("AFTER A REJOIN", fitted)
        print("  H's owner changes (the joiner claims it):", L(f"mp claim {H}"), flush=True)
        until(lambda: ent(h, H)["owner"] == lid, 10); time.sleep(3)
        traffic("AFTER H'S OWNER CHANGED", fitted, 10)
        rejoin("AFTER THE OWNER CHANGE AND A REJOIN", fitted)
        er = [s_("errors")["errors"] for s_ in (h, L)]
        verdict("NO ERRORS", er == [0, 0], str(er))
    finally:
        for n_, s_ in (("h", h), ("L", L)):
            try: s_(f"set kaposztaleves.s.settings.god {SAVED[n_]}")
            except Exception: pass
        try: L("mp stop"); h("mp stop")
        except Exception: pass
passed = sum(1 for _, ok in VERDICTS if ok)
print(f"\nSUMMARY mountedtraffic {'OLD' if OLD else 'NEW'}: {passed}/{len(VERDICTS)} passed", flush=True)
sys.exit(0 if passed == len(VERDICTS) else 1)
