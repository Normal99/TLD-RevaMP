#!/usr/bin/env python3
"""The 2026-10-03 bug list on two real machines over the relay (desktop hosts, laptop joins): what the desktop-only
bot tests couldn't show — real latency, a real second game applying attaches/removals, what the other player sees.
  1 hum / pee: the laptop's player hums, then pees — the host's model of them plays the clip, shows the stream
  2 cargo: the laptop's player sits in the host's car, drops their own lamp in it; the car's touch makes it the host's;
    the host drives off (launch 12 m/s): no crash damage, no part lost, the lamp still in the car on both machines
  3 dismount on someone else's car: the laptop dismounts the host's hubcap (as its player): it lands in the laptop
    player's hand, theirs; they bolt it back on: the host's game applies it, the host's again (bolted onto its car)
  4 a lost copy: the laptop's game loses its copy of the host's car (the vanilla streaming race, forced there): the
    host's car stays in the world, the laptop gets its copy back (Refetch)
  tools/buglist2m.py [--no-build] [--relay iad|off] [--only 1,2,3,4] [--car Car06Full] [--set Net.Entities.X=False,...] [--old66]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
# --set A=v,... : TLDRevamp field values on both machines once joined; --old66: every v0.66.x fix off (A/B)
OLD66 = ("Net.PoseInterpolator.FreshAfterIdle=False,Net.Entities.FarMoveAttached=False,Net.Entities.StoredAttach=False,"
         "Net.Entities.ClearHeldOnAttach=False,Net.Entities.ReshareSurvivors=False,Net.Entities.CopyContainersInert=False,"
         "Net.Entities.ScopedEdits=False,Net.Entities.CopyStartFreezeOff=False")
SETS = [kv.split("=") for kv in ((sys.argv[sys.argv.index("--set") + 1] if "--set" in sys.argv else "") + ("," + OLD66 if "--old66" in sys.argv else "")).split(",") if kv]
ONLY = (sys.argv[sys.argv.index("--only") + 1] if "--only" in sys.argv else "1,2,3,4").split(",")
PUSH = float(sys.argv[sys.argv.index("--push") + 1]) if "--push" in sys.argv else 2.0   # 7a: the car's pace, m/s
CAR = sys.argv[sys.argv.index("--car") + 1] if "--car" in sys.argv else "Car06Full"
ITEM = "regizseblampa"
T = "TLDRevamp.Net.Entities"; A = "TLDRevamp.Net.PlayerActs"
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
def dist(a, b): return math.dist(a, b)
def flat_park(h, net):
    """the car (the host's) to flat open ground near where it is: every probe over its path — 5 m behind to 12 m ahead,
    2.5 m either side — plain terrain within 0.25 m (the site's car stood by a ramp: the user saw it climb it in step 7)"""
    c = {e["net"]: e for e in h("mp entities")["entities"]}[net]
    g = h("gpos"); ox, oz = g["ux"] - g["gx"], g["uz"] - g["gz"]
    def ground(x, z):
        r = h(f"ground {x + ox:.2f} {z + oz:.2f}")
        return (r.get("y"), r.get("collider")) if r.get("hit") else (None, None)
    cx, cz = c["pos"][0], c["pos"][2]
    for rad in (0, 8, 14, 20, 28, 36):
        for ang in range(0, 360, 45 if rad else 360):
            px, pz = cx + rad * math.sin(math.radians(ang)), cz + rad * math.cos(math.radians(ang))
            for yaw in (0, 90, 180, 270):
                fx, fz = math.sin(math.radians(yaw)), math.cos(math.radians(yaw))
                ys = []
                for f in [x * 1.5 - 5 for x in range(12)]:
                    for l in (-2.5, 0, 2.5):
                        y, n = ground(px + fx * f + fz * l, pz + fz * f - fx * l)
                        if y is None or n != "Terrain": ys = None; break
                        ys.append(y)
                    if ys is None: break
                if ys and max(ys) - min(ys) < 0.25:
                    r = h(f"mount carto {net} {px:.2f} {pz:.2f} {yaw}")
                    print(f"  car {net} parked on flat ground {rad} m from where it was, yaw {yaw} (rise {max(ys) - min(ys):.2f} m): {r}", flush=True)
                    time.sleep(2)
                    return True
    print(f"  NO FLAT GROUND within 36 m of car {net}", flush=True)
    return False

def seat_laptop(h, L, net):
    """the laptop's player in the car's driver seat and the car the laptop's (up to three tries), or the run stops"""
    for _ in range(3):
        for _k in range(2):   # the first call may only step to the seat
            try: L(f"mp sitdriver {net}")
            except RuntimeError: pass
            time.sleep(1.5)
        if until(lambda: {e["net"]: e for e in h("mp entities")["entities"]}[net]["owner"] == 1, 6): return
        out(L); time.sleep(1)
    sys.exit(f"the laptop could not take the driver's seat of car {net}")

def out(s_):
    """out of any seat (no error when not seated)"""
    try: s_("driveout")
    except Exception: pass

def seat_idx(s):
    seats = s("seatraw")["seats"]
    target = next(x["i"] for x in seats if x["main"] == "null" and x["d0"])
    return next(x["i"] for x in seats if x["main"] == f"#{target}")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    for k_, v_ in SETS:
        for s_ in (h, L): print(f"  set {k_}={v_}:", s_(f"set TLDRevamp.{k_} {v_}"), flush=True)
    lid = L("mp status")["client"].get("id")
    gods = {}
    for name, s_ in (("host", h), ("laptop", L)):
        gods[name] = (s_, s_("get kaposztaleves.s.settings.god").get("value"))
        s_("set kaposztaleves.s.settings.god True")
    # --plock N: the game's physics-lock setting (cargo locked to the car: passenger room only at 3) for this run on
    # both machines — each player's own value is put back at the end
    PLOCK = sys.argv[sys.argv.index("--plock") + 1] if "--plock" in sys.argv else None
    plocks = {}
    if PLOCK is not None:
        for name, s_ in (("host", h), ("laptop", L)):
            plocks[name] = (s_, s_("get settingsscript.s.S.IPhysicsLock").get("value"))
            s_(f"set settingsscript.s.S.IPhysicsLock {PLOCK}")
        print("  physics lock set to", PLOCK, "(was", {k: v[1] for k, v in plocks.items()}, ")", flush=True)
    try:
        hp = h("gpos")
        h(f"tpground {hp['gx'] + 15} {hp['gz']}"); L(f"tpground {hp['gx'] + 15} {hp['gz'] + 6}"); time.sleep(4)
        before = set(ents(h))
        print("  host car:", h(f"devspawn {CAR}"), flush=True)
        net = until(lambda: next((n for n, e in ents(h).items() if n not in before and e["car"] and not e["proxy"]), None), 20)
        if not until(lambda: net in ents(L), 30): sys.exit("laptop never got the car")
        time.sleep(6)
        cp = ents(h)[net]["pos"]

        if "1" in ONLY:
            L(f"tpground {cp[0] + 8} {cp[2] + 8}"); h(f"tpground {cp[0] + 4} {cp[2] + 8}"); time.sleep(3)
            for k in range(3 if "--hum3" in sys.argv else 1):
                L(f"set {A}.TestHum True"); time.sleep(3)
                # the loop clip restarts once it ends (as the game's own hum source): a frame or two between plays —
                # one sample can land in that gap (1a failed once so, humVol 0.5): up to 6 samples 0.2 s apart
                for _ in range(6):
                    ha, la = h("mp acts"), L("mp acts")
                    m = (ha["models"] or [{}])[0]
                    if m.get("humPlaying"): break
                    time.sleep(0.2)
                if len(ha["models"] or []) != 1: print(f"  NOTE: the host has {len(ha['models'] or [])} remote-player models: {ha['models']}", flush=True)
                print(f"  1 hum #{k + 1}: host's model {m}\n      host sent/applied {ha.get('sent')}/{ha.get('applied')}; laptop local {la.get('local')} sent/applied {la.get('sent')}/{la.get('applied')}", flush=True)
                verdict(f"1a THE LAPTOP'S HUM PLAYS ON THE HOST{' #' + str(k + 1) if k else ''}", m.get("fresh") and m.get("humPlaying") and m.get("humVol", 0) > 0, f"{m}")
                L(f"set {A}.TestHum False"); time.sleep(3)
                m = (h("mp acts")["models"] or [{}])[0]
                verdict(f"1b AND STOPS{' #' + str(k + 1) if k else ''}", not m.get("humPlaying") or m.get("humVol", 1) < 0.02, f"{m}")
            L("mp acts fill"); L(f"set {A}.TestPiss True"); time.sleep(3)
            m = (h("mp acts")["models"] or [{}])[0]; print("  1 pee:", m, "laptop:", L("mp acts")["local"], flush=True)
            verdict("1c THE LAPTOP'S PEE SEEN AND HEARD ON THE HOST", m.get("pissEmit") and m.get("pissParticles", 0) > 0 and m.get("pourPlaying"), f"{m}")
            L(f"set {A}.TestPiss False"); time.sleep(7)
            m = (h("mp acts")["models"] or [{}])[0]
            verdict("1d AND STOPS", not m.get("pissEmit") and not m.get("pourPlaying"), f"{m}")

        if "v" in ONLY:
            # control: the HOST is the passenger in its own car and drops its own lamp — no copy, no claim, the game alone
            h(f"tpground {cp[0] + 6} {cp[2]}"); time.sleep(2)
            hb = set(ents(h))
            print("  host spawns:", h(f"devspawn {ITEM}"), flush=True)
            hl = until(lambda: next((n for n, e in ents(h).items() if n not in hb and not e["car"] and not e["proxy"] and ITEM in e["name"]), None), 20)
            print("  host to the passenger seat:", h(f"mp sitpassenger {net}"), flush=True); time.sleep(1.5)
            print("  host sits:", h(f"mp sitpassenger {net}"), flush=True); time.sleep(2)
            # look forward and down into the footwell: a fresh sit-down faces the side window, and a drop from there
            # lands outside the door (the control "failed" on the terrain for that reason - seen on the screenshots)
            h("set mainscript.s.player.FxRot 0"); h("set mainscript.s.player.FyRot 40"); time.sleep(0.5)  # before the pickup: turning with it in hand knocks it out on the dash
            print("  host picks it up:", h(f"mp pickupnet {hl}"), flush=True); time.sleep(2)
            print("  host's view before the drop:", h("shot v_before"), h("seatlook"), flush=True)
            print("  host drops it:", h("mp drop"), flush=True)
            for k in range(8):   # its fall, in the car's frame
                time.sleep(0.25); q = h(f"mount probe {hl}").get("inCar") or {}
                print(f"    t{(k+1)*0.25:.2f}s lp {q.get('lp')} inside {q.get('inside')}", flush=True)
            time.sleep(2)
            print("  host's view after the drop:", h("shot v_after"), flush=True)
            pr = h(f"mount probe {hl}")
            print(f"  control lamp probe: {pr}", flush=True)
            # in the car = the game's passenger-room lock took it (parented to the car: the probe's ray then skips the
            # car's colliders as the item's own root and sees the terrain), or it rests on something that isn't the ground
            locked = pr.get("physlock", "").endswith("passengerRoom")
            verdict("2v CONTROL: A PASSENGER'S OWN LAMP DROPPED IN ITS OWN CAR LANDS IN THE CAR",
                    (locked or "Terrain" not in pr.get("under", "")) and (pr.get("inCar") or {}).get("inside"),
                    f"lock '{pr.get('physlock')}', parent '{pr.get('parent')}', under {pr.get('under')}; in the car's frame {(pr.get('inCar') or {}).get('lp')}")
            out(h); time.sleep(1)

        if "2" in ONLY:
            # the laptop's lamp, spawned beside the car (its own, shared)
            L(f"tpground {cp[0] + 6} {cp[2]}"); time.sleep(2)
            lb = set(ents(L))
            print("  laptop spawns:", L(f"devspawn {ITEM}"), flush=True)
            lamp = until(lambda: next((n for n, e in ents(L).items() if n not in lb and not e["car"] and not e["proxy"] and ITEM in e["name"]), None), 20)
            until(lambda: lamp in ents(h), 20); time.sleep(2)
            # as a passenger: the laptop gets in, takes the lamp in its hands (a copy that collides with nothing), drops it
            print("  laptop to the passenger seat:", L(f"mp sitpassenger {net}"), flush=True); time.sleep(1.5)
            print("  laptop sits:", L(f"mp sitpassenger {net}"), flush=True); time.sleep(2)
            print("  laptop picks the lamp up:", L(f"mp pickupnet {lamp}"), flush=True); time.sleep(2)
            print("  host sees it held:", {k: ents(h)[lamp].get(k) for k in ("owner", "proxy")}, "heldShown", h(f"get {T}.HeldShown")["value"], flush=True)
            h("crash rec start 30"); h(f"mount trace {net} {lamp} 6"); time.sleep(0.3)
            print("  laptop drops it in the car:", L("mp drop"), flush=True); time.sleep(4)
            el_owner = lambda n: ents(L)[n]["owner"]
            def calm(tag, item):
                rc = h("crash rec stop"); cc = next((c for c in rc["cars"] if c.get("net") == net), {})
                ec = h("mp entities").get("attach", {})
                print(f"  host car while {tag}: damage {cc.get('damageMaxV')} spin {cc.get('peakSpinDeg')} upY {cc.get('endUpY')} parts lost {cc.get('partsLost')}; claims near {ec.get('cargoNear')} on contact {ec.get('cargoOnContact')}", flush=True)
                print("  host trace:\n    " + h("mount tracelog")["log"][:3000].replace("\n", "\n    "), flush=True)
                return (cc.get("peakSpinDeg") or 0) < 5 and (cc.get("damageMaxV") or 0) < 3 and (cc.get("partsLost") or 0) == 0, f"damage {cc.get('damageMaxV')} spin {cc.get('peakSpinDeg')} upY {cc.get('endUpY')}"
            print(f"  lamp probe host {h(f'mount probe {lamp}')}\n             laptop {L(f'mount probe {lamp}')}", flush=True)
            ok, why = calm("the lamp was dropped in and claimed", lamp)
            verdict("2- THE LAMP DROPPED IN AND CLAIMED DOESN'T MOVE OR DAMAGE THE CAR", ok, why)
            eh, el = ents(h)[lamp], ents(L)[lamp]
            print(f"  lamp: host {eh['owner']}/{eh['proxy']} at {eh['pos']}, laptop {el['owner']}/{el['proxy']} at {el['pos']}; cargo claims {h(f'get {T}.CargoClaimed')['value']}", flush=True)
            verdict("2a THE DROPPED LAMP (BESIDE THE SEAT, WITHIN THE CAR'S MARGIN) BECOMES THE HOST'S", eh["owner"] == 0 and not eh["proxy"] and el["owner"] == 0, f"host owner {eh['owner']}, laptop sees owner {el['owner']}")
            # from outside: the laptop gets out, stands beside the car and throws a second lamp in
            out(L); time.sleep(1.5)
            lb = set(ents(L))
            print("  laptop spawns another:", L(f"devspawn {ITEM}"), flush=True)
            # (spawned within the car's margin it may be the host's at once — the proximity claim; whoever owns it, it's new)
            lamp2 = until(lambda: next((n for n, e in ents(L).items() if n not in lb and not e["car"] and ITEM in e["name"]), None), 20)
            until(lambda: lamp2 in ents(h), 20)
            print("  laptop beside the car:", L(f"mp sitpassenger {net}"), flush=True); time.sleep(1.5)
            print("  laptop picks it up:", L(f"mp pickupnet {lamp2}"), flush=True); time.sleep(2)
            h("crash rec start 30"); h(f"mount trace {net} {lamp2} 6"); time.sleep(0.3)
            print("  laptop throws it in:", L(f"mp throwat {net} 0.5"), flush=True); time.sleep(4)
            ok, why = calm("a lamp was thrown in", lamp2)
            verdict("2d A LAMP THROWN INTO THE HOST'S CAR DOESN'T MOVE OR DAMAGE IT", ok, why)
            e2, ec2 = ents(h)[lamp2], ents(h)[net]
            pr2 = h(f"mount probe {lamp2}")
            print(f"  thrown lamp: host owner {e2['owner']} at {e2['pos']} ({dist(e2['pos'], ec2['pos']):.2f} m from the car); probe {pr2}", flush=True)
            verdict("2e THE THROWN LAMP BECOMES THE HOST'S", e2["owner"] == 0 and el_owner(lamp2) == 0, f"owner {e2['owner']}, landed on: {pr2.get('under')} (thrown at the closed door from 0.7 m)")
            # the host, its owner now, puts it on the back seat; then it must ride along — on the laptop too
            print("  host puts it on a seat:", h(f"mount putin {lamp2} {net}"), flush=True); time.sleep(3)
            pr3 = h(f"mount probe {lamp2}")
            print(f"  on the seat: {pr3.get('under')}", flush=True)
            lamp = lamp2   # the one in the car rides along in 2b/2c
            h(f"tpground {cp[0] + 3} {cp[2] + 3}"); time.sleep(1)
            print("  host sits:", h(f"sitseat {seat_idx(h)} redirect"), flush=True); time.sleep(2)
            h("crash rec start 30"); L("crash rec start 30"); time.sleep(0.5)
            # a real start (2 m/s every half second, ~0.4 g): a car set to 8 m/s in one physics step leaves its cargo where
            # it was — the seat slides out from under it (infinite acceleration; no driver can do that)
            for v in (2, 4, 5): r_ = h(f"crash launch {v}"); time.sleep(0.5)
            print("  launch:", r_, flush=True); time.sleep(5)
            rh, rl = h("crash rec stop"), L("crash rec stop")
            print(f"  after the drive, lamp probe host {h(f'mount probe {lamp}')}", flush=True)
            car = next((c for c in rh["cars"] if c.get("net") == net), {})
            sh_, sl_ = ents(h), ents(L)   # one snapshot per machine: the car is still rolling
            eh, el, ec, ecl = sh_[lamp], sl_[lamp], sh_[net], sl_[net]
            print(f"  host car: {car}\n  lamp host {eh['pos']} car {ec['pos']}; laptop lamp {el['pos']} car {ecl['pos']}", flush=True)
            verdict("2b DRIVING WITH THE LAMP: IT NEVER HITS THE CAR, NOTHING OFF WITHOUT A TERRAIN HIT", "lampa" not in str(car.get("contactWith")) and car.get("damageAfterContact", 0) == 0 and (car.get("partsLost") == 0 or car.get("damageMaxV", 0) > 15),
                    f"damage {car.get('damageMaxV')} (terrain; spin {car.get('peakSpinDeg')} deg), contact {car.get('contactWith')!r}, parts lost {car.get('partsLost')}")
            verdict("2c THE LAMP STAYS IN THE CAR ON BOTH MACHINES", dist(eh["pos"], ec["pos"]) < 3 and dist(el["pos"], ecl["pos"]) < 3,
                    f"host {dist(eh['pos'], ec['pos']):.2f} m, laptop {dist(el['pos'], ecl['pos']):.2f} m from the car")
            print("  host stops the car:", h("crash launch 0 hold"), flush=True); time.sleep(3)   # coasting otherwise: steps 3/4 go where it IS
            out(h); time.sleep(2)
            cp = ents(h)[net]["pos"]
            cp = ents(h)[net]["pos"]

        if "3" in ONLY:
            L(f"tpground {cp[0] + 2.5} {cp[2] + 1}"); time.sleep(2)
            sh = [x for x in h(f"mount slots {net}")["slots"] if "Disztarcsa" in x[0] or "Wheel" in x[0]]
            sl = [x for x in L(f"mount slots {net}")["slots"] if "Disztarcsa" in x[0] or "Wheel" in x[0]]
            print(f"  wheel/hubcap slots host {sh}\n                       laptop {sl}", flush=True)
            ih, il = h(f"mount items {net}")["items"], L(f"mount items {net}")["items"]
            print(f"  items host {len(ih)} laptop {len(il)}; differing:", [(x, y) for x, y in zip(ih, il) if x.split(" ")[1:] != y.split(" ")[1:] and x.split(" ")[1].rstrip("0123456789") != "Wheel"][:12], flush=True)
            print("  host wheel-area items:", [x for x in ih if any(k in x for k in ("Tire", "Wheel", "Disztarcsa", "Felni", "Gumi"))][:20], flush=True)
            print("  laptop wheel-area items:", [x for x in il if any(k in x for k in ("Tire", "Wheel", "Disztarcsa", "Felni", "Gumi"))][:20], flush=True)
            verdict("3- THE LAPTOP'S COPY HAS THE HOST'S WHEELS AND HUBCAPS", [bool(x[1]) for x in sh] == [bool(x[1]) for x in sl], "")
            lb = set(ents(L))
            L(f"set {T}.TestAsPlayer True")
            try: print("  laptop dismounts the host's hubcap:", L(f"mount off {net} DisztarcsaSlotCol"), flush=True)
            except Exception as ex:
                print("  dismount failed:", ex, "\n  slots now host", h(f"mount slots {net}")["slots"], "\n  laptop", L(f"mount slots {net}")["slots"], "\n  items laptop", L(f"mount items {net}")["items"], flush=True)
                raise
            L(f"set {T}.TestAsPlayer False")
            hub = until(lambda: next((n for n, e in ents(L).items() if n not in lb and "Disztarcsa" in e["name"]), None), 10)
            time.sleep(3)
            try: held = L("get mainscript.s.player.pickedUp.name").get("value")
            except Exception: held = None
            ah, al = h("mp entities"), L("mp entities")
            print(f"  host: attach {ah.get('attach')} partRejects {ah.get('partRejects')!r}\n  laptop: attach {al.get('attach')} partRejects {al.get('partRejects')!r} handedOver {L(f'get {T}.HandedOver')['value']}", flush=True)
            el = ents(L).get(hub, {}); eh = ents(h).get(hub, {})
            print(f"  hubcap {hub}: laptop holds {held}; laptop sees owner {el.get('owner')} proxy {el.get('proxy')}; host owner {eh.get('owner')}", flush=True)
            verdict("3a DISMOUNTED ON THE HOST'S CAR: IN THE LAPTOP PLAYER'S HAND, THEIRS", hub and "Disztarcsa" in str(held) and el.get("owner") == lid and not el.get("proxy"),
                    f"held {held}, owner {el.get('owner')} (laptop is {lid})")
            try: L("mp drop")
            except Exception as ex: print("  drop:", ex)
            time.sleep(1)
            L(f"mount watch {hub}")
            L(f"set {T}.TestAsPlayer True")   # the player's own bolt (fpscontroller.MountFunc in the game): a copy's goes to its owner
            print("  laptop bolts it back:", L(f"mount on {hub} {net} DisztarcsaSlotCol"), flush=True)
            L(f"set {T}.TestAsPlayer False"); time.sleep(10)   # 3+ resync passes
            print("  attach requests: laptop sent", L(f"get {T}.AttachReqsSent")["value"], "host applied", h(f"get {T}.AttachReqsApplied")["value"], "refused", h(f"get {T}.AttachReqsRefused")["value"], flush=True)
            print("  laptop's watch of the hubcap:", L("mount watchlog")["log"], flush=True)
            ih, il = h(f"mount items {net}")["items"], L(f"mount items {net}")["items"]
            print(f"  items host {len(ih)} laptop {len(il)}; differing:", [(x, y) for x, y in zip(ih, il) if x.split(" ")[1:] != y.split(" ")[1:] and x.split(" ")[1].rstrip("0123456789") != "Wheel"][:12], flush=True)
            print("  host wheel-area items:", [x for x in ih if any(k in x for k in ("Tire", "Wheel", "Disztarcsa", "Felni", "Gumi"))][:20], flush=True)
            print("  laptop wheel-area items:", [x for x in il if any(k in x for k in ("Tire", "Wheel", "Disztarcsa", "Felni", "Gumi"))][:20], flush=True)
            ph = h(f"mount pose {hub} {net} DisztarcsaSlotCol"); pl = L(f"mount pose {hub} {net} DisztarcsaSlotCol")
            print(f"  host: {ph}\n  laptop: {pl}\n  host attach counters: {h('mp entities').get('attach')}", flush=True)
            verdict("3b BOLTED BACK BY THE LAPTOP: IN THE SLOT ON BOTH, THE HOST'S", ph.get("inSlot") and pl.get("inSlot") and ph.get("dist", 9) < 0.05 and pl.get("dist", 9) < 0.05 and ph.get("owner") == 0 and pl.get("owner") == 0,
                    f"host in slot {ph.get('inSlot')} {ph.get('dist')} owner {ph.get('owner')}; laptop in slot {pl.get('inSlot')} {pl.get('dist')} owner {pl.get('owner')}")
            ih, il = h(f"mount items {net}")["items"], L(f"mount items {net}")["items"]
            par = lambda rows: [r.split(" @")[1].split(" ")[0] if " @" in r else "" for r in rows]
            rs = L("mp entities").get("resync", {})
            verdict("3e EVERY PART ON THE SAME PARENT ON BOTH MACHINES", par(ih) == par(il),
                    f"differ at {[i for i, (x, y) in enumerate(zip(par(ih), par(il))) if x != y]}; laptop foreignAttachDropped {rs.get('foreignAttachDropped')} attachReapplied {rs.get('attachReapplied')}")
            verdict("3c ON THE LAPTOP IT RIDES THE CAR (PARENTED, NOT STREAMED ON ITS OWN)", pl.get("ridesParent") is True, f"laptop parent {pl.get('parent')!r} root {pl.get('root')!r}")
            # the host drives off: the laptop sees the hubcap stay on the car
            h(f"tpground {cp[0] + 3} {cp[2] + 3}"); time.sleep(1)
            print("  host sits:", h(f"sitseat {seat_idx(h)} redirect"), flush=True); time.sleep(2)
            print("  host launches the car:", h(f"crash launch 6"), flush=True); time.sleep(5)
            pl = L(f"mount pose {hub} {net} DisztarcsaSlotCol")
            print(f"  laptop after the drive: {pl}\n  laptop's watch: {L('mount watchlog')['log'][-1500:]}", flush=True)
            verdict("3d THE HOST DRIVES OFF: ON THE LAPTOP THE HUBCAP STAYS ON THE CAR", pl.get("inSlot") and pl.get("dist", 9) < 0.05, f"dist {pl.get('dist')} parent {pl.get('parent')!r}")

        if "4" in ONLY:
            L(f"tpground {cp[0] + 4} {cp[2] + 4}"); time.sleep(2)
            L("set TLDRevamp.Fixes.StreamRace.Enabled False")   # the vanilla race, on the laptop only: its copy is lost
            a = L(f"streamrace arm {net}"); print("  laptop loses its copy:", a, flush=True)
            L("set TLDRevamp.Fixes.StreamRace.Enabled True")
            time.sleep(6)
            hc = ents(h).get(net); lc = ents(L).get(net)
            print(f"  host car {hc and hc['pos']}; laptop copy {lc and (lc['pos'], lc['proxy'], lc['dormant'])}", flush=True)
            verdict("4a THE HOST'S CAR SURVIVES THE LAPTOP'S LOSS", hc is not None and not hc["dormant"], f"{hc and hc['name']}")
            verdict("4b THE LAPTOP GETS ITS COPY BACK", lc is not None and not lc["dormant"] and dist(lc["pos"], hc["pos"]) < 1.0 if hc else False,
                    f"laptop copy {lc and lc['pos']} vs host {hc and hc['pos']}")
        if "5" in ONLY:
            for s_ in (h, L):
                try: s_("driveout")
                except Exception: pass
            # bug 5, "items don't stay in the car if you switch owners": the host's lamp rides in the host's car, then the
            # laptop takes the wheel (the car becomes the laptop's, the lamp is still the host's) and drives off
            cp = ents(h)[net]["pos"]
            h(f"tpground {cp[0] + 5} {cp[2] + 3}"); time.sleep(1.5)
            hb = set(ents(h))
            print("  host spawns a lamp:", h(f"devspawn {ITEM}"), flush=True)
            l5 = until(lambda: next((n for n, e in ents(h).items() if n not in hb and not e["car"] and ITEM in e["name"]), None), 20)
            until(lambda: l5 in ents(L), 20)
            print("  host puts it on a seat:", h(f"mount putin {l5} {net}"), flush=True); time.sleep(4)
            print(f"  lamp: host {h(f'mount probe {l5}')}\n        laptop {L(f'mount probe {l5}')}", flush=True)
            h(f"tpground {cp[0] + 8} {cp[2] + 8}"); time.sleep(1)
            print("  laptop to the driver's seat:", L(f"mp sitdriver {net}"), flush=True); time.sleep(1.5)
            try: print("  laptop sits:", L(f"mp sitdriver {net}"), flush=True)
            except RuntimeError as ex: print("  laptop sits: already —", ex, flush=True)   # the first call may have seated it
            took = until(lambda: ents(h)[net]["owner"] == 1 and ents(L)[net]["owner"] == 1, 10)
            el, eh = ents(L)[l5], ents(h)[l5]
            print(f"  car owner now: host sees {ents(h)[net]['owner']}, laptop {ents(L)[net]['owner']}; lamp owner host {eh['owner']} laptop {el['owner']}", flush=True)
            verdict("5- THE DRIVER'S SEAT MAKES THE CAR THE LAPTOP'S", bool(took), f"host sees {ents(h)[net]['owner']}")
            L("crash rec start 30"); L(f"mount trace {net} {l5} 12"); time.sleep(0.3)
            for v in (2, 4, 5): r_ = L(f"crash launch {v}"); time.sleep(0.5)
            print("  laptop drives:", r_, flush=True); time.sleep(5)
            rl = L("crash rec stop"); car = next((c for c in rl["cars"] if c.get("net") == net), {})
            sl_, sh_ = ents(L), ents(h)   # one snapshot per machine
            el, eh, ecl, ech = sl_[l5], sh_[l5], sl_[net], sh_[net]
            print(f"  laptop's car: damage {car.get('damageMaxV')} spin {car.get('peakSpinDeg')} parts lost {car.get('partsLost')} moved {car.get('movedAfterM')}", flush=True)
            print(f"  lamp owner host {eh['owner']} laptop {el['owner']}; lamp-car: laptop {dist(el['pos'], ecl['pos']):.2f} m, host {dist(eh['pos'], ech['pos']):.2f} m", flush=True)
            print(f"  lamp: laptop {L(f'mount probe {l5}')}\n        host {h(f'mount probe {l5}')}", flush=True)
            print("  laptop trace:\n    " + L("mount tracelog")["log"][:2500].replace("\n", "\n    "), flush=True)
            verdict("5a THE NEW DRIVER'S CAR TAKES NO HIT FROM THE LAMP", (car.get("partsLost") or 0) == 0 and (car.get("damageMaxV") or 0) < 5, f"damage {car.get('damageMaxV')} parts lost {car.get('partsLost')}")
            verdict("5b THE LAMP STAYS IN THE CAR ON BOTH MACHINES", dist(el["pos"], ecl["pos"]) < 3 and dist(eh["pos"], ech["pos"]) < 3,
                    f"laptop {dist(el['pos'], ecl['pos']):.2f} m, host {dist(eh['pos'], ech['pos']):.2f} m")
            L("crash launch 0 hold"); time.sleep(2); out(L); time.sleep(1)

        if "7" in ONLY or "7a" in ONLY:
            for s_ in (h, L):
                try: s_("driveout")
                except Exception: pass
            # bug 7, "players killed standing next to a friend": the game kills a player whose body's speed jumps by more
            # than deathVelocityChange in a frame (god mode on: measured, not died). A car rolls into a standing player at
            # walking pace — first the host's own car (control: the game's physics), then the laptop's (a copy on the host)
            def roll_into_host(who, tag):
                fr = h(f"mount front {net} 3.5"); time.sleep(2.5)
                h("mount deathwatch reset")
                # kept at walking pace: one launch coasts to a stop within a metre (the car never reached the host)
                for k in range(10):
                    v = 1 if k == 0 else PUSH
                    if who is h: h(f"crash launch {v} {fr['carGx']} {fr['carGz']}")
                    else: L(f"crash launch {v}")
                    time.sleep(0.4)
                dw = h("mount deathwatch")
                hp = h("gpos"); ec = ents(h)[net]
                print(f"  {tag}: host player {dw}; car moved to {ec['pos']}, host at {[hp['gx'], hp['gz']]}", flush=True)
                if who is h: h(f"crash launch 0 {fr['carGx']} {fr['carGz']} hold")
                else: L("crash launch 0 hold")
                time.sleep(2)
                return dw
            if ents(h)[net]["owner"] != 0:   # the control needs the host's own car: the host takes the wheel once
                h(f"mp sitdriver {net}"); time.sleep(1.5); h(f"mp sitdriver {net}")
                until(lambda: ents(h)[net]["owner"] == 0, 10); out(h); time.sleep(1)
            if not flat_park(h, net): sys.exit("step 7: no flat ground for the car")
            c = roll_into_host(h, "control, the host's own car")
            seat_laptop(h, L, net)
            m = roll_into_host(L, "the laptop's car (a copy here)")
            touched = m["vMax"] > 0.5 and c["vMax"] > 0.5   # the car reached the host (else the check says nothing)
            verdict("7a THE LAPTOP'S CAR ROLLING INTO THE HOST AT WALKING PACE DOESN'T KILL", touched and m["deadlyFrames"] == 0,
                    ("" if touched else f"INVALID: the car never reached the host (top speed {m['vMax']}, control {c['vMax']}) ") +
                    f"dv max {m['dvMax']} (threshold {m['threshold']}; control, own car: {c['dvMax']}, deadly frames {c['deadlyFrames']}) {m['near']}")
            out(L); time.sleep(1)

        if "7" in ONLY or "7b" in ONLY:
            # an item swept through the host: the laptop grabs a lamp lying on the far side of the host player
            hp = h("gpos"); gx, gz = hp["gx"] + 12, hp["gz"] + 12
            h(f"tpground {gx} {gz}"); L(f"tpground {gx + 3} {gz}"); time.sleep(2)
            lb = set(ents(L)); L(f"devspawn {ITEM}")
            l7 = until(lambda: next((n for n, e in ents(L).items() if n not in lb and not e["car"] and ITEM in e["name"]), None), 20)
            until(lambda: l7 in ents(h), 20); time.sleep(1)
            lp7 = ents(h)[l7]["pos"]; dx, dz = lp7[0] - gx, lp7[2] - gz; n_ = max(0.01, math.hypot(dx, dz))
            L(f"tpground {gx - dx / n_ * 1.5} {gz - dz / n_ * 1.5}"); time.sleep(2.5)   # the host right between the laptop and the lamp
            print(f"  lamp {n_:.1f} m from the host, laptop 1.5 m on the other side", flush=True)
            h("mount deathwatch reset")
            print("  laptop grabs the lamp across the host:", L(f"mp pickupnet {l7}"), flush=True); time.sleep(3)
            dw = h("mount deathwatch"); print("  host player:", dw, flush=True)
            verdict("7b A LAMP GRABBED ACROSS THE HOST DOESN'T HURT THEM", dw["deadlyFrames"] == 0, f"dv max {dw['dvMax']} (threshold {dw['threshold']}) {dw['near']}")
            # thrown at the host, hard: in single player a 1 kg lamp nudges a player; a copy is kinematic until it's ours
            L(f"tpground {gx - 2.5} {gz}"); time.sleep(2)
            print("  laptop picks it up again (the teleport dropped it):", L(f"mp pickupnet {l7}"), flush=True); time.sleep(2)
            h("mount deathwatch reset")
            print("  laptop throws it at the host:", L(f"mp throwpoint {gx} {gz} 1"), flush=True); time.sleep(3)
            dw = h("mount deathwatch"); print("  host player:", dw, flush=True)
            verdict("7c A LAMP THROWN HARD AT THE HOST DOESN'T KILL", dw["deadlyFrames"] == 0, f"dv max {dw['dvMax']} (threshold {dw['threshold']}) {dw['near']}")

        if "7d" in ONLY or "7" in ONLY:
            # riding on top of a car: first the host's own (control), then the laptop's (a copy here, moved by states)
            def ride(who, tag):
                fr = h(f"mount roof {net}"); time.sleep(2.5)
                h("mount deathwatch reset"); h(f"mount ridetrace {net} 9")
                # held at 1 -> 3 m/s for 5 s (one launch coasts to a stop in half a second: the host slid off the roof)
                for k in range(12):
                    v = min(3, 1 + k)
                    try: (h if who is h else L)(f"crash launch {v} {fr['carGx']} {fr['carGz']}")
                    except RuntimeError as ex:   # the driver's car went to another machine mid-ride: who took it, why
                        print(f"  LAUNCH {k} FAILED: {ex}\n  owner log: {h(f'mp ownerlog {net}')}", flush=True)
                        break
                    time.sleep(0.4)
                time.sleep(0.5)
                dw = h("mount deathwatch"); hp = h("gpos"); ec = ents(h)[net]
                print("  ride trace:\n    " + h("mount ridelog")["log"][:5000].replace("\n", "\n    "), flush=True)
                dw["offCar"] = round(dist([hp['gx'], hp['gz']], [ec['pos'][0], ec['pos'][2]]), 2)
                print(f"  {tag}: host player {dw}; host {dw['offCar']} m from the car (flat)", flush=True)
                (h if who is h else L)(f"crash launch 0 {ec['pos'][0]} {ec['pos'][2]} hold")
                time.sleep(2)
                return dw
            if ents(h)[net]["owner"] != 0:   # the host takes the wheel once (the laptop's exit first: a seat shows taken while it's in flight)
                out(L); time.sleep(2)
                for _k in range(3):
                    try: h(f"mp sitdriver {net}")
                    except RuntimeError: pass
                    time.sleep(1.5)
                    if ents(h)[net]["owner"] == 0: break
                if not until(lambda: ents(h)[net]["owner"] == 0, 10): sys.exit(f"the host could not take car {net} back")
                out(h); time.sleep(1)
            if not flat_park(h, net): sys.exit("step 7d: no flat ground for the car")
            c = ride(h, "control: on the host's own car")
            seat_laptop(h, L, net)   # the laptop drives it (out of the car since 7a)
            m = ride(L, "on the laptop's car (a copy here)")
            rode = m["vMax"] > 1.0 and c["vMax"] > 1.0   # carried along (else it says nothing; offCar is from the car's pivot)
            verdict("7d RIDING ON THE LAPTOP'S CAR DOESN'T KILL THE HOST", rode and m["deadlyFrames"] == 0,
                    ("" if rode else f"INVALID: the host didn't ride it (top speed {m['vMax']}; control {c['vMax']}) ") + f"dv max {m['dvMax']} (threshold {m['threshold']}; control, own car: {c['dvMax']}, deadly {c['deadlyFrames']}) {m['near']}")
            out(L); time.sleep(1)

        if "8" in ONLY:
            for s_ in (h, L):
                try: s_("driveout")
                except Exception: pass
            # bug 8, "engine state can get unsynced": the host walks 250 m away (its game freezes the car: no states), the
            # laptop stands by the car; the host switches the ignition on and off — each change must reach the laptop
            cp = ents(h)[net]["pos"]
            L(f"tpground {cp[0] + 5} {cp[2] + 5}"); h(f"tpground {cp[0] + 250} {cp[2]}"); time.sleep(4)
            t0 = time.time(); last = ents(h)[net]["seqOut"]; since = time.time(); rest = None
            while time.time() - t0 < 40:
                time.sleep(0.5); s8 = ents(h)[net]["seqOut"]
                if s8 != last: last, since = s8, time.time()
                elif time.time() - since >= 3: rest = s8; break
            e8 = ents(h)[net]
            print(f"  car at rest on the host (no states for 3 s): {rest is not None}; owner {e8['owner']}; laptop sees {L(f'mp carsig {net}')}", flush=True)
            # (the near player becomes the owner: proximity hand-off) the host comes back to watch from 12 m
            h(f"tpground {cp[0] - 8} {cp[2] - 9}"); time.sleep(3)
            o = ents(h)[net]["owner"]; O, V, on, vn = (L, h, "laptop", "host") if o == 1 else (h, L, "host", "laptop")
            print(f"  owner now: {on}", flush=True)
            for v, tag in ((1, "on"), (0, "off")):
                print(f"  the {on} (owner) switches the ignition {tag}:", O(f"mp carset {net} ignition {v}"), flush=True)
                ok = until(lambda: V(f"mp carsig {net}")["ignition"] == bool(v), 5)
                verdict(f"8{'a' if v else 'b'} THE PARKED CAR'S IGNITION {tag.upper()} REACHES THE {vn.upper()}", bool(ok), f"{vn} {V(f'mp carsig {net}')}")
            h(f"tpground {cp[0] + 4} {cp[2] - 4}"); time.sleep(2)

        if "9" in ONLY:
            for s_ in (h, L):
                try: s_("driveout")
                except Exception: pass
            # leaving and joining again: the laptop drops a lamp of its own, leaves, joins again — the lamp is still
            # there, once, where it was; the car once; nothing doubled on either machine
            cp = ents(h)[net]["pos"]
            L(f"tpground {cp[0] + 6} {cp[2] - 6}"); time.sleep(2)
            lb = set(ents(L)); L(f"devspawn {ITEM}")
            l9 = until(lambda: next((n for n, e in ents(L).items() if n not in lb and not e["car"] and ITEM in e["name"]), None), 20)
            until(lambda: l9 in ents(h), 20); time.sleep(3)
            lp9 = ents(h)[l9]["pos"]
            def census(s_, around):
                es = s_("mp entities")["entities"]
                return {"lamps": sum(1 for e in es if ITEM in e["name"] and dist(e["pos"], around) < 30),
                        "cars": sum(1 for e in es if e["car"] and dist(e["pos"], cp) < 30), "all": len(es)}
            before_h = census(h, lp9)
            g_before = h(f"mount census {cp[0]} {cp[2]} 40 byid")
            print(f"  before: host {before_h}, laptop {census(L, lp9)}; the laptop's lamp {l9} at {lp9}", flush=True)
            print("  laptop leaves:", L("mp stop"), flush=True); time.sleep(5)
            e9 = ents(h).get(l9)
            print(f"  host after the leave: lamp {e9 and {k: e9[k] for k in ('owner', 'proxy', 'pos', 'dormant')}}; {census(h, lp9)}", flush=True)
            verdict("9a THE LAMP STAYS IN THE WORLD WHEN ITS OWNER LEAVES, THE HOST'S", e9 is not None and e9["owner"] == 0 and not e9["proxy"], f"{e9 and e9['owner']}")
            print("  laptop joins again:", L(f"mp joinid {h('net me')['steamId']}"), flush=True)
            rj = until(lambda: L("mp status")["client"]["ready"], 150)
            time.sleep(8)
            L(f"tpground {cp[0] + 6} {cp[2] - 6}"); time.sleep(6)
            ch, cl = census(h, lp9), census(L, lp9)
            print(f"  after the rejoin ({bool(rj)}): host {ch}, laptop {cl}", flush=True)
            lamps_l = [e for e in L("mp entities")["entities"] if ITEM in e["name"] and dist(e["pos"], lp9) < 30]
            print("  laptop's lamps near:", [(e["net"], e["owner"], e["proxy"], [round(x, 2) for x in e["pos"]]) for e in lamps_l], flush=True)
            at_lamp = [e for e in lamps_l if dist(e["pos"], lp9) < 1]
            gh, gl = h(f"mount census {lp9[0]} {lp9[2]} 1.5 byid"), L(f"mount census {lp9[0]} {lp9[2]} 1.5 byid")
            print(f"  game objects within 1.5 m of the lamp: host {gh}, laptop {gl}", flush=True)
            verdict("9b AFTER THE REJOIN THE LAMP IS THERE ONCE, IN PLACE", len(at_lamp) == 1 and gl == gh,
                    f"laptop: {len(at_lamp)} shared lamp(s) at the spot; objects there host {gh} laptop {gl}")
            import re as _re
            def norm(g):   # built-in objects keep their scene names ("Beer (12)"), copies are named after the prefab
                o = {}
                for k, v in g.items():
                    k2 = _re.sub(r" \(\d+\)$", "", k); k2 = _re.sub(r"Full$", "", k2); o[k2] = o.get(k2, 0) + v
                return o
            g_h, g_l = h(f"mount census {cp[0]} {cp[2]} 40 byid"), L(f"mount census {cp[0]} {cp[2]} 40 byid")   # by prefab id (save id): names differ
            diff = {k: (g_h.get(k), g_l.get(k)) for k in set(g_h) | set(g_l) if g_h.get(k) != g_l.get(k)}
            print(f"  game objects within 40 m: host {sum(g_h.values())} (before {sum(g_before.values())}), laptop {sum(g_l.values())}; differing: {diff}", flush=True)
            for k in diff:   # where each one is, on each machine (parented ones too: the census counts only loose ones)
                print(f"    {k} host  :", h(f"mount census {cp[0]} {cp[2]} 40 byid {k}")["rows"], flush=True)
                print(f"    {k} laptop:", L(f"mount census {cp[0]} {cp[2]} 40 byid {k}")["rows"], flush=True)
            verdict("9d THE SAME GAME OBJECTS AROUND ON BOTH MACHINES AFTER THE REJOIN", not diff, f"{diff}")
            verdict("9c NOTHING DOUBLED: CARS AND ENTITIES ON BOTH MACHINES", cl["cars"] == before_h["cars"] and ch["cars"] == before_h["cars"] and ch["all"] == before_h["all"],
                    f"cars host {ch['cars']} laptop {cl['cars']} (were {before_h['cars']}); entities host {ch['all']} (were {before_h['all']}) laptop {cl['all']}")

        if "9e" in ONLY or "9" in ONLY:
            # leaving with the lamp in hand: on the host the copy was "held" (collides with nothing) — the holder gone, it
            # must be a normal object again: the host's, colliding, on the ground (not floating where the hands were)
            cp = ents(h)[net]["pos"]
            L(f"tpground {cp[0] + 7} {cp[2] + 7}"); h(f"tpground {cp[0] + 10} {cp[2] + 10}"); time.sleep(2)
            lb = set(ents(L)); L(f"devspawn {ITEM}")
            le = until(lambda: next((n for n, e in ents(L).items() if n not in lb and not e["car"] and ITEM in e["name"]), None), 20)
            until(lambda: le in ents(h), 20); time.sleep(1)
            print("  laptop picks it up:", L(f"mp pickupnet {le}"), flush=True); time.sleep(2)
            print("  host sees:", {k: ents(h)[le][k] for k in ("owner", "proxy", "pos")}, h(f"mount probe {le}"), flush=True)
            print("  laptop leaves holding it:", L("mp stop"), flush=True); time.sleep(6)
            pe = h(f"mount probe {le}"); ee = ents(h).get(le)
            print(f"  host after: {ee and {k: ee[k] for k in ('owner', 'proxy', 'pos')}} {pe}", flush=True)
            verdict("9e A LAMP HELD BY A LEAVING PLAYER BECOMES A NORMAL OBJECT ON THE HOST", ee is not None and ee["owner"] == 0 and not ee["proxy"] and pe.get("rb") and pe["rb"].get("det") and not pe["rb"].get("kin") and "0.0" in pe.get("under", "") or (pe.get("under", "").split(" at ")[-1].split(" ")[0] < "0.5"),
                    f"{pe}")
            print("  laptop joins again:", L(f"mp joinid {h('net me')['steamId']}")["client"]["ready"], flush=True)
            until(lambda: L("mp status")["client"]["ready"], 150); time.sleep(8)

        if "9f" in ONLY or "9" in ONLY:
            # leaving while driving one's own car: the host takes it over — it rolls out, it doesn't fly or vanish
            cp = ents(h)[net]["pos"]
            h(f"tpground {cp[0] + 12} {cp[2] + 12}")
            L(f"tpground {cp[0] + 4} {cp[2] + 4}"); time.sleep(2)
            L(f"mp sitdriver {net}"); time.sleep(1.5); L(f"mp sitdriver {net}")
            until(lambda: ents(h)[net]["owner"] == 1, 10)
            for v in (2, 4, 5): L(f"crash launch {v}"); time.sleep(0.5)
            time.sleep(1); h("crash rec start 40"); time.sleep(0.3)
            p0 = ents(h)[net]["pos"]
            print("  laptop leaves while driving:", L("mp stop"), flush=True); time.sleep(7)
            rc = h("crash rec stop"); car = next((c for c in rc["cars"] if c.get("net") == net), {})
            ef = ents(h).get(net)
            print(f"  host after: owner {ef and ef['owner']} proxy {ef and ef['proxy']} moved {round(dist(ef['pos'], p0), 1) if ef else None} m; damage {car.get('damageMaxV')} spin {car.get('peakSpinDeg')} rise {car.get('riseM')} upY {car.get('endUpY')} parts lost {car.get('partsLost')}", flush=True)
            verdict("9f A CAR WHOSE DRIVER LEAVES MID-DRIVE IS THE HOST'S, ROLLS OUT, NOT HURT", ef is not None and ef["owner"] == 0 and not ef["proxy"] and dist(ef["pos"], p0) < 60 and (car.get("damageMaxV") or 0) < 10 and (car.get("partsLost") or 0) == 0,
                    f"moved {round(dist(ef['pos'], p0), 1) if ef else None} m, damage {car.get('damageMaxV')}, parts lost {car.get('partsLost')}")
            print("  laptop joins again:", L(f"mp joinid {h('net me')['steamId']}")["client"]["ready"], flush=True)
            until(lambda: L("mp status")["client"]["ready"], 150); time.sleep(5)

        if "10" in ONLY:
            for s_ in (h, L):
                try: s_("driveout")
                except Exception: pass
            # sleeping: the laptop sleeps (the game races its clock for 2 x sleepRealTime); when it wakes everyone's
            # clock jumps with it — and stays there through the server's periodic clock (every 10 s)
            def clock(s_): return s_("get napszakvaltakozas.s.t")["value"]
            day = h("get napszakvaltakozas.s.dt")["value"] + h("get napszakvaltakozas.s.nt")["value"]
            def gap(a, b): d = (a - b) % day; return min(d, day - d)
            print(f"  clocks before: host {clock(h):.0f} laptop {clock(L):.0f} (day {day:.0f})", flush=True)
            L(f"set {A}.TestSleep True")
            slept = until(lambda: L("get mainscript.s.sleeping")["value"], 15)
            L(f"set {A}.TestSleep False")
            t_sleep = clock(h)
            print(f"  laptop asleep: {slept}; host clock {t_sleep:.0f}", flush=True)
            woke = until(lambda: not L("get mainscript.s.sleeping")["value"], 90)
            time.sleep(2)
            ch, cl = clock(h), clock(L)
            print(f"  laptop woke ({bool(woke)}): host {ch:.0f} laptop {cl:.0f}; the host's clock moved {gap(ch, t_sleep):.0f}", flush=True)
            verdict("10a THE LAPTOP'S SLEEP MOVES THE HOST'S CLOCK TOO", gap(ch, cl) < 20 and gap(ch, t_sleep) > 60, f"host {ch:.0f} laptop {cl:.0f}, host moved {gap(ch, t_sleep):.0f}")
            time.sleep(12)
            ch, cl = clock(h), clock(L)
            verdict("10b AND THE CLOCKS STAY TOGETHER THROUGH THE SERVER'S PERIODIC CLOCK", gap(ch, cl) < 20, f"host {ch:.0f} laptop {cl:.0f}")

    finally:
        for s_ in (h, L):
            for k, v in ((f"{A}.TestHum", False), (f"{A}.TestPiss", False), (f"{A}.TestSleep", False), (f"{T}.TestAsPlayer", False), ("TLDRevamp.Fixes.StreamRace.Enabled", True)):
                try: s_(f"set {k} {v}")
                except Exception as ex: print("restore", k, ex)
            try: s_("driveout")
            except Exception: pass
        for name, (s_, v) in plocks.items():
            try: s_(f"set settingsscript.s.S.IPhysicsLock {v}")
            except Exception as ex: print("physics lock restore", name, ex)
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        ERRS = {}
        for name, s_ in (("host", h), ("laptop", L)):   # what the games logged as errors during the run (Diagnostics.ErrorLog)
            try: ERRS[name] = s_("errors")
            except Exception as ex: ERRS[name] = {"error": str(ex)}
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
L.close()
for name, e_ in ERRS.items():
    print(f"\nERRORS on the {name}: {e_.get('errors')} errors, {e_.get('exceptions')} exceptions", flush=True)
    for k in e_.get("kinds", [])[:25]: print(f"   x{k['n']:<5} {k['what'][:300]}", flush=True)
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
