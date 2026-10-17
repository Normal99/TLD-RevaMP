#!/usr/bin/env python3
"""Items falling through the world at a building (single machine, seed 20260924). leasepair runs 2+3: at the
Vizmu + OldHouse pair (4811,1762) 2-3 RubberWoman (model 82) ended ~255 m below ground on the lease holder (the
client) and never reached the host. Here: no multiplayer - arrive like a driver (600 m short first), then watch every
item near the site: y now vs y a moment ago, anything below the ground under it.
  --vanilla: every mod fix off (drivebench.ALL_FIXES) - is it the game's own bug?
  --off A,B: only these fixes off (names under TLDRevamp.Fixes, e.g. ItemSpawnSpread,UnfreezeOnGround)
  --trace [name]: timeline of ragdolls (or of items whose name contains `name`) (bridge `ragdolls`) from the moment the player is sent to the site
  --raydown 'gx,gy,gz;gx,gy,gz': every collider straight down (front faces / with backfaces)
  --spawnpoints gx gz: list the item spawn points within 3 m of that global point (after the watch)
  tools/fallprobe.py [--no-build] [--trace] [--site 4811 1762] [--r 110] [--watch 40] [--vanilla | --off A,B]"""
import sys, time, math, collections, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge

def arg(name, n=1, conv=float, default=None):
    if name not in sys.argv: return default
    i = sys.argv.index(name); v = [conv(x) for x in sys.argv[i + 1:i + 1 + n]]
    return v if n > 1 else v[0]
SX, SZ = arg("--site", 2, float, [4811.0, 1762.0])
R, WATCH = arg("--r", default=110.0), arg("--watch", default=40.0)
TRACE = arg("--trace", conv=str, default="") if "--trace" in sys.argv and sys.argv.index("--trace") + 1 < len(sys.argv) and not sys.argv[sys.argv.index("--trace") + 1].startswith("--") else ""

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def travel(b, gx, gz, who, settle=8):
    b("set mainscript.s.player.godModeTillGrounded True")
    b(f"tpg {gx} {gz} hold")
    def grounded():
        g = b("gpos"); return "y" in b(f"ground {g['ux']} {g['uz']}")
    ok = until(grounded, 90, 1.0)
    if not ok: b("tprelease")
    else:
        r = b(f"tpground {gx} {gz}")
        if r.get("tpground") == "hop": until(grounded, 30, 1.0); b(f"tpground {gx} {gz}")
    time.sleep(settle)
    g = b("gpos"); print(f"  {who} at {g['gx']:.0f},{g['gz']:.0f} y {g['gy']:.1f}", flush=True)

if "--no-build" not in sys.argv: game.build()
game.quit_game(); game.launch()
with game.ProtectedSession():
    seeded.restart_same_seed()
    b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True")
    god = b("get kaposztaleves.s.settings.god").get("value"); b("set kaposztaleves.s.settings.god True")
    offs = []
    if "--vanilla" in sys.argv:
        from drivebench import ALL_FIXES
        offs = list(ALL_FIXES)
    elif "--off" in sys.argv:
        offs = [f"TLDRevamp.Fixes.{n}.Enabled" for n in sys.argv[sys.argv.index("--off") + 1].split(",")]
    was = {}
    for f in offs:
        try: was[f] = b(f"get {f}").get("value"); b(f"set {f} False")
        except Exception as ex: print("  (no switch", f, ex, ")")
    print(f"  fixes off: {len(was)}", flush=True)
    travel(b, SX + 600, SZ, "approach")
    if "--trace" in sys.argv:
        # ragdoll timeline from the moment the player is sent to the site: root state + lowest limb, every change
        b("set mainscript.s.player.godModeTillGrounded True"); b(f"tpg {SX + 8} {SZ + 8} hold")
        t0 = time.time(); last = {}; placed = False
        while time.time() - t0 < WATCH:
            if not placed:
                try:
                    g = b("gpos")
                    if "y" in b(f"ground {g['ux']} {g['uz']}"): b(f"tpground {SX + 8} {SZ + 8}"); placed = True; print(f"  {time.time() - t0:5.1f}s player placed", flush=True)
                except Exception: pass
            for r in b(f"ragdolls {TRACE}")["items"]:
                gp = [float(x) for x in r["pos"].strip("()").split(",")]
                if math.hypot(gp[0] - SX, gp[2] - SZ) > R: continue
                limbs = [l for l in r["limbs"] if l != "null"]
                low = min(limbs, key=lambda l: l[1]) if limbs else None
                states = "".join(sorted(set(l[2][0] for l in limbs)))
                key = (r["rb"], states, r["frozen"], round(r["y"] / 5), round(low[1] / 5) if low else 0, tuple(r.get("cols", [])), r.get("parent"), r.get("buried"))
                if last.get(r["id"]) != key:
                    last[r["id"]] = key
                    print(f"  {time.time() - t0:5.1f}s #{r['id']} {r['name'][:14]} root y {r['y']:7.1f} {r['rb']:5} vy {r['vy']:6.1f} frozen {r['frozen']} | "
                          f"cols {r.get('cols')} rbs {r.get('rbs')} parent {r.get('parent')} buried {r.get('buried')} layer {r.get('layer')} {r.get('mode')} | {len(limbs)} limbs {states} lowest {low[0] if low else '-'} y {low[1] if low else 0:7.1f} vy {low[3] if low else 0} joint {low[4] if low else '-'} parent {low[5] if low else '-'}", flush=True)
            time.sleep(0.25)
    else:
        travel(b, SX + 8, SZ + 8, "site", settle=1)
    first = {}; t0 = time.time(); low = {}
    while time.time() - t0 < WATCH:
        for it in b(f"mp sitestate {SX} {SZ} {R}")["items"]:
            key = (it[0], it[5], round(it[1]), round(it[3]))
            if it[2] < -50: low[(it[0], it[5])] = (round(time.time() - t0), [round(x, 1) for x in it[1:4]])
        time.sleep(2)
    if "--raydown" in sys.argv:
        i = sys.argv.index("--raydown")
        for pt in sys.argv[i + 1].split(";"):
            r = b(f"raydown {pt.replace(',', ' ')}")
            print(f"  raydown from {pt}:", flush=True)
            for k in ("front", "backfaces"): print(f"    {k}: {r[k]}", flush=True)
    if "--spawnpoints" in sys.argv:
        i = sys.argv.index("--spawnpoints"); px, pz = float(sys.argv[i + 1]), float(sys.argv[i + 2])
        sp = b(f"spawnpoints {px} {pz} 3")
        print(f"  spawn points within 3 m of {px},{pz}: {sp['count']}", flush=True)
        for q in sp["points"]: print(f"    {q['g']} chance {q['chance']} spawned {q['spawned']} active {q['active']} | {q['path'][-110:]} | {q['items'][:160]}", flush=True)
    v = b(f"mp sitestate {SX} {SZ} {R}")["items"]
    c = collections.Counter(it[0] for it in v)
    print(f"  after {WATCH:.0f} s: {len(v)} items; model 82: {[[round(x, 1) for x in it[1:4]] for it in v if it[0] == 82]}", flush=True)
    print(f"  below y -50 at any point: {low or 'none'}", flush=True)
    try: print("  stackedspawn:", b("stackedspawn"), flush=True)
    except Exception: pass
    print("VERDICT:", "FALLS IN SINGLE PLAYER" if low else "NOTHING FALLS IN SINGLE PLAYER", flush=True)
    b(f"set kaposztaleves.s.settings.god {god if god is not None else False}")
    for f, v in was.items(): b(f"set {f} {v}")
