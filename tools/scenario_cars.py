#!/usr/bin/env python3
"""Watchable car scenarios on two machines (desktop hosts, laptop joins). Captions on both screens say what's
happening and what to look for; the numbers are measured at the same time.
  1/3 car appears   2/3 drive-by at 90 km/h   3/3 drive-by at simulated 180 ms / 2 % loss / jitter
  tools/scenario_cars.py [--pace 1.0] [--no-build]
"""
import sys, time, math, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP

PACE, BUILD, ONLY = 0.0, True, None   # fast by default; --watch paces it for a person
it = iter(sys.argv[1:])
for a in it:
    if a == "--pace": PACE = float(next(it))
    elif a == "--watch": PACE = 1.0
    elif a == "--no-build": BUILD = False
    elif a == "--only": ONLY = next(it).split(",")   # e.g. --only 1,4

def wait(s): time.sleep(s * PACE)

def until(fn, timeout=15, step=0.25):
    """Poll a condition instead of sleeping a fixed time; returns whether it held."""
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def dist(a, b): return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))

T0 = time.time()
print(f"games ready in {ensure_both(BUILD):.0f} s")
L = Agent()
b = None
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        print(f"host world ready at {time.time() - T0:.0f} s")
        for side in (b, L):
            side("set TLDRevamp.TestSafety.BlockAutoSave True"); side("set kaposztaleves.s.settings.god True")
        def both(cmd): b(cmd); L(cmd)
        def banner(text, secs=12): both(f"banner {secs} " + text.replace("\n", "\\n"))  # one bridge line

        b("mp host 27070"); L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 120)       # the laptop loads the host's world by itself
        L("set TLDRevamp.TestSafety.BlockAutoSave True"); L("set kaposztaleves.s.settings.god True")
        print(f"laptop in the host's world at {time.time() - T0:.0f} s")
        print("session:", L("mp status")["client"]["id"], "joined; seeds", L("mp status")["client"]["serverSeed"], L("mp status")["client"]["mySeed"])

        # staging along the main road near the start (no floating-origin shift here: global = Unity)
        rp = b("roadpoint")
        yaw = rp["yaw"] + 180.0                       # travel direction (east on this seed)
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        R = (F[1], -F[0])                             # right of travel
        def at(along, side): return rp["x"] + F[0] * along + R[0] * side, rp["z"] + F[1] * along + R[1] * side
        def place(who, along, side, face):
            x, z = at(along, side); g = who(f"ground {x} {z}")
            who(f"teleportyaw {x} {g['y'] + 1.5} {z} {face}"); who("set mainscript.s.player.godModeTillGrounded True")
        face_road = math.degrees(math.atan2(-R[0], -R[1]))
        place(b, 0, 9, face_road)                      # desktop watches from 9 m beside the road
        place(L, -30, 6, yaw)
        wait(3)

        # ---- 1/3 car appears
        banner("1/3  CAR APPEARS — the laptop spawns a car on the road and shares it.\nDesktop: watch it appear complete (wheels, glass, paint).")
        wait(4)
        cx, cz = at(-25, 0); g = L(f"ground {cx} {cz}")
        vx, vz = at(-25, 12); gv = b(f"ground {vx} {vz}")                     # viewer 12 m beside the car spot, facing it
        b(f"teleportyaw {vx} {gv['y'] + 1.5} {vz} {math.degrees(math.atan2(cx - vx, cz - vz))}")
        L(f"spawnpile 2 1 {cx} {g['y']} {cz} 1 1 1.0 4242"); time.sleep(4)
        L(f"carface {cx} {g['y']} {cz} {yaw}"); time.sleep(2)
        print("share:", L("mp share"))
        until(lambda: len(b("mp entities")["entities"]) >= 1 and len(L("mp entities")["entities"]) >= 1, 15); wait(5)
        de, le = b("mp entities"), L("mp entities")
        newest_net = max(e["net"] for e in le["entities"]) if le["entities"] else -1
        e_d = next((e for e in de["entities"] if e["net"] == newest_net), None); e_l = next((e for e in le["entities"] if e["net"] == newest_net), None)
        print(f"  1/3: desktop has {len(de['entities'])} shared car(s) {('proxy, ' + str(e_d['items']) + ' items') if e_d else ''}; "
              f"laptop owns {e_l['items'] if e_l else 0} items; position difference {dist(e_d['pos'], e_l['pos']):.2f} m" if e_d and e_l else "  1/3: FAILED", flush=True)
        print("  screenshots:", b("screenshot")["path"], "|", L("screenshot")["path"])
        wait(4)

        # ---- 8 state: fuel, a part's condition and a door/knob change on the owner; the copy must follow
        if ONLY is None or "8" in ONLY:
            net = newest_net
            def st(side): return side(f"mp entstate {net}")
            def same(): x, y = st(L), st(b); return all(x[k] == y[k] for k in ("tanks", "parts", "usables", "doors"))
            print(f"  8 state at share: same on both {same()}")
            banner("8  STATE — the laptop empties the tank, rusts a part and opens a door of its car.\nDesktop: the copy should show the same within a few seconds.", 20)
            print("     poke:", L(f"mp entpoke {net} fuel 1.5"), L(f"mp entpoke {net} part 0 3"), L(f"mp entpoke {net} usable 0 1"))
            t0 = time.time(); ok = until(same, 12); lag = time.time() - t0
            x, y = st(L), st(b)
            for k in ("tanks", "parts", "usables", "doors"):
                if x[k] != y[k]: print(f"     {k} differ:\n       laptop  {x[k][:300]}\n       desktop {y[k][:300]}")
            rs = L("mp entities")["resync"]; rd = b("mp entities")["resync"]
            print(f"  8 state: copy followed the owner {ok} after {lag:.1f} s; laptop sent {rs['sent']} ({rs['bytes']} B, {rs['unchanged']} unchanged), "
                  f"desktop applied {rd['applied']}, part refreshes {rd['partStates']}", flush=True)
            print("  screenshots:", b("screenshot")["path"], "|", L("screenshot")["path"])
            wait(4)

        # ---- 9 edits: the desktop (not the owner) changes the laptop's car the way a player does; the owner takes it
        if ONLY is None or "9" in ONLY:
            net = newest_net
            def st(side): return side(f"mp entstate {net}")
            def same(): x, y = st(L), st(b); return all(x[k] == y[k] for k in ("tanks", "parts", "usables", "doors"))
            banner("9  EDITS — you (desktop) pour fuel into the laptop's car, rust a part and move a knob.\nLaptop: its car should take the changes; nothing snaps back.", 20)
            before = st(L)
            print("     desktop edits:", b(f"mp entpoke {net} p:fuel 7.25"), b(f"mp entpoke {net} p:part 1 2"), b(f"mp entpoke {net} p:usable 0 0.5"))
            t0 = time.time(); ok = until(lambda: st(L)["tanks"] != before["tanks"] and same(), 10); lag = time.time() - t0
            time.sleep(4)   # past the owner's next passes: nothing may snap back
            x, y = st(L), st(b)
            for k in ("tanks", "parts", "usables", "doors"):
                if x[k] != y[k]: print(f"     {k} differ:\n       laptop  {x[k][:300]}\n       desktop {y[k][:300]}")
            ed, el = b("mp entities")["edits"], L("mp entities")["edits"]
            print("     marks:", ed.get("marks"))
            print(f"  9 edits: owner took the desktop's changes {ok} after {lag:.1f} s; still the same 4 s later {same()}; "
                  f"desktop marked {ed['marked']} sent {ed['sent']}, laptop applied {el['applied']} rejected {el['rejected']}; "
                  f"laptop fuel now {x['tanks'].split()[0] if x['tanks'] else '?'}", flush=True)
            wait(4)

        def newest(ents):
            return max(ents, key=lambda e: e["net"]) if ents else None

        def driveby(label, n):
            banner(f"{n}/3  getting ready: the laptop gets a fresh car 160 m up the road (it appears there)…", 12)
            try: L("autodrive off")
            except RuntimeError: pass
            if L("get mainscript.s.player.Bsitting")["value"] is True: L("driveout"); time.sleep(2)
            x, z = at(-160, 0); g = L(f"ground {x} {z}")
            L(f"spawnpile 2 1 {x} {g['y']} {z} 1 1 1.0 {4242 + n}"); time.sleep(4)
            L(f"carface {x} {g['y']} {z} {yaw}"); time.sleep(2)
            L(f"drivein {x} {g['y']} {z}"); time.sleep(2); L(f"drivein {x} {g['y']} {z}"); time.sleep(3)  # sitting in the driver seat shares it
            before = len(b("mp entities")["entities"])
            for _ in range(20):
                if len(b("mp entities")["entities"]) > before - 1 and newest(b("mp entities")["entities"])["net"] == newest(L("mp entities")["entities"])["net"]: break
                time.sleep(0.5)
            net = newest(L("mp entities")["entities"])["net"]
            banner(f"{n}/3  DRIVE-BY {label}: the laptop starts driving; you'll be placed ahead of it at the roadside…", 8)
            both("mp entstats")
            L("autodrive on 25")
            # which way does it really go? let it settle on the road, measure, then put the viewer on the road ahead
            time.sleep(6)
            p0 = {e["net"]: e for e in L("mp entities")["entities"]}[net]["pos"]; time.sleep(1.0)
            p1 = {e["net"]: e for e in L("mp entities")["entities"]}[net]["pos"]
            dx, dz = p1[0] - p0[0], p1[2] - p0[2]; m = max(1e-3, math.hypot(dx, dz)); dx, dz = dx / m, dz / m
            ax, az = p1[0] + dx * 150, p1[2] + dz * 150
            ga = b(f"ground {ax} {az}"); b(f"teleportyaw {ax} {ga['y'] + 1.5} {az} 0"); time.sleep(1)
            rq = b("roadpoint")                                   # the real road next to that spot
            ry = math.radians(rq["yaw"]); sx, sz = math.cos(ry), -math.sin(ry)   # perpendicular to the road
            vx, vz = rq["x"] + sx * 9, rq["z"] + sz * 9
            gv = b(f"ground {vx} {vz}")
            fx, fz = -dx + 0.35 * (rq["x"] - vx) / 9, -dz + 0.35 * (rq["z"] - vz) / 9   # towards the oncoming car, a bit to the road
            b(f"teleportyaw {vx} {gv['y'] + 1.5} {vz} {math.degrees(math.atan2(fx, fz))}")
            banner(f"{n}/3  DRIVE-BY {label} — the laptop's car is coming towards you along the road.\nWatch the car: any jumps, stutter or sliding?", 20)
            b("mp entstats")
            closest = 1e9
            att0 = {e["net"]: e for e in b("mp entities")["entities"]}[net]["attached"]
            fuel0 = {e["net"]: e for e in b("mp entities")["entities"]}.get(net, {}).get("fuel")
            gaps, t0 = [], time.time()
            while time.time() - t0 < 14:
                dd = {e["net"]: e for e in b("mp entities")["entities"]}; ll = {e["net"]: e for e in L("mp entities")["entities"]}
                if net in dd and net in ll:
                    gaps.append(dist(dd[net]["pos"], ll[net]["pos"]))
                    if len(gaps) == 20: print(f"     engine mid-drive: laptop running {ll[net].get('engRunning')} rpm {ll[net].get('engRpm')} | desktop copy running {dd[net].get('engRunning')} rpm {dd[net].get('engRpm')}")
                    me = b("mp players")["me"]; closest = min(closest, dist(dd[net]["pos"], me))
                time.sleep(0.25)
            L("autodrive off")
            d = {e["net"]: e for e in b("mp entities")["entities"]}[net]
            rs = L("mp entities")["resync"]
            print(f"     resync while driving so far (laptop): {rs['sent']} messages, {rs['itemsSent']} item records, {rs['bytes']} B; capture max {rs['captureMsMax']} ms, total {rs['captureMsTotal']} ms")
            for side, nm in ((b, "desktop"), (L, "laptop")):
                odd = [x for x in side(f"mp entitems {net}")["items"] if not x["loaded"] or x.get("fromRoot", 0) > 4]
                if odd: print(f"     {nm} copy of #{net}: items away from the car or not loaded: {odd[:6]}")
            gaps.sort(); spd = 24.0
            print(f"  {n}/3 {label}: car #{net}: desktop showed {d['frames']} frames, visible jumps (>20 cm) {d['jumps']}, largest deviation {d['maxDevCm']} cm; "
                  f"shown behind the real car by {gaps[len(gaps)//2]:.1f} m median (≈ {gaps[len(gaps)//2] / spd * 1000:.0f} ms at 90 km/h); "
                  f"passed the viewer at {closest:.0f} m{'  (STAGING MISS)' if closest > 30 else ''}; "
                  f"parts attached on the desktop copy {att0} → {d['attached']}", flush=True)
            wait(3)

        if ONLY is None or "2" in ONLY:
            place(b, 0, 9, face_road)
            driveby("(normal network)", 2)
        if ONLY is None or "3" in ONLY:
            both("net lag 180 2 10 40")
            place(b, 0, 9, face_road)
            driveby("(180 ms, 2 % loss, jitter)", 3)
            both("net lag 0")
        if ONLY is not None and "4" in ONLY and not (set(ONLY) & {"2", "3"}):
            # crash straight after 1/3: the laptop sits in that car first
            if L("get mainscript.s.player.Bsitting")["value"] is not True:
                cx0, cz0 = at(-25, 0); g0 = L(f"ground {cx0} {cz0}")
                L(f"drivein {cx0} {g0['y']} {cz0}"); time.sleep(2); L(f"drivein {cx0} {g0['y']} {cz0}"); time.sleep(2)

        # ---- 4/4 crash: head-on into a parked car (the game's crash damage = sudden change of forward speed)
        if ONLY is None or "4" in ONLY:
            me = b("mp players")["me"]
            ox, oz = me[0] + 25, me[2]                    # impact point, 25 m in front of the viewer (+x)
            go = L(f"ground {ox} {oz}")
            if L("get mainscript.s.player.Bsitting")["value"] is True: L("driveout"); time.sleep(2)
            L(f"teleportyaw {ox} {go['y'] + 1.5} {oz + 4} 0"); time.sleep(1)
            L(f"spawnpile 2 1 {ox} {go['y']} {oz} 1 1 1.0 777"); time.sleep(4)
            L(f"carface {ox} {go['y']} {oz} 90"); time.sleep(2)
            print("  obstacle:", L("mp share")); time.sleep(3)
            # back into the driving car (the other one), then aim it at the obstacle from 40 m
            ents = L("mp entities")["entities"]
            car = max((x for x in ents if x["part"] < 0 and dist(x["pos"], [ox, go["y"], oz]) > 5), key=lambda x: x["net"])
            L(f"drivein {car['pos'][0]} {car['pos'][1]} {car['pos'][2]}"); time.sleep(2); L(f"drivein {car['pos'][0]} {car['pos'][1]} {car['pos'][2]}"); time.sleep(2)
            gv = b(f"ground {me[0]} {me[2]}"); b(f"teleportyaw {me[0]} {gv['y'] + 1.5} {me[2]} 90")   # viewer faces the impact point
            banner("4/4  CRASH — the laptop's car hits a parked car at 90 km/h, 25 m in front of you.\nWatch: the parts that fly off should be the same on both screens and land in the same places.", 40)
            wait(4)
            rep0 = L("mp entities")["partsReported"]
            for attempt in range(3):
                gs = L(f"ground {ox} {oz - 40}")
                L(f"carmove {ox} {gs['y'] + 0.8} {oz - 40} 0 0 25"); time.sleep(3)
                if attempt == 0: print("  desktop screenshot at impact:", b("screenshot")["path"])
                time.sleep(4)
                if L("mp entities")["partsReported"] > rep0: break
            time.sleep(5)
            le, de = L("mp entities"), b("mp entities")
            lp = {e["net"]: e for e in le["entities"] if e["part"] >= 0}
            dp = {e["net"]: e for e in de["entities"] if e["part"] >= 0}
            diffs = [dist(lp[n]["pos"], dp[n]["pos"]) for n in lp if n in dp and not dp[n].get("dormant") and not lp[n].get("dormant")]
            print(f"  4/4 crash: parts off on the laptop {len(lp)} (reported {le['partsReported'] - rep0}), on the desktop {len(dp)} "
                  f"(same parts: {sorted((e['parent'], e['part']) for e in lp.values()) == sorted((e['parent'], e['part']) for e in dp.values())}); "
                  f"landing position difference max {max(diffs) if diffs else 0:.2f} m", flush=True)
            print(f"     far store: desktop parts applied to stored copies {de.get('partsAppliedStored')}, record moves {de.get('farRecordUpdates')} re-chunked {de.get('farRechunked')} members re-bound {de.get('membersRebound')} whole re-binds {de.get('rebound')}")
            # stored copies that lost parts while away: go there, the copy must come back as the owner has it
            dormant_cars = {e["net"] for e in de["entities"] if e["part"] < 0 and e.get("dormant")}
            print(f"     desktop: dormant cars {sorted(dormant_cars)[:10]}, parts by car {sorted({(e['parent'], bool(e.get('dormant'))) for e in dp.values()})}")
            for cn in sorted({e["parent"] for e in dp.values() if e.get("dormant")} | (dormant_cars & {e["parent"] for e in dp.values()})):
                lc = {e["net"]: e for e in L("mp entities")["entities"]}.get(cn)
                if not lc: continue
                x, y, z = lc["pos"]
                if L("get mainscript.s.player.Bsitting")["value"] is True: L("driveout"); time.sleep(1)
                L(f"teleportyaw {x - 6} {y + 3} {z} 90"); L("set mainscript.s.player.godModeTillGrounded True")   # the owner goes back too
                b(f"teleportyaw {x + 6} {y + 3} {z} 270"); b("set mainscript.s.player.godModeTillGrounded True"); time.sleep(3)
                until(lambda: not {e["net"]: e for e in L("mp entities")["entities"]}[cn].get("dormant"), 10)
                lc = {e["net"]: e for e in L("mp entities")["entities"]}.get(cn)
                gq = b(f"ground {x + 6} {z}")
                if gq.get("hit", True) and "y" in gq: b(f"teleportyaw {x + 6} {gq['y'] + 1.5} {z} 270")
                until(lambda: not {e["net"]: e for e in b("mp entities")["entities"]}[cn].get("dormant"), 10); time.sleep(2)
                dc = {e["net"]: e for e in b("mp entities")["entities"]}.get(cn)
                time.sleep(3)
                dparts = {e["net"]: e for e in b("mp entities")["entities"] if e["part"] >= 0 and e["parent"] == cn}
                lparts = {e["net"]: e for e in L("mp entities")["entities"] if e["part"] >= 0 and e["parent"] == cn}
                pd = [dist(lparts[n]["pos"], dparts[n]["pos"]) for n in lparts if n in dparts and not dparts[n].get("dormant")]
                print(f"  4b back at car #{cn} (its copy was stored when parts came off): attached here {dc['attached'] if dc else '?'} / owner {lc['attached']}; "
                      f"its fallen parts placed {len(pd)}/{len(lparts)}, max distance from the owner's {max(pd) if pd else 0:.2f} m", flush=True)
            ls_, ds_ = sorted((e['parent'], e['part']) for e in lp.values()), sorted((e['parent'], e['part']) for e in dp.values())
            if ls_ != ds_:
                print(f"     laptop only {[x for x in ls_ if x not in ds_]}, desktop only {[x for x in ds_ if x not in ls_]}")
                print(f"     rejects: laptop {le.get('partRejects')!r} desktop {de.get('partRejects')!r}; suppressed fall-offs desktop {de.get('suppressedFallOff')} damage {de.get('suppressedDamage')}")
            print(f"     members re-bound: laptop {le.get('membersRebound')} desktop {de.get('membersRebound')}")
            print("  screenshots:", b("screenshot")["path"], "|", L("screenshot")["path"])
            wait(4)
        # ---- 5 takeover: refused while the owner drives; one clean hand-over once they're out
        if ONLY is None or "5" in ONLY:
            me = b("mp players")["me"]
            cx, cz = me[0] + 15, me[2]
            gc = L(f"ground {cx} {cz}")
            if L("get mainscript.s.player.Bsitting")["value"] is True: L("driveout"); time.sleep(2)
            L(f"teleportyaw {cx} {gc['y'] + 1.5} {cz + 4} 0")
            L(f"spawnpile 2 1 {cx} {gc['y']} {cz} 1 1 1.0 555"); time.sleep(4); L(f"carface {cx} {gc['y']} {cz} 0"); time.sleep(2)
            L(f"drivein {cx} {gc['y']} {cz}"); time.sleep(2); L(f"drivein {cx} {gc['y']} {cz}"); time.sleep(3)   # laptop in the driver seat → shared
            net = max(x["net"] for x in L("mp entities")["entities"] if x["part"] < 0)
            banner("5  TAKEOVER — you try to take the laptop's car while its driver sits in it: must be refused, no glitching.", 15)
            wait(3)
            oc0 = b("mp entities")["ownerChanges"]
            try:
                b(f"drivein {cx} {gc['y']} {cz}"); time.sleep(2); b(f"drivein {cx} {gc['y']} {cz}"); time.sleep(4)
            except RuntimeError as ex: print("   desktop drivein:", str(ex)[:100])
            d1 = b("mp entities")
            own = {x["net"]: x for x in d1["entities"]}[net]["owner"]
            print(f"  5a while the laptop drives: owner {own} (laptop = {L('mp status')['client']['id']}), owner changes {d1['ownerChanges'] - oc0}, claims refused {d1['claimsRefused']}", flush=True)
            if b("get mainscript.s.player.Bsitting")["value"] is True: b("driveout"); time.sleep(2)
            banner("5  TAKEOVER — the laptop's driver gets out, you take the car and drive off.\nLaptop screen: watch the car leave smoothly with all its parts.", 30)
            L("driveout"); time.sleep(2)
            b("mp entstats"); L("mp entstats")
            b(f"drivein {cx} {gc['y']} {cz}"); time.sleep(2); b(f"drivein {cx} {gc['y']} {cz}"); time.sleep(3)
            d2 = b("mp entities")
            print(f"     5b debug: desktop sitting {b('get mainscript.s.player.Bsitting')['value']}, claimsSent {d2['claimsSent']}, claimsRefused {d2['claimsRefused']}")
            print(f"  5b after the laptop got out: desktop owns it: {({x['net']: x for x in d2['entities']}[net]['owner'] == 0)}, owner changes {d2['ownerChanges'] - oc0}", flush=True)
            b("autodrive on 15"); time.sleep(12); b("autodrive off")
            le = {x["net"]: x for x in L("mp entities")["entities"]}[net]; de = {x["net"]: x for x in b("mp entities")["entities"]}[net]
            print(f"  5c desktop drove it away: laptop copy {le['frames']} frames, visible jumps {le['jumps']}, largest deviation {le['maxDevCm']} cm; "
                  f"parts attached laptop copy {le['attached']} / desktop {de['attached']}; owner changes total {b('mp entities')['ownerChanges'] - oc0}", flush=True)
            if b("get mainscript.s.player.Bsitting")["value"] is True: b("driveout")
            wait(3)

        # ---- 6 world car: the host's start car exists once; the laptop takes it over and drives off
        if ONLY is None or "6" in ONLY:
            ents = b("mp entities")["entities"]
            cars = [e for e in ents if e["part"] < 0 and e["owner"] == 0 and e["car"]]
            print("     host-owned cars:", [(e["net"], e["name"]) for e in cars][:6])
            if not cars:
                print("  6 world car: no host-owned world car found")
            else:
                home = b("mp players")["me"]
                wc = min(cars, key=lambda e: e["net"])   # shared when hosting started (later ones are this run's cars, e.g. 5's)
                x, y, z = wc["pos"]
                if L("get mainscript.s.player.Bsitting")["value"] is True: L("driveout"); time.sleep(2)
                gd = b(f"ground {x - 12} {z}"); b(f"teleportyaw {x - 12} {gd['y'] + 1.5} {z} 90")
                banner("6  WORLD CAR — the laptop takes the host's start car and drives off.\nYou should see ONE car, leaving; nothing left behind.", 25)
                wait(3)
                n0 = b(f"mp carsnear {x} {z} 4")["active"]; oc0 = b("mp entities")["ownerChanges"]
                L(f"drivein {x} {y} {z}"); time.sleep(2); L(f"drivein {x} {y} {z}"); time.sleep(3)
                gm = L(f"ground {x + 30} {z}"); L(f"carmove {x + 30} {gm['y'] + 0.8} {z} 90"); time.sleep(4)   # the new owner moves it 30 m
                now = {e["net"]: e for e in L("mp entities")["entities"]}[wc["net"]]["pos"]
                moved = math.hypot(now[0] - x, now[2] - z)
                print(f"  6 world car {wc['name']}: owner changes {b('mp entities')['ownerChanges'] - oc0}, moved {moved:.0f} m; desktop cars at the start before {n0}, "
                      f"after {b(f'mp carsnear {x} {z} 4')['active']} (want 0 if it moved > 4 m), where it stopped {b(f'mp carsnear {now[0]} {now[2]} 4')['active']} (want 1)", flush=True)
                wait(4)

        # ---- 7 passenger: the laptop rides in the desktop's car; each side shows the other seated in its own copy
        if ONLY is not None and "7" in ONLY:
            me = b("mp players")["me"]
            cx, cz = me[0] + 10, me[2]
            g = b(f"ground {cx} {cz}")
            if b("get mainscript.s.player.Bsitting")["value"] is True: b("driveout"); time.sleep(2)
            b(f"spawnpile 2 1 {cx} {g['y']} {cz} 1 1 1.0 999"); time.sleep(4); b(f"carface {cx} {g['y']} {cz} 0"); time.sleep(2)
            b(f"drivein {cx} {g['y']} {cz}"); time.sleep(2); b(f"drivein {cx} {g['y']} {cz}"); time.sleep(3)   # desktop drives → shared
            net = max(e["net"] for e in b("mp entities")["entities"] if e["part"] < 0)
            until(lambda: any(e["net"] == net for e in L("mp entities")["entities"]), 15)
            banner("7  PASSENGER — the laptop gets in as passenger, the desktop drives.\nBoth screens: the other player should sit IN the car, not trail behind it.", 30)
            if L("get mainscript.s.player.Bsitting")["value"] is True: L("driveout"); time.sleep(2)
            print("     sit:", L(f"mp sitpassenger {net}")); time.sleep(2); print("     sit:", L(f"mp sitpassenger {net}")); time.sleep(3)
            b("autodrive on 20")
            seated_d = seated_l = total = 0
            t0 = time.time()
            while time.time() - t0 < 12:
                dp = b("mp players")["players"]; lp = L("mp players")["players"]
                seated_d += sum(1 for p in dp if p["seated"]); seated_l += sum(1 for p in lp if p["seated"]); total += 1
                time.sleep(0.5)
            b("autodrive off")
            car_moved = b("mp entities")
            print(f"  7 passenger: desktop showed the laptop player seated in {seated_d}/{total} samples, laptop showed the driver seated in {seated_l}/{total}", flush=True)
            print("  screenshots:", b("screenshot")["path"], "|", L("screenshot")["path"])
            wait(3)

        banner("Scenarios done — tell Claude what you saw.", 15)
        print(f"total {time.time() - T0:.0f} s")
        wait(5)
        both("mp stop"); both("set kaposztaleves.s.settings.god False")
finally:
    L.close()
