#!/usr/bin/env python3
"""Players far apart (two machines over the Steam relay): does the far player's world load, does the host learn what
they did there, and is the place as it was left when someone else comes — live, and after a server restart?
  0 the laptop travels ~200 km out (the host stays home): ground, frame times there on both machines
  1 there it opens a building nobody visited (spawn lease) and changes things as their owner: a car's fuel, a part's
    condition, a part off the car, the car moved, a building item moved. Reference = the laptop's view of the site.
    It goes home: the site is now far from everyone.
  A live    : the desktop (host) travels there → its view vs the reference
  B restart : host saves, quits, reloads the save, hosts again; the laptop rejoins and travels there while the host
              stays home (another player stumbles upon it) → its view vs the reference
  C restart : then the desktop travels there → its view vs the reference
  --save-first : the host saves right after the far player leaves, BEFORE it goes there itself (A), and the restart
              loads that save — what reached the host from 200 km away, untouched by its own visit (the user's call)
Views: `mp sitestate` — every item within R of the site by model + global position + saved state (fuel, part
conditions, switches, attached), independent of network ids.
  tools/farworld.py [--no-build] [--km 200] [--relay iad|off] [--only 0,1,A,B,C] [--set Net.Entities.X=False,...] [--old66]"""
import sys, time, math, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP
from roadkit import CAR01

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
KM, RELAY, ONLY = arg("--km", 200.0, float), arg("--relay", "iad"), arg("--only", "0,1,A,B,C")
SAVE_FIRST = "--save-first" in sys.argv
TAG = " [saved before the host's visit]" if SAVE_FIRST else ""
if RELAY == "off": RELAY = None
R = 160.0
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
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def in_world(s):
    try: return s("get DataFromMenuScript.s.mainmenu")["value"] is False and s("get mainscript.s.player")["value"] is not None
    except Exception: return False

def travel(s, gx, gz, who, settle=10, approach=False):
    """teleport to a GLOBAL point and stand on generated ground; returns seconds until the ground was there.
    approach: arrive like a driver would — first 600 m short (outside the game's 250 m item placing range) until the
    ground and the buildings there are generated, then the last hop. A direct 200 km teleport lands before the
    building exists: the game places the stored items around it at once and unfreezes them within a second
    (itemPlaceRemoveScript, no ground check) — they fall through the missing floor (seen: 242 m, farworld run 4)."""
    if approach:
        travel(s, gx + 600, gz, who + " (approach)", settle=8)
    t0 = time.time()
    s("set mainscript.s.player.godModeTillGrounded True")
    s(f"tpg {gx} {gz} hold")   # held still until tpground (a drop from 120 m over the old height can start under the terrain)
    def grounded():
        g = s("gpos")
        return "y" in s(f"ground {g['ux']} {g['uz']}")
    ok = until(grounded, 90, 1.0)
    if not ok: s("tprelease")
    took = time.time() - t0
    if ok:
        r = s(f"tpground {gx} {gz}")
        if r.get("tpground") == "hop": until(grounded, 30, 1.0); s(f"tpground {gx} {gz}")
    time.sleep(settle)
    if not in_world(s): raise RuntimeError(f"{who}: left the world travelling to {gx:.0f},{gz:.0f}")
    if not ok: raise RuntimeError(f"{who}: no ground after 90 s at {gx:.0f},{gz:.0f}")
    g = s("gpos")
    off = math.hypot(g["gx"] - gx, g["gz"] - gz)
    print(f"  {who} at global {g['gx']:.0f},{g['gz']:.0f} (ground after {took:.0f} s)" + (f"  !! {off:.0f} m from where it was sent" if off > 200 else ""), flush=True)
    return took

def frames(sides, secs=15):
    for s in sides.values(): s("reset")
    time.sleep(secs)
    return {n: {k: s("stats")[k] for k in ("p50ms", "p99ms", "maxms", "frames")} for n, s in sides.items()}

def settle_items(s, gx, gz, secs=5, timeout=90):
    last, since, t = None, time.time(), time.time()
    while time.time() - t < timeout:
        c = s(f"mp sitestate {gx} {gz} {R}")["count"]
        if c != last: last, since = c, time.time()
        elif c > 0 and time.time() - since > secs: return c
        time.sleep(1)
    return last

AI_IDS = set()   # creature models (mp aiids): alive, they walk — not part of a site's state
def static(view):
    keep = [it for it in view["items"] if it[0] not in AI_IDS and "Ragdol" not in it[5]]
    return {"count": len(keep), "items": keep, "left_out": [it[5] for it in view["items"] if it not in keep]}

_SIDES = []   # (bridge, name): deaths are checked on both machines before every verdict
_DEATHS0 = {}
def deaths_now():
    out = {}
    for s, n in _SIDES:
        try: out[n] = int(s("get TLDRevamp.Diagnostics.DeathLog.Deaths")["value"])
        except Exception: pass
    return out

