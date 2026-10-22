#!/usr/bin/env python3
"""A real player's save in multiplayer (2026-10-22 bug hunt: the owner's DevTest save "Playing" had many issues in play).
The host loads the save (a copy of it in the tests' own prefix: TLD_PLAYSAVE_DIR holds the TLDsave*_<name>.* set), the
laptop joins over the relay; then the cars around the player, the nearest first:
  J  JOINED: every car near the player is on both screens, at the same place, with the same parts (`mp carparts`)
  I  ITEMS: the player who doesn't own the car (the laptop: the host loaded the save) takes items out of each car and
     puts one back on a seat → out of / in the car on both, the same spot; the host drives off with it → still inside
  P  PARTS: the laptop (not the owner) takes a door off and bolts it back, a wheel and the hood off → the host's copy matches
  D  DRIVEN: the laptop drives each car a stretch and stops → the host sees it where the laptop stopped, with its parts,
     upright and on the ground; on the first car the host (now not the owner) takes items and parts; then the host
     drives it on (the car changes owner both ways)
  R  REJOIN: the laptop leaves and joins again → every car of J again the same on both
  E  no errors on either machine; the anomaly counters (`mp anomalies`) reported
  tools/playsave.py [--no-build] [--relay iad|off] [--save Playing] [--cars 4] [--radius 150]"""
import sys, os, time, math, shutil, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
from mplib import Agent, ensure_both, join_both, relay_both
from roadkit import g

os.environ.setdefault("TLD_TEST_MODS", "CarTrailer")   # the owner's copy has the trailer mod: the save's trailers load
def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, SAVE, NCARS, RADIUS = arg("--relay", "iad"), arg("--save", "Playing"), arg("--cars", 4, int), arg("--radius", 150, float)
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
def parts(s, car):
    c = s(f"mp carparts {car}")
    return None if c.get("dormant") or "error" in c else c

def same_car(car, label, tol=1.0):
    """the car on both machines: placed, the same parts, within tol m"""
    eh, el = ents(h).get(car), ents(L).get(car)
    if not eh or not el or eh.get("dormant") or el.get("dormant"):
        verdict(label, False, f"host {'-' if not eh else 'dormant' if eh.get('dormant') else 'here'}, laptop {'-' if not el else 'dormant' if el.get('dormant') else 'here'}")
        return False
    ph, pl = parts(h, car), parts(L, car)
    gap = math.dist(eh["pos"], el["pos"])
    if not ph or not pl: verdict(label, False, f"parts host {bool(ph)} laptop {bool(pl)}"); return False
    missing, extra = sorted(set(ph["parts"]) - set(pl["parts"])), sorted(set(pl["parts"]) - set(ph["parts"]))
    ok = gap < tol and not missing and not extra
    verdict(label, ok, f"{eh.get('name', '?')} gap {gap:.2f} m, parts {len(ph['parts'])} vs {len(pl['parts'])}: missing on the laptop {missing[:6]}, extra there {extra[:6]}")
    return ok

def sit(s, car):
    for _ in range(4):
        s(f"mp sitdriver {car}"); time.sleep(1.5)
        if g(s, "mainscript.s.player.Bsitting") is True: return True
    return False

def drive(s, who, secs=12, kmh=25):
    s("fuelup"); s("set settingsscript.s.S.BAutoShift True"); s("handbrake off")
    s(f"autodrive on {kmh}"); t0, vmax = time.time(), 0
    while time.time() - t0 < secs: vmax = max(vmax, speed(s)); time.sleep(0.5)
    s("autodrive on 0"); until(lambda: speed(s) < 3, 20, 0.5); s("autodrive off"); s("brakecar"); s("handbrake on"); time.sleep(2)
    return vmax

def upright(s, car):
    e = ents(s)[car]; gr = s(f"ground {e['pos'][0]} {e['pos'][2]}")
    up = s(f"mp carsig {car}").get("up")
    return gr.get("y"), e["pos"][1], up

def hand(s):
    for f in ("pickedUp", "inHandP"):
        v = g(s, f"mainscript.s.player.{f}.name")
        if isinstance(v, str) and v not in ("", "null"): return f, v
    return None, None

