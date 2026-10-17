#!/usr/bin/env python3
"""Rabbits in a session (Entities.Rabbits, Fixes.Rabbits), two machines over the relay. OLD (RabbitSync and
RabbitOwners off on both: copies think on their own, no ownership rules) then NEW, on the same build.
  S  the player's spawner runs again (started on both).
  A  a rabbit the laptop's spawner makes (size 0.9 asked for): the host's copy is the same rabbit - size, scale, mass,
     speed, evil.
  B  the host's rabbit runs off and roars: the laptop's copy shows the owner's run animation and the roar.
  C  the host's evil rabbit (owner rules off: it stays the host's) bites the laptop standing in front of it: the laptop
     takes the damage. D: rules on - the rabbit goes to the laptop's machine (its player is nearest).
  E  a big rabbit charges the laptop's car (the laptop at the wheel): at the hit one machine simulates both (the rabbit's
     owner is the car's), the car's speed change fits the rabbit's momentum (a kinematic copy of the rabbit has infinite
     mass), and afterwards both screens show the car and the rabbit in the same place.
  F  the laptop's car rolls into a standing rabbit at 15 m/s: one machine simulates both, and the car loses only what a
     body of the rabbit's mass can take (a kinematic copy is a wall: a crash).
Both players in god mode; natural spawns off during the phases.
  tools/rabbits.py [--no-build] [--relay iad|off] [--only new]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
CC = "TLDRevamp.Diagnostics.CrashCause"
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
ONLY = sys.argv[sys.argv.index("--only") + 1] if "--only" in sys.argv else None
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

def ents(x): return x("mp entities")["entities"]
def ent(x, net): return next((e for e in ents(x) if e["net"] == net), None)
def rab(x, net):
    try: return x(f"mp rabbit {net}")
    except Exception: return None
def owner_side(net, h, L, lid):
    r = rab(h, net)
    return (L if r and r["owner"] == lid else h)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    sid = h("net me")["steamId"]
    gods = {}
    try:
        until(lambda: h("rabbits")["spawner"]["started"] and L("rabbits")["spawner"]["started"], 10)
        st_h, st_l = h("rabbits"), L("rabbits")
        print("spawner host:", st_h, "\nspawner laptop:", st_l, flush=True)
        verdict("S THE PLAYER'S SPAWNER RUNS (host, laptop)", bool(st_h["spawner"] and st_h["spawner"]["started"] and st_l["spawner"] and st_l["spawner"]["started"]))
        for s_ in (h, L): s_("set TLDRevamp.Fixes.Rabbits.Enabled False")
        h("mp host 27070"); L(f"mp joinid {sid}")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        lid = L("mp status")["client"]["id"]
        for nm, s_ in (("host", h), ("laptop", L)):
            gods[nm] = s_("get kaposztaleves.s.settings.god").get("value"); s_("set kaposztaleves.s.settings.god True")
        rp = h("roadpoint"); X, Z, YAW = rp["gx"], rp["gz"], rp["yaw"]
        F = (math.sin(math.radians(YAW)), math.cos(math.radians(YAW)))      # along the road
        R = (math.cos(math.radians(YAW)), -math.sin(math.radians(YAW)))     # across it
        modes = [("OLD", False), ("NEW", True)] if ONLY != "new" else [("NEW", True)]
        for mi, (tag, on) in enumerate(modes):
            for s_ in (h, L):
                s_(f"set TLDRevamp.Net.Entities.RabbitSync {on}"); s_(f"set TLDRevamp.Net.Entities.RabbitOwners {on}")
            # each mode further down the road: fresh ground, nothing left over
            bx, bz = X + F[0] * 300 * mi + R[0] * 30, Z + F[1] * 300 * mi + R[1] * 30
            h(f"tpground {bx} {bz}"); L(f"tpground {bx + 4} {bz}"); time.sleep(4)

            # ---- A: the laptop's spawner makes a big one
            before = {e["net"] for e in ents(L)}
            L("set TLDRevamp.Net.Entities.RabbitNextSize 0.9")
            print(f"{tag} A spawn:", L("rabbits spawn"), flush=True)
            if not until(lambda: any(e["net"] not in before and not e["car"] for e in ents(L)), 15): verdict(f"{tag} A SPAWNED AND SHARED", False); continue
            na = next(e["net"] for e in ents(L) if e["net"] not in before and not e["car"])
            if not until(lambda: rab(h, na) is not None, 20): verdict(f"{tag} A THE HOST HAS THE COPY", False); continue
            time.sleep(2)
            ra, rh = rab(L, na), rab(h, na)
            print("  laptop:", ra, "\n  host:  ", rh, flush=True)
            same = abs(ra["size"] - rh["size"]) < 0.002 and abs(ra["scale"] - rh["scale"]) < 0.002 and abs(ra["mass"] - rh["mass"]) < 0.05 and ra["evil"] == rh["evil"] and abs(ra["speed"] - rh["speed"]) < 0.01
            verdict(f"{tag} A THE COPY IS THE SAME RABBIT", same, f"size {ra['size']:.3f}/{rh['size']:.3f} scale {ra['scale']:.3f}/{rh['scale']:.3f} mass {ra['mass']:.1f}/{rh['mass']:.1f} evil {ra['evil']}/{rh['evil']}")

            # ---- B: the host's small rabbit runs and roars, the laptop 40 m off
            h(f"tpground {bx + 60 * R[0]} {bz + 60 * R[1]}"); L(f"tpground {bx + 60 * R[0] + 40 * F[0]} {bz + 60 * R[1] + 40 * F[1]}"); time.sleep(3)
            before = {e["net"] for e in ents(h)}
            h("set TLDRevamp.Net.Entities.RabbitNextSize 0.3"); h("devspawn 103")
            if not until(lambda: any(e["net"] not in before and not e["car"] for e in ents(h)), 15): verdict(f"{tag} B RABBIT", False); continue
            nb = next(e["net"] for e in ents(h) if e["net"] not in before and not e["car"])
            if not until(lambda: rab(L, nb) is not None, 20): verdict(f"{tag} B THE LAPTOP HAS THE COPY", False); continue
            time.sleep(2)
            p = rab(h, nb)["pos"]
            goal = (p[0] - 25 * R[0], p[2] - 25 * R[1])
            h(f"mp rabbitdo {nb} charge {goal[0]} {goal[1]} 6 4")
            diffs, ho, lo = [], [], []
            t0 = time.time()
            while time.time() - t0 < 4:
                a, b = rab(h, nb), rab(L, nb)
                if a and b and a["anim"] is not None and b["anim"] is not None: diffs.append(abs(a["anim"] - b["anim"])); ho.append(a["anim"]); lo.append(b["anim"])
                time.sleep(0.2)
            mean = sum(diffs) / max(1, len(diffs))
            print(f"  run anim host {['%.2f' % v for v in ho]}\n  run anim laptop {['%.2f' % v for v in lo]}", flush=True)
            verdict(f"{tag} B THE COPY RUNS AS THE OWNER'S", len(diffs) >= 8 and mean < 0.15 and max(ho) > 0.3, f"mean |diff| {mean:.3f} over {len(diffs)}, host peak {max(ho) if ho else 0:.2f}")
            fx0 = L("mp rabbits")["stats"]["fxApplied"]
            h(f"mp rabbitdo {nb} roar")
            roared = until(lambda: rab(L, nb)["roaring"], 3, 0.1)
            fx1 = L("mp rabbits")["stats"]["fxApplied"]
            verdict(f"{tag} B THE COPY ROARS WITH THE OWNER", roared and fx1 > fx0, f"roaring {roared}, events {fx0} -> {fx1}")

            # ---- C/D: the host's evil rabbit bites the laptop
            h("set TLDRevamp.Net.Entities.RabbitOwners False")
            hx, hz = bx + 120 * R[0], bz + 120 * R[1]
            h(f"tpground {hx} {hz}"); L(f"tpground {hx + 30 * F[0]} {hz + 30 * F[1]}"); time.sleep(3)
            before = {e["net"] for e in ents(h)}
            h("set TLDRevamp.Net.Entities.RabbitNextSize 0.95"); h("devspawn 103")
            if not until(lambda: any(e["net"] not in before and not e["car"] for e in ents(h)), 15): verdict(f"{tag} C RABBIT", False); continue
            nc = next(e["net"] for e in ents(h) if e["net"] not in before and not e["car"])
            if not until(lambda: rab(L, nc) is not None, 20): verdict(f"{tag} C THE LAPTOP HAS THE COPY", False); continue
            time.sleep(2)
            r = rab(h, nc); p, f = r["pos"], r["fwd"]
            L(f"tpground {p[0] + f[0] * 1.6} {p[2] + f[1] * 1.6}"); L(f"mp lookat {nc}"); h(f"tpground {p[0] - f[0] * 8} {p[2] - f[1] * 8}"); time.sleep(1.5)
            # it stands still for the bite (AI off on both): a rabbit that turned away bit the air (run 2)
            for s_ in (h, L): s_("set mainscript.s.AIDisable True")
            d0 = L("mp rabbits")["stats"]["damageTaken"]
            r = rab(h, nc); p, f = r["pos"], r["fwd"]
            L(f"tpground {p[0] + f[0] * 1.2} {p[2] + f[1] * 1.2}"); time.sleep(1)
            for _ in range(4):
                r = h(f"mp rabbitdo {nc} attack"); time.sleep(0.15)
                lp = L("gpos")
                print(f"    bite: trigger {rab(h, nc)['trigger']}, laptop {math.hypot(lp['gx'] - r['pos'][0], lp['gz'] - r['pos'][2]):.2f} m from it", flush=True)
                time.sleep(0.7)
            time.sleep(1)
            for s_ in (h, L): s_("set mainscript.s.AIDisable False")
            d1 = L("mp rabbits")["stats"]["damageTaken"]
            print("  host stats:", h("mp rabbits")["stats"], flush=True)
            verdict(f"{tag} C THE HOST'S RABBIT BITES THE LAPTOP", d1 > d0, f"laptop damage messages {d0} -> {d1}")
            h(f"set TLDRevamp.Net.Entities.RabbitOwners {on}")
            r = rab(h, nc); p, f = r["pos"], r["fwd"]
            L(f"tpground {p[0] + f[0] * 1.6} {p[2] + f[1] * 1.6}")
            moved = until(lambda: rab(h, nc)["owner"] == lid, 5, 0.25)
            verdict(f"{tag} D THE RABBIT GOES TO THE NEAREST PLAYER'S MACHINE", moved, f"owner {rab(h, nc)['owner']} (laptop {lid}); {h('mp rabbits')['stats']['lastMove']}")

            # ---- E: a big rabbit charges the laptop's car
            cx, cz = bx + 180 * R[0], bz + 180 * R[1]
            h(f"tpground {cx} {cz}"); L(f"tpground {cx + 4 * R[0]} {cz + 4 * R[1]}"); time.sleep(3)
            before = {e["net"] for e in ents(h)}
            h("devspawn 99")
            if not until(lambda: any(e["car"] and e["net"] not in before for e in ents(h)), 20): verdict(f"{tag} E CAR", False); continue
            car = next(e["net"] for e in ents(h) if e["car"] and e["net"] not in before)
            if not until(lambda: ent(L, car) is not None, 30): verdict(f"{tag} E THE LAPTOP HAS THE CAR", False); continue
            time.sleep(3)
            for _ in range(6):
                rr = L(f"mp sitdriver {car}")
                if "seat" in rr and not rr.get("stepped"): break
                time.sleep(1.5)
            if not until(lambda: ent(h, car)["owner"] == lid, 15): verdict(f"{tag} E SETUP: THE LAPTOP DRIVES THE CAR", False, "INVALID"); continue
            time.sleep(3)
            cb = L(f"mp body {car}"); cp = cb["pos"]
            # the host spawns it 14 m beside the car (devspawn: just in front of the host), then walks 40 m off. No
            # teleport of the rabbit itself: a copy interpolated across a 40 m jump sweeps through whatever is in the way
            # (run 1: the laptop's copy swept through the car at 691 m/s)
            h(f"tpground {cp[0] + 16 * R[0]} {cp[2] + 16 * R[1]}"); time.sleep(2)
            before = {e["net"] for e in ents(h)}
            h("set TLDRevamp.Net.Entities.RabbitNextSize 1.0"); h("devspawn 103")
            if not until(lambda: any(e["net"] not in before and not e["car"] for e in ents(h)), 15): verdict(f"{tag} E RABBIT", False); continue
            ne = next(e["net"] for e in ents(h) if e["net"] not in before and not e["car"])
            if not until(lambda: rab(L, ne) is not None, 20): verdict(f"{tag} E THE LAPTOP HAS THE RABBIT", False); continue
            h(f"tpground {cp[0] + 16 * R[0] + 40 * F[0]} {cp[2] + 16 * R[1] + 40 * F[1]}"); time.sleep(2)
            print("  before the charge: rabbit", rab(h, ne), flush=True)
            rmass = rab(h, ne)["mass"]; cmass = cb["mass"]
            for s_ in (h, L): s_(f"mp rabbitdo {ne} charge {cp[0]} {cp[2]} 12 4")
            samples = []
            t0 = time.time()
            while time.time() - t0 < 5:
                try:
                    c_l = L(f"mp body {car}"); r_h = rab(h, ne); r_l = rab(L, ne)
                    samples.append((time.time() - t0, c_l, r_h, r_l))
                except Exception as ex: print("  sample:", ex)
                time.sleep(0.05)
            vmax = max(math.hypot(s[1]["vel"][0], s[1]["vel"][2]) for s in samples)
            rmax = max(s[2]["vel"] if not s[2]["proxy"] else s[3]["vel"] for s in samples)
            dist_min = min(math.hypot(s[2]["pos"][0] - s[1]["pos"][0], s[2]["pos"][2] - s[1]["pos"][2]) for s in samples)
            near = [s for s in samples if math.hypot(s[2]["pos"][0] - s[1]["pos"][0], s[2]["pos"][2] - s[1]["pos"][2]) < 4]
            one = all(s[2]["owner"] == lid for s in near) if near else False
            bound = 2 * rmass * 12 / max(1.0, cmass) + 1.0
            print(f"  rabbit mass {rmass:.1f} kg, car {cmass:.0f} kg; car peak {vmax:.2f} m/s (momentum bound {bound:.2f}), rabbit peak {rmax:.2f}, closest {dist_min:.2f} m, {len(near)} samples within 4 m", flush=True)
            for s in samples[::4]: print(f"   t {s[0]:.2f} car v {math.hypot(s[1]['vel'][0], s[1]['vel'][2]):.2f} up {s[1]['up'][1]:.2f} rabbit owner {s[2]['owner']} v {s[2]['vel']:.2f}", flush=True)
            if not near: verdict(f"{tag} E THE RABBIT REACHED THE CAR", False, "INVALID: no hit"); continue
            verdict(f"{tag} E ONE MACHINE SIMULATES THE RABBIT AND THE CAR AT THE HIT", one)
            verdict(f"{tag} E THE CAR'S KICK FITS THE RABBIT'S MOMENTUM", vmax <= bound, f"{vmax:.2f} <= {bound:.2f} m/s")
            time.sleep(3)
            ch, cl = h(f"mp body {car}"), L(f"mp body {car}"); rh, rl = rab(h, ne), rab(L, ne)
            dc = math.dist(ch["pos"], cl["pos"]); dr = math.dist(rh["pos"], rl["pos"])
            verdict(f"{tag} E BOTH SCREENS AGREE AFTERWARDS", dc < 0.5 and dr < 1.0, f"car {dc:.2f} m, rabbit {dr:.2f} m apart")

            # ---- F: the laptop's car rolls into the host's rabbit at 15 m/s (AI off on both: it stands still)
            cb = L(f"mp body {car}"); cp, fw = cb["pos"], cb["fwd"]
            fl = math.hypot(fw[0], fw[2]) or 1.0; fx, fz = fw[0] / fl, fw[2] / fl
            spot = (cp[0] + fx * 25, cp[2] + fz * 25)
            h(f"tpground {spot[0] + 3 * fz} {spot[1] - 3 * fx}"); time.sleep(2)
            before = {e["net"] for e in ents(h)}
            h("set TLDRevamp.Net.Entities.RabbitNextSize 0.6"); h("devspawn 103")
            if not until(lambda: any(e["net"] not in before and not e["car"] for e in ents(h)), 15): verdict(f"{tag} F RABBIT", False); continue
            nf = next(e["net"] for e in ents(h) if e["net"] not in before and not e["car"])
            if not until(lambda: rab(L, nf) is not None, 20): verdict(f"{tag} F THE LAPTOP HAS THE RABBIT", False); continue
            h(f"mp rabbitdo {nf} charge {spot[0]} {spot[1]} 3 4"); time.sleep(4.5)
            for s_ in (h, L): s_("set mainscript.s.AIDisable True")
            h(f"tpground {spot[0] + 60 * fz} {spot[1] - 60 * fx}"); time.sleep(3)
            rr = rab(h, nf); miss = abs((rr["pos"][0] - cp[0]) * fz - (rr["pos"][2] - cp[2]) * fx)
            print(f"  rabbit {nf} owner {rr['owner']} mass {rr['mass']:.1f} kg, {miss:.2f} m off the car's line", flush=True)
            c0 = int(L(f"get {CC}.Logged")["value"])
            try: L("handbrake off")
            except Exception: pass
            L(f"mp body {car} launch 15")
            samples, t0 = [], time.time()
            while time.time() - t0 < 4:
                try: samples.append((time.time() - t0, L(f"mp body {car}"), rab(L, nf)))
                except Exception as ex: print("  sample:", ex)
                time.sleep(0.03)
            for s_ in (h, L): s_("set mainscript.s.AIDisable False")
            sp = [math.hypot(s[1]["vel"][0], s[1]["vel"][2]) for s in samples]
            hit = next((i for i, s in enumerate(samples) if math.hypot(s[2]["pos"][0] - s[1]["pos"][0], s[2]["pos"][2] - s[1]["pos"][2]) < 3), None)
            crashes = int(L(f"get {CC}.Logged")["value"]) - c0
            for s in samples[::5]: print(f"   t {s[0]:.2f} car v {math.hypot(s[1]['vel'][0], s[1]['vel'][2]):.2f} up {s[1]['up'][1]:.2f} rabbit owner {s[2]['owner']} kin {s[2]['kin']} v {s[2]['vel']:.2f}", flush=True)
            if hit is None: verdict(f"{tag} F THE CAR REACHED THE RABBIT", False, f"INVALID: no hit (line miss {miss:.2f} m)"); continue
            vin = max(sp[:hit + 1]); vafter = min(sp[hit:hit + 15]) if len(sp) > hit else 0
            drop = vin - vafter; bound = 2 * rr["mass"] * vin / max(1.0, cb["mass"]) + 1.5
            verdict(f"{tag} F ONE MACHINE SIMULATES THE CAR AND THE RABBIT IT HITS", all(s[2]["owner"] == lid for s in samples[max(0, hit - 2):hit + 3]))
            verdict(f"{tag} F THE CAR LOSES WHAT A {rr['mass']:.0f} KG BODY TAKES (as in single player)", drop <= bound and min(s[1]["up"][1] for s in samples) > 0.5,
                    f"{vin:.1f} -> {vafter:.1f} m/s (drop {drop:.1f}, bound {bound:.1f}), lowest up {min(s[1]['up'][1] for s in samples):.2f}, crash causes {crashes}")
        print("host:", h("mp rabbits")["stats"], "\nlaptop:", L("mp rabbits")["stats"], flush=True)
        print("errors host:", h("errors"), "\nerrors laptop:", L("errors"), flush=True)
        h("shot rabbits_host"); L("shot rabbits_laptop")
    finally:
        for s_ in (h, L):
            for k in ("RabbitSync", "RabbitOwners"):
                try: s_(f"set TLDRevamp.Net.Entities.{k} True")
                except Exception: pass
        for nm, s_ in (("host", h), ("laptop", L)):
            try: s_(f"set kaposztaleves.s.settings.god {gods.get(nm) if gods.get(nm) is not None else False}")
            except Exception as ex: print("god restore", nm, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