def compare(ref, got, label, tol=0.6, record=True):
    # a player who died at the site (the building's turrets, a fall) contaminates it: whatever else happened there
    # (crates shot apart, a ragdoll) is the death's, not the sync's — the verdict is INVALID (2026-10-09: the host died
    # during its A visit at a Turrethaz; two crates were gone)
    d = deaths_now()
    died = {n: d[n] - _DEATHS0.get(n, 0) for n in d if d[n] > _DEATHS0.get(n, 0)}
    _DEATHS0.update(d)
    if died: label = "INVALID (a player died: " + ", ".join(f"{n} +{k}" for n, k in died.items()) + ") " + label
    """match by model within tol m; then the saved state of each matched item must be the same.
    Live creatures and player ragdolls are left out (farworld run ho2: a building's creature walked 57 m and killed the
    far player — both 'misplaced' though nothing was wrong)."""
    ref, got = static(ref), static(got)
    if ref["left_out"] or got["left_out"]: print(f"  left out (alive / ragdolls): reference {ref['left_out']}, here {got['left_out']}", flush=True)
    left = list(got["items"])
    missing, moved, state = [], [], []
    for it in ref["items"]:
        best, bi = None, -1
        for i, o in enumerate(left):
            if o[0] != it[0]: continue
            d = math.dist(it[1:4], o[1:4])
            if best is None or d < best: best, bi = d, i
        if best is None or best > tol:
            if best is not None and best <= 5:
                o = left.pop(bi)
                moved.append((it[5], [round(o[k] - it[k], 2) for k in (1, 2, 3)], it[4], o[4]))
            else: missing.append((it[5], round(best, 2) if best is not None else None, it[4], [round(v, 1) for v in it[1:4]]))
            continue
        o = left.pop(bi)
        if o[4] != it[4]: state.append((it[5], it[4], o[4]))
    extra = [(o[5], round(o[1], 1), round(o[2], 1), round(o[3], 1)) for o in left]
    ok = not missing and not moved and not state and not extra
    (verdict if record else (lambda n, k, d: print(f"{'same' if k else 'DIFFERS'}  {n}  {d}", flush=True)))(label, ok, f"reference {ref['count']} items, here {got['count']}: missing {len(missing)}, misplaced {len(moved)}, "
                       f"state differs {len(state)}, extra {len(extra)}")
    for name, rows in (("missing", missing), ("misplaced", moved), ("state differs (reference → here)", state), ("extra", extra)):
        for r in rows[:40]: print(f"      {name}: {r}", flush=True)
    return ok

def farstore(label, sides, sx, sz):
    for n, s in sides.items():
        print(f"  far store [{label}] {n}: {s(f'mp farstore {sx} {sz} {R}')}, loaded {s(f'mp sitestate {sx} {sz} {R}')['count']}, "
              f"ground/origin {s(f'groundg {sx} {sz}')}, deaths {s('get TLDRevamp.Diagnostics.DeathLog.Deaths')['value']}, "
              f"died {s('get mainscript.s.player.died')['value']}", flush=True)

# --set A=v,B=v : TLDRevamp field values set on both machines before every session (re-applied after the restart);
# --old66 : every v0.66.x fix off (A/B)
OLD66 = ("Net.PoseInterpolator.FreshAfterIdle=False,Net.Entities.FarMoveAttached=False,Net.Entities.StoredAttach=False,"
         "Net.Entities.ClearHeldOnAttach=False,Net.Entities.ReshareSurvivors=False,Net.Entities.CopyContainersInert=False,"
         "Net.Entities.ScopedEdits=False,Net.Entities.CopyStartFreezeOff=False")
SETS = [kv.split("=") for kv in ((sys.argv[sys.argv.index("--set") + 1] if "--set" in sys.argv else "") + ("," + OLD66 if "--old66" in sys.argv else "")).split(",") if kv]

