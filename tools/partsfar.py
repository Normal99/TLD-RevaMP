#!/usr/bin/env python3
"""A friend's car back in view with parts missing (playtest 2026-10-09/10, v0.66.7). Client 2's car lost parts in crashes,
client 2 bolted them back; the host's copy of the car came back with ~20 parts missing, and on client 1 the interior
parts (sun visor, radio, front mirror) sat in the copy's CONTAINER and were then "taken out" by the v0.66.7 release rule
("its owner has it 776-2169 m away": a bolted part sends no states, its last pose is where it was last loose).
Two machines over the relay: the laptop owns a car01; the host watches it.
  near: an interior part off on the laptop; its copy lands in the host's copy's container (`mp copystore`: what the copy's
        own scan did in the field); the laptop bolts it back at the car, then takes the car 1 km on; the host walks up.
  far : the part off, its copy stored on the host; the laptop takes the car and the part 1 km on (the host's copy of the
        car is stored away) and bolts it back there; the host walks up.
  Every cycle: the car's parts on both machines (`mp carparts`), the part's copy state on the host.
  tools/partsfar.py [--no-build] [--relay iad|off] [--modes near,far] [--old: the release rule as in v0.66.7]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import CAR01

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
MODES = arg("--modes", "near,far").split(",")
T = "TLDRevamp.Net.Entities"; FAR = 1000
INTERIOR = ("Napellenzo", "Radio", "FrontMirror"); USED = set()   # a part once off is an object of its own: each cycle another
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

def travel(s, gx, gz, who, settle=4):
    s("set mainscript.s.player.godModeTillGrounded True")
    s(f"tpg {gx} {gz} hold")
    def grounded():
        q = s("gpos"); return "y" in s(f"ground {q['ux']} {q['uz']}")
    if not until(grounded, 90, 1.0):
        s(f"tpg {gx + 300} {gz} hold"); until(grounded, 60, 1.0); s(f"tpg {gx} {gz} hold")
        if not until(grounded, 90, 1.0): s("tprelease"); raise RuntimeError(f"{who}: no ground at {gx:.0f},{gz:.0f}")
    r = s(f"tpground {gx} {gz}")
    if r.get("tpground") == "hop": until(grounded, 30, 1.0); s(f"tpground {gx} {gz}")
    time.sleep(settle)
    q = s("gpos"); print(f"  {who} at {q['gx']:.0f},{q['gz']:.0f}", flush=True)

def census(label, car):
    ref, got = L(f"mp carparts {car}"), h(f"mp carparts {car}")
    if got.get("dormant") or "error" in got: verdict(label, False, f"host copy {got}"); return
    rp, gp = set(ref["parts"]), set(got["parts"])
    verdict(label, rp == gp, f"parts {len(rp)} vs {len(gp)}: missing on the host {sorted(rp - gp)}, extra {sorted(gp - rp)}")

def part_state(s, net, car):
    e = ent(s, net) or {}
    try: pin = s(f"mp partin {net} {car}")
    except RuntimeError as ex: pin = {"error": str(ex)}
    return {k: e.get(k) for k in ("dormant", "attached", "parent", "stored", "proxy", "pos")} | {"inCar": pin.get("inCar"), "slot": pin.get("slotIdx")}

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if OLD:
            for s_ in (h, L): s_(f"set {T}.CopyLockSafe False"); s_(f"set {T}.CopyLockByContainer False"); s_(f"set {T}.DriveClaimRootOnly False")
        print(f"  {'OLD (v0.66.7 release)' if OLD else 'CURRENT BUILD'}", flush=True)
        time.sleep(5)
        lid = L("mp entities")["me"]
        hp = h("gpos"); L(f"tpground {hp['gx'] + 3} {hp['gz'] + 3}"); time.sleep(3)
        before = set(ents(L))
        print("  laptop's car:", L(f"spawncarg {CAR01} 0 {hp['gx'] + 12} {hp['gz'] + 12} 0"), flush=True)
        car = until(lambda: next((n for n, e in ents(L).items() if n not in before and e["car"] and e["owner"] == lid), None), 20)
        if not car: sys.exit("the laptop's car was not shared")
        until(lambda: (ent(h, car) or {}).get("dormant") is False, 30); time.sleep(5)
        census("SETUP: THE HOST'S COPY HAS THE CAR'S PARTS", car)
        home = ent(L, car)["pos"]

        for k, mode in enumerate(MODES, 1):
            tag = f"{mode.upper()}{k}"
            parts = L(f"mp attachedparts {car}")["parts"]
            pp = next((p for p in parts if any(n in p["name"] for n in INTERIOR) and p["name"].split("(")[0] not in USED), None)
            if not pp: verdict(f"{tag} SETUP: AN INTERIOR PART ON THE CAR", False, str([p["name"] for p in parts])); break
            USED.add(pp["name"].split("(")[0]); lb = set(ents(L))
            print(f"  {tag}: {pp['name']} off: {L(f'mp falloff {car} {pp['i']}')}", flush=True)
            part = until(lambda: next((n for n, e in ents(L).items() if n not in lb and pp["name"].split('(')[0] in e["name"]), None), 15)
            if not part: verdict(f"{tag} SETUP: THE PART BECAME AN OBJECT OF ITS OWN", False); break
            until(lambda: (ent(h, part) or {}).get("dormant") is False, 20); time.sleep(4)
            print(f"  {tag}: part {part}; host's copy after the fall: {part_state(h, part, car)}", flush=True)
            print(f"  {tag}: [host] its copy into the copy of the car: {h(f'mp copystore {part} {car}')}", flush=True)
            time.sleep(3)
            def drive_away(what):
                # the owner takes its car (and what goes with it) 1 km on and goes with it — the host's copy is stored away
                p0 = (ent(L, part) or {}).get("pos")
                print(f"  {tag}: laptop moves the car {FAR} m: {L(f'mp moveitem {car} {FAR} 0')}", flush=True)
                if what != "the car":   # the part: in the laptop's car container it went along (the game stores what falls in)
                    p1 = (ent(L, part) or {}).get("pos")
                    came = p0 and p1 and abs(p1[0] - p0[0] - FAR) < 5
                    print(f"  {tag}: the part {'came along in the car' if came else 'stayed: moved too ' + str(L(f'mp moveitem {part} {FAR} 0'))}", flush=True)
                cp_ = ent(L, car)["pos"]
                travel(L, cp_[0] + 4, cp_[2] + 4, f"{tag} laptop (with its car)", settle=3)
                for n in ([car] if what == "the car" else [car, part]):
                    if not until(lambda: (ent(L, n) or {}).get("dormant") is False, 30): print(f"  {tag}: laptop: {n} not placed", flush=True)
                until(lambda: (ent(h, car) or {}).get("dormant") is True, 40); time.sleep(3)
            if mode == "near":
                print(f"  {tag}: laptop bolts it back at the car: {L(f'mp attachto {part} {car} -1')}", flush=True)
                time.sleep(5)
                print(f"  {tag}: host's copy after the bolt: {part_state(h, part, car)}", flush=True)
                census(f"{tag}: BOLTED BACK NEXT TO THE HOST — THE HOST'S COPY HAS THE CAR'S PARTS", car)
                drive_away("the car")
            else:
                drive_away("the car and the part")
                def bolt():
                    try: return L(f"mp attachto {part} {car} -1")
                    except RuntimeError as ex: return None
                r_ = until(bolt, 40, 2.0)
                pe_, ce_ = ent(L, part) or {}, ent(L, car) or {}
                print(f"  {tag}: laptop bolts it back there (the host's copy stored away): {r_}; part {pe_.get('dormant')} {pe_.get('pos')}, car {ce_.get('pos')}", flush=True)
                if not r_: verdict(f"{tag} SETUP: THE LAPTOP BOLTED THE PART BACK", False); break
                time.sleep(5)
            verdict(f"{tag}: THE HOST'S COPY WAS STORED AWAY MEANWHILE", (ent(h, car) or {}).get("dormant") is True)
            cp = ent(L, car)["pos"]
            travel(h, cp[0] - 20, cp[2] - 20, f"{tag} host (to the car)")
            placed = until(lambda: (ent(h, car) or {}).get("dormant") is False, 30)
            verdict(f"{tag}: THE HOST PLACED ITS COPY OF THE CAR", bool(placed))
            time.sleep(8)   # the release rule acts after 1 s; the anomaly census after 6 s
            print(f"  {tag}: host's copy of the part: {part_state(h, part, car)}; released so far {h(f'get {T}.CopyLocksReleased')['value']}", flush=True)
            census(f"{tag}: {FAR} M AWAY AND BACK — THE HOST'S COPY HAS THE CAR'S PARTS", car)
            try:
                a, b = L(f"mp relpos {car} {part}")["items"].get(str(part)), h(f"mp relpos {car} {part}")["items"].get(str(part))
            except RuntimeError as ex: a = b = None; print("  relpos:", ex, flush=True)
            verdict(f"{tag}: THE PART WHERE THE OWNER HAS IT IN THE CAR (host's copy, car frame)", a is not None and b is not None and math.dist(a, b) < 0.2,
                    f"laptop {a}, host {b}")
            an = h("mp anomalies"); print(f"  {tag}: host anomalies {an['count']} {an['lines'][-3:]}", flush=True)
            # home for the next cycle: the car back with the laptop, the host back
            L(f"mp moveitem {car} {-FAR} 0"); time.sleep(1)
            travel(L, home[0] + 4, home[2] + 4, f"{tag} laptop (home)", settle=3)
            travel(h, home[0] - 10, home[2] - 10, f"{tag} host (home)")
            until(lambda: (ent(h, car) or {}).get("dormant") is False, 30); time.sleep(4)
        print("  errors:", [s_("errors")["errors"] for s_ in (h, L)], flush=True)
    finally:
        for s_ in (h, L):
            try: s_(f"set {T}.CopyLockSafe True"); s_(f"set {T}.CopyLockByContainer True"); s_(f"set {T}.DriveClaimRootOnly True")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"PARTSFAR: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")
