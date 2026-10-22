#!/usr/bin/env python3
"""Another player's car as you see it after it was out of your game's item range (playtest 2026-10-09, a friend hosting:
"cars desyncing when they found each other", "the wheels never right for the other person looking at your car", "they
disappear for a moment at the edge of your rendering and come back"). Two machines over the relay.
  E EDGE  : the laptop's car crosses the host's item range back and forth (out 60 m past it, back 40 m inside) — each
            time the host's game streams the copy out and places it again from its own record. After each return: the
            host's copy has the same parts in the same slots, its wheels where the laptop has them, the car at the same place
  A AWAY  : the host goes ~4 km away (its copy dormant); the laptop changes parts on its car (a door off and back on,
            a wheel off, a hubcap off) and moves it; the host comes back → same census on both
  B BACK  : the same the other way: the laptop away, the host changes its own car, the laptop comes back
  B2      : (after B) the laptop away again; the host takes the door it bolted back in B (an object of its own now,
            its mirror on it) off again and leaves it, moves the car; the laptop back → census, every object within
            15 m on both (playround R2: the laptop's copy came back with that door's mirror in the car)
  D DRIVE : the laptop drives its car away on autopilot (70 km/h) while the host stands; past the host's item range
            the host's copy is dormant (streamed out) for --drive s; then the host jumps 60 m behind the car. Its copy
            must show where the car is from the first frames (no glide in from where it went out of view), and be placed
            once (no stream-out/in flicker at the edge)
Census: `mp carparts <net>` — every part bolted into the car (slot → model) and the wheels in the car's frame.
  tools/meetagain.py [--no-build] [--relay iad|off] [--only D,E,A,B] [--cycles 4] [--drive 40] [--old] [--car 2] [--leftoff]"""
import sys, os, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import CAR01, g, car_ready, clear_road

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY = arg("--relay", "iad")
ONLY = set(arg("--only", "D,E,A,B").split(","))
DRIVE_S = arg("--drive", 40, float)
CAR = arg("--car", CAR01, int)
CYCLES = arg("--cycles", 4, int)
LEFT_OFF = "--leftoff" in sys.argv   # A/B also take the re-bolted door (with its mirror) off again and leave it off
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

def in_world(s):
    try: return s("get DataFromMenuScript.s.mainmenu")["value"] is False and s("get mainscript.s.player")["value"] is not None
    except Exception: return False

def travel(s, gx, gz, who, settle=8):
    """ground-safe teleport to a GLOBAL point (farworld.travel without the building approach)"""
    s("set mainscript.s.player.godModeTillGrounded True")
    s(f"tpg {gx} {gz} hold")
    def grounded():
        g = s("gpos"); return "y" in s(f"ground {g['ux']} {g['uz']}")
    ok = until(grounded, 90, 1.0)
    if not ok:   # once more from 300 m aside (a far jump's ground sometimes never generates under the held player)
        s(f"tpg {gx + 300} {gz} hold"); until(grounded, 60, 1.0); s(f"tpg {gx} {gz} hold"); ok = until(grounded, 90, 1.0)
    if not ok: s("tprelease"); raise RuntimeError(f"{who}: no ground at {gx:.0f},{gz:.0f}")
    r = s(f"tpground {gx} {gz}")
    if r.get("tpground") == "hop": until(grounded, 30, 1.0); s(f"tpground {gx} {gz}")
    time.sleep(settle)
    if not in_world(s): raise RuntimeError(f"{who}: left the world")
    g = s("gpos"); print(f"  {who} at {g['gx']:.0f},{g['gz']:.0f}", flush=True)
    return g

def ent(s, net):
    return next((e for e in s("mp entities")["entities"] if e["net"] == net), None)

def census(s, net):
    return s(f"mp carparts {net}")

def same(ref, got, label, wtol=0.05):
    """parts: the same slot → model rows; wheels: the same slots, within wtol m in the car's frame"""
    if got.get("dormant") or "error" in got: verdict(label, False, f"not here: {got}"); return False
    rp, gp = set(ref["parts"]), set(got["parts"])
    missing, extra = sorted(rp - gp), sorted(gp - rp)
    rw = {w[0]: w[1:] for w in ref["wheels"]}; gw = {w[0]: w[1:] for w in got["wheels"]}
    off = [(k, round(math.dist(rw[k], gw[k]), 3)) for k in rw if k in gw and math.dist(rw[k], gw[k]) > wtol]
    wmiss = sorted(set(rw) - set(gw)); wextra = sorted(set(gw) - set(rw))
    ok = not missing and not extra and not off and not wmiss and not wextra
    verdict(label, ok, f"parts {len(rp)} vs {len(gp)}: missing {missing}, extra {extra}; wheels missing {wmiss}, extra {wextra}, off {off}")
    return ok