def host_and_join(b, L):
    for k, v in SETS:
        for s in (b, L): print(f"  set {k}={v}:", s(f"set TLDRevamp.{k} {v}"), flush=True)
    if RELAY: print("relay:", b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
    b("mp host 27070")
    if RELAY: L(f"mp joinid {b('net me')['steamId']}")
    else: L(f"mp join {DESKTOP_IP}:27070")
    if not until(lambda: L("mp status")["client"]["ready"], 180, 2): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY: print("route:", L("net route"), flush=True)
    time.sleep(5)

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        b = seeded_both(L)
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        _SIDES[:] = [(b, "desktop"), (L, "laptop")]; _DEATHS0.update(deaths_now())
        AI_IDS.update(x[0] for x in b("mp aiids")["ai"])
        if "--no-handoff" in sys.argv: print("hand-off OFF (A/B):", b("set TLDRevamp.Net.Entities.HandoffEnabled False"), flush=True)
        time.sleep(8)
        host_and_join(b, L)
        sides = {"desktop": b, "laptop": L}
        home = b("gpos"); lhome = L("gpos")
        print(f"home: desktop {home['gx']:.0f},{home['gz']:.0f}  laptop {lhome['gx']:.0f},{lhome['gz']:.0f}", flush=True)

        # ---------- 0: the laptop goes far out
        tx = home["gx"] + KM * 1000.0
        travel(L, tx, home["gz"], "laptop")
        rp = L("roadpoint")
        if "error" not in rp: travel(L, rp["gx"], rp["gz"], "laptop (on the road)", settle=8)
        f0 = frames(sides)
        g = L("gpos")
        far = math.hypot(g["gx"] - home["gx"], g["gz"] - home["gz"]) / 1000
        verdict("0 FAR PLAYER'S WORLD LOADS", in_world(L) and far > KM * 0.9,
                f"laptop {far:.0f} km from the host; frames there: laptop {f0['laptop']}, host {f0['desktop']}")

        # ---------- 1: a building nobody visited, things changed there as their owner
        site = None
        for n in range(8):
            g = L("gpos")
            pois = L(f"pois {g['ux']} {g['uz']} 3000")["pois"]
            fresh = [w for w in pois if not w["items"] and not w["fixed"]]
            print(f"  search {n}: at {g['gx']:.0f},{g['gz']:.0f}: pois {len(pois)}, fresh {[(w['name'][:12], int(w['d'])) for w in fresh[:3]]}", flush=True)
            if fresh:
                w = min(fresh, key=lambda w: w["d"])
                l0 = L(f"mp itemsnear {g['gx']} {g['gz']} 50")["leases"]
                travel(L, w["gx"] + 25, w["gz"] + 25, "laptop (building)", settle=4)
                spawned = until(lambda: L(f"mp itemsnear {w['gx']} {w['gz']} 50")["leases"]["spawns"] > l0["spawns"], 30)
                done = until(lambda: L(f"mp itemsnear {w['gx']} {w['gz']} 50")["leases"]["done"] > l0["done"], 20)
                print(f"  {w['name']}: lease spawn {spawned}, capture done {done}", flush=True)
                site = (w["gx"], w["gz"], w["name"])
                break
            travel(L, g["gx"] + 3000, g["gz"], f"laptop (search hop {n + 1})", settle=4)
        if site is None: sys.exit("no unvisited building found")
        sx, sz, sname = site
        n_items = settle_items(L, sx, sz)
        me = L("mp entities")["me"]
        print(f"  building items settled: {n_items}", flush=True)
        # a car of the laptop's at the building
        rp = L("roadpoint")
        cx, cz = (rp["gx"], rp["gz"]) if "error" not in rp and math.hypot(rp["gx"] - sx, rp["gz"] - sz) < R - 30 else (sx + 30, sz + 30)
        print("  car:", L(f"spawncarg {CAR01} 0 {cx} {cz} 0"), flush=True)
        car = None
        def find_car():
            nonlocal_car = [e for e in L("mp entities")["entities"] if e["car"] and e["owner"] == me and not e["dormant"]
                            and math.hypot(e["pos"][0] - cx, e["pos"][2] - cz) < 15]
            return nonlocal_car[0]["net"] if nonlocal_car else None
        until(lambda: find_car() is not None, 15)
        car = find_car()
        if car is None: sys.exit("the laptop's car was not shared")
        time.sleep(4)
        changes = []
        changes.append(("fuel 3.70 L", L(f"mp entpoke {car} p:fuel 3.7")))
        changes.append(("part 2 condition → 3", L(f"mp entpoke {car} p:part 2 3")))
        parts = L(f"mp attachedparts {car}")["parts"]
        pref = sorted(parts, key=lambda p: 0 if any(k in p["name"] for k in ("oor", "ood", "unk", "ender")) else 1)
        for p in pref:
            r = L(f"mp falloff {car} {p['i']}")
            if "error" not in r: changes.append((f"part off: {p['name']}", r)); break
        time.sleep(3)
        changes.append(("car moved 12 m", L(f"mp moveitem {car} 12 0")))
        own = [e for e in L("mp entities")["entities"] if e["owner"] == me and not e["car"] and not e["dormant"] and e["parent"] == 0 and not e["attached"]
               and math.hypot(e["pos"][0] - sx, e["pos"][2] - sz) < R - 20]
        if own: changes.append((f"building item {own[0]['name']} moved 3 m", L(f"mp moveitem {own[0]['net']} 3 0")))
        for c in changes: print("  change:", c[0], c[1], flush=True)
        time.sleep(10)   # resyncs go out, the car and the item come to rest
        ref = L(f"mp sitestate {sx} {sz} {R}")
        print(f"  reference (laptop at the site): {ref['count']} items; car state {L(f'mp entstate {car}')}", flush=True)
        json.dump({"site": site, "ref": ref, "changes": [c[0] for c in changes]},
                  open(pathlib.Path(__file__).resolve().parent.parent / "bench" / "farworld_ref.json", "w"))
        farstore("far player at the site", sides, sx, sz)
        hcar = next((e for e in b("mp entities")["entities"] if e["net"] == car), None)
        print(f"  host's copy of the car meanwhile: {hcar and {k: hcar.get(k) for k in ('dormant', 'pos', 'fuel', 'attached', 'proxy')}}", flush=True)
        travel(L, lhome["gx"], lhome["gz"], "laptop (home)")
        time.sleep(5)
        farstore("far player home again", sides, sx, sz)

        if SAVE_FIRST:   # (mp stop doesn't save in tests: BlockAutoSave — only this explicit save is reloaded)
            print("  mp save (before the host's visit):", b("mp save"), flush=True)
        # ---------- A: live — the host travels there
        if "A" in ONLY:
            travel(b, sx + 20, sz - 20, "desktop", approach=True)
            settle_items(b, sx, sz)
            time.sleep(5)
            compare(ref, b(f"mp sitestate {sx} {sz} {R}"), "A LIVE: THE HOST FINDS THE PLACE AS THE FAR PLAYER LEFT IT")
            travel(b, home["gx"], home["gz"], "desktop (home)")
            time.sleep(3)
            farstore("host home after its visit", sides, sx, sz)

        # ---------- server restart
        if "B" in ONLY or "C" in ONLY:
            if not SAVE_FIRST: print("  mp save:", b("mp save"), flush=True)
            L("mp stop"); time.sleep(2); b("mp stop"); time.sleep(3)
            game.quit_game()
            game.launch()
            b = game.loadsave("autosave1")
            b("set TLDRevamp.TestSafety.BlockAutoSave True")
            g = b("gpos")
            print(f"  host reloaded its save, at {g['gx']:.0f},{g['gz']:.0f}", flush=True)
            sides["desktop"] = b
            farstore("host reloaded the save", {"desktop": b}, sx, sz)
            host_and_join(b, L)
            farstore("laptop rejoined", {"laptop": L}, sx, sz)
            if "B" in ONLY:
                travel(L, sx + 20, sz + 20, "laptop (back to the site)", approach=True)
                settle_items(L, sx, sz)
                time.sleep(5)
                okB = compare(ref, L(f"mp sitestate {sx} {sz} {R}"), "B AFTER RESTART: ANOTHER PLAYER FINDS IT AS LEFT" + TAG)
                if not okB:
                    for w in ("Door", "Bumper", "Mirror", "Napellenzo", "Hood", "Engine", "Coolant", "Trunk", "Tire"):
                        for row in L(f"whereis {w}")["items"]:
                            print(f"      whereis {w}: {row}", flush=True)
                fB = frames(sides)
                print(f"  frames with the laptop there: {fB}", flush=True)
            if "C" in ONLY:
                travel(b, sx - 20, sz + 20, "desktop", approach=True)
                settle_items(b, sx, sz)
                time.sleep(5)
                hv = b(f"mp sitestate {sx} {sz} {R}")
                lv = L(f"mp sitestate {sx} {sz} {R}")   # the laptop is still there (B): what it has now
                okC = compare(ref, hv, "C AFTER RESTART: THE HOST FINDS IT AS LEFT" + TAG)
                if not okC:
                    print("  (info) the laptop's view now vs the reference — changed during its B visit:", flush=True)
                    compare(ref, lv, "  info: laptop now vs reference", record=False)
                compare(lv, hv, "C' SAME PLACE, SAME MOMENT: HOST VIEW = LAPTOP VIEW" + TAG)
        for n, s_ in (("desktop", b), ("laptop", L)):
            e = s_("mp entities")
            print(f"  {n}: building socket items stored {s_('poistore')}, empty tanks loaded empty {s_('get TLDRevamp.Fixes.EmptyTankLoad.Emptied')['value']}, free attach points {s_('get TLDRevamp.Fixes.FreeAttachPoints.Given')['value']}, unfreeze on ground {s_('unfreezeground')}, attach log {s_('attachlog')}, foreign refs dropped {s_('get TLDRevamp.Net.ItemSnapshot.ForeignRefsDropped')['value']}, "
                  f"far adds {e.get('farAdds')}, far resyncs {e.get('farResyncs')} ({e.get('farResyncItems')} items)", flush=True)
        b("mp stop"); L("mp stop")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")