def let_go(s):
    """drop what the player holds, the way it is held (carried, in the hand, or picked into the inventory)"""
    f, v = hand(s)
    if f is None:   # picked into the inventory: switch away and back to bring it into the hand
        s0 = int(g(s, "mainscript.s.player.selectedInv") or 0); n = int(g(s, "mainscript.s.player.inventory.Count") or 1)
        s(f"call mainscript.s.player.InvSwitchTo {(s0 + 1) % n} False"); time.sleep(0.8); s(f"call mainscript.s.player.InvSwitchTo {s0} False"); time.sleep(1)
        f, v = hand(s)
    if f == "inHandP": r = s("call mainscript.s.player.RightHandRelease True")
    else:
        try: r = s("dropheld")
        except Exception as ex: r = repr(ex)
    return f, v, r

def phase(name, fn):
    print(f"\n=== {name}", flush=True)
    try: fn()
    except Exception as ex: verdict(f"{name}: RAN TO THE END", False, repr(ex)[:300])

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
        h("mp host 27070"); time.sleep(5)   # the server's entities from the save
        cars = sorted(((math.hypot(e["pos"][0] - me["gx"], e["pos"][2] - me["gz"]), n, e.get("name", "?"))
                       for n, e in ents(h).items() if e["car"] and not e.get("dormant")), key=lambda t: t[0])
        print("  cars on the host:", [(n, nm, round(d)) for d, n, nm in cars], flush=True)
        near = [n for d, n, _ in cars if d < RADIUS]
        drivable = []
        for n in near:
            p = parts(h, n); print(f"   car {n}: {len(p['parts']) if p else '-'} parts, wheels {len(p['wheels']) if p else '-'}", flush=True)
            if p and len(p["wheels"]) >= 4 and any("otor" in x or "ngine" in x for x in map(str, p["parts"])): drivable.append(n)
        print("  drivable (4 wheels, an engine):", drivable, flush=True)
        print("  host anomalies after the load:", h("mp anomalies"), flush=True)
        h("mp stop")
        if RELAY != "off": relay_both(h, L, RELAY)
        L("set TLDRevamp.Net.Entities.HitTrace 6")   # what makes the joiner hit things at the join (the log, read at the end)
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True"); s_("set kaposztaleves.s.settings.god True")
        hid, lid = h("mp entities")["me"], L("mp entities")["me"]
        L(f"tpground {me['gx'] + 3} {me['gz'] + 3}"); time.sleep(12)

        def hits(tag):
            """hits on things the other player owns, nobody hitting anything: the joiner's game sending them on its own"""
            a, b, c = h("mp aistats"), L("mp aistats"), h("mp hitcheck")
            print(f"  [{tag}] laptop break hits sent {b.get('breakHitsSent')}, host break hits run {a.get('breakHitsRun')}; host's check: "
                  f"objects checked {c.get('objChecked')}, allowed {c.get('objAllowed')}, refused {c.get('objRefused')}", flush=True)
            return b.get("breakHitsSent") or 0, a.get("breakHitsRun") or 0
        def joined():
            sent, run = hits("after the join")
            verdict("J NOBODY HIT ANYTHING: THE LAPTOP SENT NO BREAK HITS AT THE JOIN", sent == 0, f"sent {sent}, the host ran {run}")
            for n in near: same_car(n, f"J {n} THE SAME ON BOTH AFTER THE JOIN")
        phase("JOINED", joined)

        def inside(r): return abs(r[0]) < 1.4 and -0.8 < r[1] < 2.2 and abs(r[2]) < 3.2   # in the car's box (car frame, m)
        def cargo(s, car):
            """loose items in the car's box on this machine: net → car-frame position"""
            es = ents(s); c = es[car]["pos"]
            cand = [n for n, e in es.items() if not e["car"] and e["part"] < 0 and not e.get("dormant") and not e.get("stored")
                    and math.dist(e["pos"], c) < 4 and "Ragdol" not in e.get("name", "")]
            if not cand: return {}
            r = s(f"mp relpos {car} {' '.join(map(str, cand))}")["items"]
            return {int(k): v for k, v in r.items() if inside(v)}
        def near_car(s, car, d=2.5):
            e = ents(s)[car]["pos"]; s(f"tpground {e[0] + d} {e[2] + d}"); time.sleep(2.5)

        def take_out(breaker, owner, bname, car, tag):
            """the player who doesn't own the car takes items out of it and puts one back in"""
            oid = ents(owner)[car]["owner"]
            box = cargo(owner, car)
            print(f"  [{tag}] car {car} ({ents(owner)[car].get('name')}) owner {oid}; in it on the owner: {len(box)} items "
                  f"{[(n, ents(owner)[n].get('name')) for n in list(box)[:6]]}; on the {bname}: {len(cargo(breaker, car))}", flush=True)
            if not box: print(f"  [{tag}] nothing inside", flush=True); return
            near_car(breaker, car)
            for it in list(box)[:2]:
                nm = ents(owner)[it].get("name")
                print(f"  [{tag}] the {bname} takes {nm} ({it}) out:", breaker(f"mp pickupnet {it}"), flush=True)
                for k in range(5):
                    time.sleep(0.4); eo, eb = ents(owner).get(it, {}), ents(breaker).get(it, {})
                    print(f"     +{0.4 * (k + 1):.1f} s: {bname} holds {hand(breaker)}; item owner {eo.get('owner')}/{eb.get('owner')} held {eo.get('held')}/{eb.get('held')} "
                          f"stored {eo.get('stored')}/{eb.get('stored')}", flush=True)
                p = breaker("gpos"); breaker(f"tpground {p['gx'] + 4} {p['gz'] + 4}"); time.sleep(1.5)
                print(f"  [{tag}] drops it:", let_go(breaker), flush=True); time.sleep(5)
                eo, eb = ents(owner).get(it), ents(breaker).get(it)
                gap = math.dist(eo["pos"], eb["pos"]) if eo and eb else None
                still = it in cargo(owner, car)
                verdict(f"{tag} {it} {nm} TAKEN OUT BY THE {bname.upper()}: OUT OF THE CAR ON BOTH, SAME PLACE",
                        gap is not None and gap < 0.5 and not still and it not in cargo(breaker, car),
                        f"gap {gap and round(gap, 2)} m, still in the car on the owner {still}")
                near_car(breaker, car)
            # one back in, on a seat
            it = list(box)[0]
            print(f"  [{tag}] the {bname} picks it up again:", breaker(f"mp pickupnet {it}"), flush=True); time.sleep(1.5)
            # still in hand (the breaker's while held): onto a seat — out of reach, the game lets go of it there. Dropped
            # beside the car first, the car's owner takes it at once (CargoNear) and it isn't the breaker's to move any more.
            print(f"     holding {hand(breaker)}, item owner {ents(owner)[it]['owner']}/{ents(breaker)[it]['owner']}", flush=True)
            print(f"  [{tag}] puts it on a seat:", breaker(f"mp putincar {car} {it} 1"), flush=True); time.sleep(1); let_go(breaker); time.sleep(5)
            print(f"     after: item owner {ents(owner)[it]['owner']}/{ents(breaker)[it]['owner']}", flush=True)
            ro = owner(f"mp relpos {car} {it}")["items"].get(str(it)); rb = breaker(f"mp relpos {car} {it}")["items"].get(str(it))
            verdict(f"{tag} {it} PUT BACK IN BY THE {bname.upper()}: IN THE CAR ON BOTH, SAME SPOT",
                    ro and rb and inside(ro) and inside(rb) and math.dist(ro, rb) < 0.4, f"owner {ro}, {bname} {rb}")
            return it

        def items():
            moved = [it for n in near[:NCARS] for it in [take_out(L, h, "laptop", n, f"I {n}")] if it]
            # the owner drives off with what the laptop put in: still inside on the laptop's screen
            if not drivable: print("  no drivable car for the cargo ride", flush=True); return
            n = next((c for c in drivable if cargo(h, c)), drivable[0])
            if not cargo(h, n) and moved:
                near_car(L, n)
                print(f"  the laptop puts {moved[0]} into car {n}:", L(f"mp pickupnet {moved[0]}"), let_go(L), flush=True); time.sleep(1.5)
                print("   ", L(f"mp putincar {n} {moved[0]} 1"), flush=True); time.sleep(5)
            box = cargo(h, n)
            near_car(h, n, -2.5)
            if not sit(h, n): verdict(f"I {n} THE HOST SITS IN IT", False); return
            L(f"tpground {ents(h)[n]['pos'][0] + 5} {ents(h)[n]['pos'][2] + 5}")
            v = drive(h, "host", 10, 25); time.sleep(4)
            bl = cargo(L, n)
            verdict(f"I {n} THE HOST DRIVES OFF: THE CARGO STILL IN IT ON THE LAPTOP",
                    set(box) <= set(bl), f"top {v:.0f} km/h; in it on the host {sorted(box)}, on the laptop {sorted(bl)}")
            h("driveout"); time.sleep(2)
        phase("ITEMS (THE NON-OWNER TAKES THEM)", items)

        def owner_of(car): return h if ents(h)[car]["owner"] == hid else L
        def parts_change(breaker, bname, car, tag):
            breaker("set TLDRevamp.Net.Entities.TestAsPlayer True")   # the wrench as the player's own (E-press), not the game's
            try: _parts_change(breaker, bname, car, tag)
            finally: breaker("set TLDRevamp.Net.Entities.TestAsPlayer False")
        def detach_counts(owner_, breaker):
            a, b = owner_("mp entities")["attach"], breaker("mp entities")["attach"]
            return f"{bname_of(breaker)} requests sent {b.get('detachReqsSent')}, owner applied {a.get('detachReqsApplied')}"
        def bname_of(s_): return "laptop" if s_ is L else "host"
        def _parts_change(breaker, bname, car, tag):
            near_car(breaker, car)
            print(f"  [{tag}] before: {detach_counts(owner_of(car), breaker)}; {bname} at {breaker('gpos')}, car at {ents(owner_of(car))[car]['pos']}", flush=True)
            n0 = len(parts(owner_of(car), car)["parts"])
            ps = breaker(f"mp attachedparts {car}")["parts"]
            door = next((p for p in ps if "Door" in p["name"]), None)
            if door:
                before = set(ents(breaker))
                print(f"  [{tag}] the {bname} wrenches a door off:", breaker(f"mp wrench {car} {door['i']}"), flush=True)
                new = until(lambda: [k for k, v in ents(breaker).items() if k not in before and "Door" in v.get("name", "")], 10) or []
                time.sleep(2)
                if new: print(f"  [{tag}] bolts it back:", breaker(f"mp attachto {new[0]} {car} -1"), flush=True)
                time.sleep(3)
            n0 = len(parts(owner_of(car), car)["parts"]); want = 0   # the door is back on: count from here
            wheel = next((p for p in breaker(f"mp attachedparts {car}")["parts"] if "Felni" in p["name"] or p["name"].startswith("Wheel")), None)
            want += bool(wheel)
            if wheel: print(f"  [{tag}] wrenches a wheel off:", breaker(f"mp wrench {car} {wheel['i']}"), flush=True); time.sleep(3)
            hood = next((p for p in breaker(f"mp attachedparts {car}")["parts"] if "Motorhaz" in p["name"] or "Hood" in p["name"]), None)
            if hood: print(f"  [{tag}] wrenches the hood off:", breaker(f"mp wrench {car} {hood['i']}"), flush=True); want += 1
            time.sleep(6)
            print(f"  [{tag}] after: {detach_counts(owner_of(car), breaker)}", flush=True)
            n1 = len(parts(owner_of(car), car)["parts"])
            verdict(f"{tag} THE {bname.upper()}'S WRENCH TOOK THE WHEEL AND HOOD OFF (NOT ITS CAR)", n1 <= n0 - want,
                    f"the owner's copy: {n0} parts before, {n1} after ({want} wrenched; a wheel takes its tyre and hubcap)")
            same_car(car, f"{tag} AFTER THE {bname.upper()}'S PART CHANGES (NOT ITS CAR), THE SAME PARTS ON BOTH")

        def partsoff():
            n = near[1] if len(near) > 1 else near[0]
            parts_change(L, "laptop", n, f"P {n}")
        phase("PARTS (THE NON-OWNER CHANGES THEM)", partsoff)

        def driven():
            for n in drivable[:NCARS]:
                nm = ents(h)[n].get("name", "?")
                e0 = ents(h)[n]["pos"]
                near_car(L, n)
                if not sit(L, n): verdict(f"D {n} THE LAPTOP SITS IN THE {nm}", False); continue
                took = until(lambda: ents(h)[n]["owner"] == lid, 10)
                v = drive(L, "laptop")
                el = ents(L)[n]["pos"]; moved = math.dist(e0, el)
                time.sleep(3)
                gap = math.dist(ents(h)[n]["pos"], ents(L)[n]["pos"])
                gy, cy, up = upright(h, n)
                verdict(f"D {n} THE LAPTOP DRIVES THE {nm}: THE HOST SEES IT STOP THERE",
                        bool(took) and gap < 1.0, f"owner to the laptop {bool(took)}, top {v:.0f} km/h, moved {moved:.1f} m, gap {gap:.2f} m")
                verdict(f"D {n} ON ITS WHEELS ON THE HOST", gy is None or cy > gy - 0.5, f"car y {cy:.2f} ground {gy}, up {up}")
                same_car(n, f"D {n} ITS PARTS ON BOTH AFTER THE DRIVE")
                L("driveout"); time.sleep(2)
                if n == drivable[0]:
                    # the laptop's car now: the host (not the owner) takes its items and parts
                    L(f"tpground {el[0] + 8} {el[2] + 8}"); time.sleep(2)
                    take_out(h, L, "host", n, f"D {n} HOST")
                    parts_change(h, "host", n, f"D {n} HOST")
                L(f"tpground {el[0] + 6} {el[2] + 6}")
                near_car(h, n, -2.5)
                if not sit(h, n): verdict(f"D {n} THE HOST SITS IN IT", False); continue
                took = until(lambda: ents(L)[n]["owner"] == hid, 10)
                v = drive(h, "host", 8, 20)
                time.sleep(3)
                gap = math.dist(ents(h)[n]["pos"], ents(L)[n]["pos"])
                verdict(f"D {n} THE HOST DRIVES IT ON: THE LAPTOP SEES IT STOP THERE", bool(took) and gap < 1.0,
                        f"owner to the host {bool(took)}, top {v:.0f} km/h, gap {gap:.2f} m")
                same_car(n, f"D {n} ITS PARTS ON BOTH AFTER THE HOST'S DRIVE")
                h("driveout"); time.sleep(2)
        phase("DRIVEN (OWNERSHIP BOTH WAYS)", driven)

        def rejoin():
            L("mp stop"); time.sleep(5)
            if not join_both(h, L, rehost=False): verdict("R THE LAPTOP JOINS AGAIN", False); return
            L("set TLDRevamp.TestSafety.BlockAutoSave True"); L("set kaposztaleves.s.settings.god True")
            p = h("gpos"); L(f"tpground {p['gx'] + 3} {p['gz'] + 3}"); time.sleep(15)
            sent, run = hits("after the rejoin")
            verdict("R NOBODY HIT ANYTHING: THE LAPTOP SENT NO BREAK HITS AT THE REJOIN (STANDING AT THE HOST)", sent == 0, f"sent {sent} since its join, the host ran {run}")
            for n in near: same_car(n, f"R {n} THE SAME ON BOTH AFTER THE REJOIN")
        phase("REJOIN", rejoin)

        an = h("mp anomalies"); print("\n  anomalies: host", an, "laptop", L("mp anomalies"), flush=True)
        import re
        objs = sorted({int(x) for l in an.get("lines", []) for x in re.findall(r"of object (\d+)", l)})
        if objs:
            eh, el = ents(h), ents(L)
            for o in objs[:6]:
                a, b = eh.get(o, {}), el.get(o, {})
                print(f"   object {o}: {a.get('name')} owner {a.get('owner')}, host at {a.get('pos')} dormant {a.get('dormant')}; laptop at {b.get('pos')} dormant {b.get('dormant')} proxy {b.get('proxy')}", flush=True)
            print("   laptop player at", L("gpos"), "host player at", h("gpos"), flush=True)
        import subprocess, tldenv
        from mplib import REMOTE_HOST
        r = subprocess.run(tldenv.ssh_args(REMOTE_HOST) + [f'grep -A12 "\\[hittrace\\]" "{tldenv.REMOTE_LOG}" | head -90'], capture_output=True, text=True)
        print("  the laptop's forwarded hits (trace):\n" + "\n".join("    " + l[:220] for l in r.stdout.splitlines()), flush=True)
        er = [s_("errors") for s_ in (h, L)]
        verdict("E NO ERRORS", all(x_["errors"] == 0 and x_["exceptions"] == 0 for x_ in er), str([(x_["errors"], x_["exceptions"], (x_.get("kinds") or [])[:4]) for x_ in er]))
    finally:
        for s_ in ("h", "L"):
            try: globals()[s_]("autodrive off")
            except Exception: pass
        try: L("mp stop"); h("mp stop")
        except Exception: pass
        if RELAY != "off":
            for s_ in ("h", "L"):
                try: globals()[s_]("net relay off")
                except Exception: pass
        L.close()
        try: game.quit_game()   # the save's world is not the next test's: quit before the copies go
        except Exception: pass
        for f in copied: f.unlink(missing_ok=True)
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
print(f"PLAYSAVE: {sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} PASS")