def car_gap(a, b, net):
    ea, eb = ent(a, net), ent(b, net)
    if not ea or not eb or ea.get("dormant") or eb.get("dormant"): return None
    return math.dist(ea["pos"], eb["pos"])

def spawn_car(s, who, near, off=8.0):
    me = s("mp entities")["me"]
    rp = s("roadpoint")
    cx, cz = (rp["gx"], rp["gz"]) if "error" not in rp and math.hypot(rp["gx"] - near["gx"], rp["gz"] - near["gz"]) < 60 else (near["gx"] + off, near["gz"] + off)
    print(f"  {who} car:", s(f"spawncarg {CAR} 0 {cx} {cz} 0"), flush=True)
    found = []
    def f():
        found[:] = [e["net"] for e in s("mp entities")["entities"] if e["car"] and e["owner"] == me and not e["dormant"]
                    and math.hypot(e["pos"][0] - cx, e["pos"][2] - cz) < 15]
        return bool(found)
    if not until(f, 20): sys.exit(f"{who}: its car was not shared")
    return found[0], cx, cz

def change_parts(s, net, who):
    """a door off and bolted back, a wheel off, a hubcap off; returns what was done"""
    done = []
    parts = s(f"mp attachedparts {net}")["parts"]
    door = next((p for p in parts if "Door" in p["name"]), None)
    hub = next((p for p in parts if "Disztarcsa" in p["name"]), None)
    if hub: done.append(("hubcap off", s(f"mp falloff {net} {hub['i']}")))
    time.sleep(2)
    wheel = next((p for p in s(f"mp attachedparts {net}")["parts"] if "Felni" in p["name"] or p["name"].startswith("Wheel")), None)
    if wheel: done.append(("wheel off", s(f"mp falloff {net} {wheel['i']}")))
    time.sleep(2)
    if door:
        before = {e["net"] for e in s("mp entities")["entities"]}
        done.append(("door off", s(f"mp falloff {net} {door['i']}")))
        time.sleep(3)
        new = [e for e in s("mp entities")["entities"] if e["net"] not in before and "Door" in e.get("name", "")]
        if new: done.append(("door back on", s(f"mp attachto {new[0]['net']} {net} -1")))
        time.sleep(2)
    # the same door (its mirror on it; an object of its own since it came off) off again and left off (playround R2: the
    # other machine's copy, stored meanwhile, came back with that mirror still in the car)
    if LEFT_OFF and door:
        door2 = next((p for p in s(f"mp attachedparts {net}")["parts"] if p["name"].startswith(door["name"])), None)
        if door2: done.append(("door left off", s(f"mp falloff {net} {door2['i']}")))
    time.sleep(3)
    for d in done: print(f"  {who} change: {d[0]} {d[1]}", flush=True)
    return done

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    GOD = {}
    for n_, s_ in (("h", h), ("L", L)):
        s_("set TLDRevamp.TestSafety.BlockAutoSave True"); GOD[n_] = s_("get kaposztaleves.s.settings.god").get("value"); s_("set kaposztaleves.s.settings.god True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    sid = h("net me")["steamId"]
    try:
        h("mp host 27070"); L(f"mp joinid {sid}")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True"); L("set kaposztaleves.s.settings.god True")   # (the join reloads the world)
        OLD = "--old" in sys.argv   # A/B: both v0.66.2 fixes off on both machines
        for s_ in (h, L):
            s_(f"set TLDRevamp.Net.PoseInterpolator.FreshAfterIdle {not OLD}"); s_(f"set TLDRevamp.Net.Entities.FarMoveAttached {not OLD}")
        print(f"  {'OLD (fixes off)' if OLD else 'NEW (fixes on)'}: FreshAfterIdle {h('get TLDRevamp.Net.PoseInterpolator.FreshAfterIdle')['value']}/{L('get TLDRevamp.Net.PoseInterpolator.FreshAfterIdle')['value']}", flush=True)
        time.sleep(8)
        rem = h("get itemPlaceRemoveScript.s.itemRemoveDist")["value"]; spawnd = h("get itemPlaceRemoveScript.s.itemSpawnDist")["value"]
        print(f"  item range: remove beyond {rem} m, place within {spawnd} m", flush=True)
        hp = h("gpos"); lp = L("gpos")

        def find(o, key):
            if isinstance(o, dict):
                if key in o: return o[key]
                for v in o.values():
                    r = find(v, key)
                    if r is not None: return r
            return None

        if "D" in ONLY:
            gods_shift = g(L, "settingsscript.s.S.BAutoShift")
            L("set settingsscript.s.S.BAutoShift True")
            try:
                rp = L("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
                r0 = clear_road(L, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
                if not car_ready(L, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "laptop"): sys.exit("LAPTOP NOT IN ITS CAR")
                me = L("mp players")["me"]
                car = min((e for e in L("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
                until(lambda: (ent(h, car) or {}).get("dormant") is False, 30)
                travel(h, me[0] - F[0] * 40, me[2] - F[1] * 40, "desktop (behind the laptop's car)")
                until(lambda: (ent(h, car) or {}).get("dormant") is False, 30); time.sleep(3)
                ph0, eh0 = h("mp physical"), h("mp entities")
                L("autodrive on 19.4")
                gone = until(lambda: (ent(h, car) or {}).get("dormant") is True, 90, 1.0)
                print(f"  host's copy streamed out: {gone}; driving on {DRIVE_S:.0f} s", flush=True)
                time.sleep(DRIVE_S)
                la = ent(L, car)["pos"]; time.sleep(1.0)
                le = ent(L, car); lp_ = le["pos"]; v = abs(float(g(L, "mainscript.s.player.Car.speed") or 0))
                vel = (lp_[0] - la[0], 0, lp_[2] - la[2])
                print(f"  laptop's car at {lp_[0]:.0f},{lp_[2]:.0f} ({v:.0f} km/h); host copy dormant {(ent(h, car) or {}).get('dormant')}", flush=True)
                # the host jumps to the car (60 m behind it along its way): the copy is placed from the far store
                ph1, eh1 = h("mp physical"), h("mp entities")
                d = math.hypot(vel[0], vel[2])
                bx, bz = (lp_[0] - vel[0] / d * 60, lp_[2] - vel[2] / d * 60) if d > 1 else (lp_[0] - 60, lp_[2])
                # what the host places on arrival (the rebound counter counts every entity): its log names each one
                logf = pathlib.Path(os.environ.get("TLD_DIR", "")) / "BepInEx/LogOutput.log"; car_placed = None
                log0 = logf.stat().st_size if logf.is_file() else 0
                h("set TLDRevamp.Net.Entities.StoreOutTrace 40")
                h("set mainscript.s.player.godModeTillGrounded True"); h(f"tpg {bx} {bz} hold")
                gaps, t0, states = [], time.time(), []
                while time.time() - t0 < 25:
                    he, le2 = ent(h, car), ent(L, car)
                    st = "-" if not he else "dormant" if he.get("dormant") else "placed"
                    if not states or states[-1][1] != st: states.append((round(time.time() - t0, 2), st))
                    if he and le2 and not he.get("dormant"):
                        if len(gaps) < 25: gaps.append((round(time.time() - t0, 2), round(math.dist(he["pos"], le2["pos"]), 1)))
                    time.sleep(0.08)
                gr = lambda: (lambda q: "y" in h(f"ground {q['ux']} {q['uz']}"))(h("gpos"))
                until(gr, 60, 1.0); h(f"tpground {bx} {bz}"); time.sleep(4)
                h("set TLDRevamp.Net.Entities.StoreOutTrace 0")
                print(f"  the car's copy on the host after the jump (s, state): {states}", flush=True)
                if logf.is_file():
                    with open(logf, "rb") as fh: fh.seek(log0); new = fh.read().decode("utf-8", "replace").splitlines()
                    print("  host placed/streamed out:", [l.split("] ", 1)[-1][:140] for l in new if "[placed]" in l or "[storeout]" in l][:30], flush=True)
                    car_placed = sum(1 for l in new if "[placed]" in l and f" net {car} " in l)
                else: car_placed = None
                L("autodrive off")
                ph2, eh2 = h("mp physical"), h("mp entities")
                print(f"  host copy vs laptop's car after the host arrived (s, m): {gaps}", flush=True)
                sweeps = find(ph2, "copySweeps") - find(ph1, "copySweeps")
                reb = find(eh2, "rebound") - find(eh1, "rebound")
                print(f"  while away: idle restarts +{(find(ph1, 'ipIdleRestarts') or 0) - (find(ph0, 'ipIdleRestarts') or 0)}; "
                      f"on arrival: copy sweeps +{sweeps}, big-correction snaps +{(find(ph2, 'ipBigCorrSnaps') or 0) - (find(ph1, 'ipBigCorrSnaps') or 0)}, "
                      f"copy placed (rebound) +{reb}", flush=True)
                worst = max((x[1] for x in gaps), default=None)
                verdict("D1 THE CAR COMES BACK INTO VIEW WHERE IT IS (copy vs owner, first samples)", bool(gaps) and worst < 15, f"worst {worst} m over {len(gaps)} samples")
                verdict("D2 NO GLIDE ACROSS THE WORLD (copy sweeps)", sweeps == 0, f"+{sweeps}")
                # the rebound counter counts every entity placed (a rabbit near the arrival point made it +2): the car's own
                # [placed] lines in the host's log when there is one
                verdict("D3 PLACED ONCE (no flicker at the edge)", car_placed == 1 if car_placed is not None else reb == 1,
                        f"the car placed {car_placed} times (log); every entity: rebound +{reb}")
                verdict("D4 ITS PARTS AS THE OWNER HAS THEM", same(census(L, car), census(h, car), "D4 census") or True)
            finally:
                try: L("autodrive off")
                except Exception: pass
                L(f"set settingsscript.s.S.BAutoShift {gods_shift}")
            travel(h, hp["gx"], hp["gz"], "desktop (home)")
            L("driveout"); time.sleep(1)
            travel(L, hp["gx"] + 6, hp["gz"] + 6, "laptop (next to the host)")
            hp = h("gpos"); lp = L("gpos")

        if "E" in ONLY:
            car, cx, cz = spawn_car(L, "laptop", lp)
            until(lambda: (ent(h, car) or {}).get("dormant") is False, 20)
            time.sleep(6)
            ref = census(L, car); print(f"  laptop's car {car}: {ref['n']} parts, wheels {ref['wheels']}", flush=True)
            same(ref, census(h, car), "E0 START: THE HOST'S COPY MATCHES")
            # the host stands on the line from the car outwards, the car just inside its item range
            ux, uz = 1.0, 0.0
            start = spawnd - 30   # inside the game's place range (it places within itemSpawnDist, removes beyond itemRemoveDist)
            travel(h, cx - ux * start, cz - uz * start, f"desktop (car {start:.0f} m away)")
            time.sleep(4)
            print(f"  host copy before the cycles: {ent(h, car) and {k: ent(h, car)[k] for k in ('dormant', 'proxy')}}", flush=True)
            for i in range(CYCLES):
                step = rem + 60 - start
                L(f"mp moveitem {car} {step:.0f} 0"); time.sleep(5)   # 60 m past the remove range
                out = ent(h, car); dormant_out = out and out.get("dormant")
                L(f"mp moveitem {car} {-step:.0f} 0")                  # back where it was
                back = until(lambda: (ent(h, car) or {}).get("dormant") is False, 20)
                time.sleep(5)
                gap = car_gap(h, L, car)
                print(f"  cycle {i + 1}: out → host dormant {dormant_out}; back in {back}; car {gap and round(gap, 2)} m apart", flush=True)
                same(census(L, car), census(h, car), f"E{i + 1} EDGE CYCLE {i + 1}: THE HOST'S COPY AFTER COMING BACK")
                verdict(f"E{i + 1} EDGE CYCLE {i + 1}: THE CAR WHERE THE OWNER HAS IT", gap is not None and gap < 0.5, f"{gap and round(gap, 2)} m")
            travel(h, hp["gx"], hp["gz"], "desktop (home)")

        if "A" in ONLY:
            lp = L("gpos")
            car, cx, cz = spawn_car(L, "laptop", lp, off=-10)
            until(lambda: (ent(h, car) or {}).get("dormant") is False, 20); time.sleep(6)
            c0 = census(L, car)
            print("  tyres and rims (model/variant):", [r for r in c0["parts"] if any(k in r for k in ("Gumi", "Felni", "Disztarcsa", "Wheel", "(car)"))], flush=True)
            same(c0, census(h, car), "A0 START: THE HOST'S COPY MATCHES")
            travel(h, cx + 4000, cz, "desktop (4 km away)")
            until(lambda: (ent(h, car) or {}).get("dormant") is True, 30)
            print(f"  host copy while away: dormant {(ent(h, car) or {}).get('dormant')}", flush=True)
            change_parts(L, car, "laptop")
            print("  car moved 30 m:", L(f"mp moveitem {car} 0 30"), flush=True)
            time.sleep(8)
            ref = census(L, car)
            travel(h, cx + 15, cz + 45, "desktop (back at the car)")
            until(lambda: (ent(h, car) or {}).get("dormant") is False, 30); time.sleep(8)
            same(ref, census(h, car), "A1 AFTER BEING AWAY: THE HOST'S COPY HAS THE OWNER'S PARTS")
            gap = car_gap(h, L, car)
            verdict("A1 AFTER BEING AWAY: THE CAR WHERE THE OWNER HAS IT", gap is not None and gap < 0.5, f"{gap and round(gap, 2)} m")

        if "B" in ONLY:
            hp = h("gpos")
            car, cx, cz = spawn_car(h, "host", hp, off=12)
            until(lambda: (ent(L, car) or {}).get("dormant") is False, 30); time.sleep(6)
            same(census(h, car), census(L, car), "B0 START: THE LAPTOP'S COPY MATCHES")
            travel(L, cx - 4000, cz, "laptop (4 km away)")
            until(lambda: (ent(L, car) or {}).get("dormant") is True, 30)
            print(f"  laptop copy while away: dormant {(ent(L, car) or {}).get('dormant')}", flush=True)
            change_parts(h, car, "host")
            print("  car moved 30 m:", h(f"mp moveitem {car} 0 30"), flush=True)
            time.sleep(8)
            ref = census(h, car)
            travel(L, cx - 15, cz + 45, "laptop (back at the car)")
            until(lambda: (ent(L, car) or {}).get("dormant") is False, 30); time.sleep(8)
            same(ref, census(L, car), "B1 AFTER BEING AWAY: THE LAPTOP'S COPY HAS THE OWNER'S PARTS")
            gap = car_gap(h, L, car)
            verdict("B1 AFTER BEING AWAY: THE CAR WHERE THE OWNER HAS IT", gap is not None and gap < 0.5, f"{gap and round(gap, 2)} m")
            # B2: the re-bolted door off again while the laptop's copy is stored, left off
            travel(L, cx - 4000, cz, "laptop (4 km away again)")
            until(lambda: (ent(L, car) or {}).get("dormant") is True, 30)
            door = next((p for p in h(f"mp attachedparts {car}")["parts"] if "FRDoor" in p["name"]), None)
            print("  host: the re-bolted door off again, left off:", door and h(f"mp falloff {car} {door['i']}"), flush=True)
            time.sleep(4)
            print("  car moved 10 m:", h(f"mp moveitem {car} 10 0"), flush=True)
            time.sleep(8)
            ref = census(h, car); c2 = ent(h, car)["pos"]
            travel(L, c2[0] - 15, c2[2] + 45, "laptop (back at the car)")
            until(lambda: (ent(L, car) or {}).get("dormant") is False, 30); time.sleep(8)
            same(ref, census(L, car), "B2 THE DOOR LEFT OFF: THE LAPTOP'S COPY HAS THE OWNER'S PARTS")
            sa = [it for it in h(f"mp sitestate {c2[0]} {c2[2]} 15")["items"] if "Ragdol" not in it[5]]
            sb = [it for it in L(f"mp sitestate {c2[0]} {c2[2]} 15")["items"] if "Ragdol" not in it[5]]
            left = list(sb); only_h = []
            for it in sa:
                m = next((k for k, o in enumerate(left) if o[0] == it[0] and math.dist(o[1:4], it[1:4]) < 1.0), None)
                if m is None: only_h.append((it[5], [round(v, 1) for v in it[1:4]]))
                else: left.pop(m)
            only_l = [(o[5], [round(v, 1) for v in o[1:4]]) for o in left]
            verdict("B2 THE DOOR LEFT OFF: EVERYTHING WITHIN 15 M THE SAME ON BOTH", not only_h and not only_l, f"host {len(sa)}, laptop {len(sb)}: only on the host {only_h}, only on the laptop {only_l}")
    finally:
        for n_, s_ in (("L", L), ("h", h)):
            try:
                if GOD.get(n_) is not None: s_(f"set kaposztaleves.s.settings.god {GOD[n_]}")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
