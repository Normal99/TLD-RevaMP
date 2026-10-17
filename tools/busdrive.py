#!/usr/bin/env python3
"""Bus01 with two players (relay iad by default). Its rear section (BusBack) is its own rigidbody on a ConfigurableJoint
to the front, outside the bus's hierarchy, with no tosaveitemscript of its own — the shared state moves the front only.
A: the host drives the bus along the road, the laptop rides as a passenger. B: they swap — the laptop drives, the host
rides. Every second both machines' rear-section pose IN THE BUS'S FRAME (bridge `mount bodies <net>`): the bend between
the halves doesn't depend on network delay, so a difference is a real desync. Also: the bus keeps moving on both.
  tools/busdrive.py [--no-build] [--relay iad|off] [--id 106] [--secs 20] [--rounds 3] [--no-store]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, tldenv
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
BUS = int(sys.argv[sys.argv.index("--id") + 1]) if "--id" in sys.argv else 106
SECS = int(sys.argv[sys.argv.index("--secs") + 1]) if "--secs" in sys.argv else 20
ROUNDS = int(sys.argv[sys.argv.index("--rounds") + 1]) if "--rounds" in sys.argv else 3
VERDICTS = []
FIRSTOFF = []

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

def ent(x, net):
    return next((e for e in x("mp entities")["entities"] if e["net"] == net), None)

def yawdiff(a, b):
    return abs((a - b + 180.0) % 360.0 - 180.0)

def seat(x, net, how):
    for attempt in range(6):
        r = x(f"mp {how} {net}")
        if "seat" in r and not r.get("stepped"): return r   # a first call steps beside the seat, the next one sits
        time.sleep(1.5)
    return r

SHOTS = []
def shoot(tag):
    """Both machines' screens (bridge `shot`: <game dir>/mpshot_<tag>.png), fetched at the end - look before probing."""
    if len(SHOTS) >= 6: return
    tag = "".join(c if c.isalnum() else "_" for c in tag)
    for s_ in (h, L):
        try: s_(f"shot bus_{tag}")
        except Exception as ex: print("  shot:", ex, flush=True)
    SHOTS.append(f"mpshot_bus_{tag}.png")

def fetch_shots():
    import subprocess, os, shutil
    out = pathlib.Path(os.environ.get("SHOT_DIR", "/tmp/busdrive_shots")); out.mkdir(parents=True, exist_ok=True)
    gd = "/TheLongDrive_Data/"
    for f in SHOTS:
        src = tldenv.GAME_DIR / gd.strip("/") / f
        if src.exists(): shutil.copy2(src, out / ("host_" + f))
        subprocess.run(["scp", "-q"] + tldenv.ssh_args()[1:-2] + [f"{tldenv.need_second()}:{tldenv.rsync_path(tldenv.REMOTE_GAME)}{gd}{f}".replace(" ", "\\ "), str(out / ("laptop_" + f))])
    print("  screenshots in", out, sorted(x.name for x in out.glob("*.png")), flush=True)

def slots(x):
    # filled + attached per slot: the names differ by machine (a spawned bus has Bus01WheelFull, a copy built from the
    # shared record Bus01TireFelni(Clone)) - the same parts
    return {(r[3] + "/" if len(r) > 3 else "") + r[0] + "#" + str(i): (bool(r[1]), r[2]) for i, r in enumerate(x(f"mount slots {NET}")["slots"])}

BASE = {}
def census(tag):
    """Every part slot of the bus on both machines vs the spawn census: a part off (the user saw a wheel fall off the bus
    after the seat swap - why it couldn't move) or a machine disagreeing fails the test."""
    sh, sl = slots(h), slots(L)
    if not BASE: BASE.update(sh)
    diffs = []
    for k, v in BASE.items():
        if sh.get(k) != v: diffs.append(f"host {k}: {v} -> {sh.get(k)}")
        if sl.get(k) != v: diffs.append(f"laptop {k}: {v} -> {sl.get(k)}")
    filled = sum(1 for v in sh.values() if v[0])
    print(f"  census {tag}: {len(sh)} slots ({filled} filled) on the host, {len(sl)} on the laptop; {len(diffs)} differences", flush=True)
    for d in diffs[:12]: print("     ", d, flush=True)
    return diffs

def ride(driver, rider, dname):
    """The driver drives SECS s along the road; each second the rear section on both machines, in the bus's frame."""
    worst_d, worst_y, n, rows = 0.0, 0.0, 0, []
    prev = {}   # the driver's bend a second ago: the rider's copy shows the bus ~0.25 s in the past
    start = ent(driver, NET)["pos"]; start_r = ent(rider, NET)["pos"]
    driver("autodrive on 12")
    try:
        for i in range(SECS):
            time.sleep(1)
            sit = driver("get mainscript.s.player.Bsitting")["value"]
            if not sit: print(f"  t{i+1:>2}s THE DRIVER IS NOT SEATED", flush=True)
            full = driver(f"mount bodies {NET}")
            bd = {r["name"]: r for r in full["bodies"]}
            if i % 4 == 3:
                dd = census(f"{dname} t{i+1}s")
                if dd and not FIRSTOFF: FIRSTOFF.append(f"{dname} t{i+1}s: {dd[0]}"); shoot(f"{dname}_t{i+1}_partoff")
            if i in (2, 9): print(f"  t{i+1}s DRIVER'S MACHINE car {full.get('car')} wheels {full.get('wheels(name,en,active,motor,brake,rpm,grounded,rb)')}", flush=True)
            br = {r["name"]: r for r in rider(f"mount bodies {NET}")["bodies"]}
            for name, a in bd.items():
                b_ = br.get(name)
                if b_ is None: rows.append(f"t{i+1} {name}: MISSING on the {'laptop' if rider is L else 'host'}"); continue
                d = math.dist(a["lp"], b_["lp"])
                # the copy's bend must lie between the driver's a second ago and now (+-3 deg): in a tight curve the
                # bend changes several degrees in the copy's 0.25 s delay - that's the delay, not a desync
                ry, cy, py = b_["lr"][1], a["lr"][1], prev.get(name, a["lr"][1])
                y = 0.0 if yawdiff(ry, cy) + yawdiff(ry, py) <= yawdiff(cy, py) + 6.0 else min(yawdiff(ry, cy), yawdiff(ry, py))
                prev[name] = cy
                worst_d, worst_y, n = max(worst_d, d), max(worst_y, y), n + 1
                print(f"  t{i+1:>2}s {name}: driver lp {a['lp']} yaw {a['lr'][1]:6.1f} kin {a['kin']} | rider lp {b_['lp']} yaw {b_['lr'][1]:6.1f} "
                      f"kin {b_['kin']} | off {d:.2f} m {y:.1f} deg | v {a['v']}", flush=True)
    finally:
        # brake to a stop before anyone gets out: let go at 12 m/s, the empty bus rolled on off the road while the
        # players swapped seats - every later drive "stuck" (the same bus, the same place)
        driver("autodrive on 0")
        until(lambda: driver(f"mount bodies {NET}")["car"]["rbVel"] < 0.3, 12, 1.0)
        driver("autodrive off")
    moved_d = math.dist(start, ent(driver, NET)["pos"]); moved_r = math.dist(start_r, ent(rider, NET)["pos"])
    if moved_d < 40:
        # where it stands: the road, its tilt, and a look at it from outside (the rider steps out 10 m in front)
        rp = rider("roadpoint"); bp = ent(driver, NET)["pos"]
        print(f"  STUCK: bus at {bp}, nearest road point {rp.get('gx')} {rp.get('gz')} ({math.dist((bp[0], bp[2]), (rp.get('gx', 0), rp.get('gz', 0))):.1f} m), "
              f"car {driver(f'mount bodies {NET}').get('car')}", flush=True)
        import subprocess
        if driver is L:
            lg = subprocess.run(tldenv.ssh_args() + [
                                 f"grep -a 'SeatTrace' {tldenv.remote_quote(tldenv.REMOTE_LOG)} | tail -6"],
                                capture_output=True, text=True, timeout=30).stdout
            print("  the driver's seat log:\n" + "\n".join("     " + l[:400] for l in lg.splitlines()), flush=True)
        try:
            rider("driveout"); time.sleep(1); print("  rider steps out:", rider(f"mount front {NET} 10"), flush=True); time.sleep(2)
            shoot(f"{dname}_stuck_outside")
            time.sleep(1); seat(rider, NET, "sitpassenger")
        except Exception as ex: print("  outside look:", ex, flush=True)
    for r in rows[:5]: print("  ", r, flush=True)
    verdict(f"{dname}: THE BUS DRIVES (moved on both machines)", moved_d > 40 and moved_r > 40, f"driver's view {moved_d:.0f} m, rider's {moved_r:.0f} m")
    verdict(f"{dname}: THE REAR SECTION BENDS THE SAME ON BOTH MACHINES", n > 0 and not rows and worst_d < 0.5 and worst_y < 3.0,
            f"{n} samples, worst {worst_d:.2f} m / {worst_y:.1f} deg outside the driver's last second{'; ' + str(len(rows)) + ' missing' if rows else ''}")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    for s_ in (h, L): s_("set TLDRevamp.Net.Mp.RemoteTrace True")   # SeatTrace: every GetOut, forced or not, with where
    gods = {}
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, s_("get kaposztaleves.s.settings.god").get("value")); s_("set kaposztaleves.s.settings.god True")
        rp = h("roadpoint"); h(f"tpground {rp['gx']} {rp['gz']}"); time.sleep(3)
        before = {e["net"] for e in h("mp entities")["entities"]}
        print("bus:", h(f"devspawn {BUS}"), flush=True)
        if not until(lambda: any(e["car"] and e["net"] not in before for e in h("mp entities")["entities"]), 20): sys.exit("NO BUS ENTITY")
        NET = next(e["net"] for e in h("mp entities")["entities"] if e["car"] and e["net"] not in before)
        p = ent(h, NET)["pos"]; print("bus net", NET, "at", p, flush=True)
        L(f"tpground {p[0] + 6} {p[2]}"); time.sleep(3)
        if not until(lambda: ent(L, NET) is not None, 30): sys.exit("THE LAPTOP NEVER GOT THE BUS")
        census("spawned")
        for nm, s_ in (("host", h), ("laptop", L)): print(f"  slot misses on the {nm}: {s_('slotmiss')}", flush=True)
        for nm, s_ in (("host", h), ("laptop", L)):
            gi = s_(f"mount items {NET}"); print(f"  group on the {nm}: {gi.get('n')} items", flush=True)
            for row in gi.get("items", []):
                if "Wheel" in row or "Tire" in row or "Gumi" in row or "LOOSE" in row or "-2" in row: print("     ", row, flush=True)
        for nm, s_ in (("host", h), ("laptop", L)):
            sd = s_("slotdestroys"); print(f"  slotted parts destroyed on the {nm}: {sd['count']}", flush=True)
            for ln in sd["log"].split("\n")[:60]: print("     ", ln, flush=True)
        print("bodies on the host:", h(f"mount bodies {NET}"), flush=True)
        print("bodies on the laptop:", L(f"mount bodies {NET}"), flush=True)
        for rnd in range(ROUNDS):
            A, B = f"R{rnd+1} A HOST DRIVES", f"R{rnd+1} B LAPTOP DRIVES"
            if rnd:
                for s_ in (h, L):
                    try: s_("driveout")
                    except RuntimeError as ex: print("  driveout:", ex, flush=True)
                time.sleep(3)
            # A: the host drives, the laptop rides
            print("  host sits:", seat(h, NET, "sitdriver"), " laptop sits:", seat(L, NET, "sitpassenger"), flush=True)
            time.sleep(2); print("  owner host/laptop view:", ent(h, NET)["owner"], ent(L, NET)["owner"], flush=True)
            ride(h, L, A)
            dA = census(f"end of {A}")
            verdict(f"{A}: NO PART OFF OR DIFFERENT", not dA, f"{len(dA)} differences")
            for nm, s_ in (("host", h), ("laptop", L)): print(f"  end of A, {nm}: car {s_(f'mount bodies {NET}').get('car')}", flush=True)
            # B: swap seats — the laptop drives, the host rides
            for s_ in (h, L):
                try: s_("driveout")
                except RuntimeError as ex: print("  driveout:", ex, flush=True)
            time.sleep(3)
            print("  laptop sits:", seat(L, NET, "sitdriver"), " host sits:", seat(h, NET, "sitpassenger"), flush=True)
            until(lambda: ent(h, NET)["owner"] == 1 and ent(L, NET)["owner"] == 1, 15)
            print("  owner host/laptop view:", ent(h, NET)["owner"], ent(L, NET)["owner"], flush=True)
            for nm, s_ in (("host", h), ("laptop", L)): print(f"  after the swap, {nm}: car {s_(f'mount bodies {NET}').get('car')}", flush=True)
            dS = census(f"after swap {rnd+1}")
            verdict(f"R{rnd+1} SWAP: NO PART OFF OR DIFFERENT", not dS, f"{len(dS)} differences")
            ride(L, h, B)
            dB = census(f"end of {B}")
            verdict(f"{B}: NO PART OFF OR DIFFERENT", not dB, f"{len(dB)} differences; first seen {FIRSTOFF[:1]}")
        # C: both players leave the bus 5 km behind (it goes into the far store on both machines) and come back - the
        # store/reload path that hung the rear wheels in the air before v0.65.13
        if "--no-store" not in sys.argv:
            from roadkit import tp
            for s_ in (h, L):
                try: s_("driveout")
                except RuntimeError: pass
            time.sleep(2)
            bp = ent(h, NET)["pos"]
            for nm, s_ in (("host", h), ("laptop", L)): print(f"  {nm} 5 km away:", tp(s_, bp[0] + 5000, bp[2] + (8 if s_ is L else 0)), flush=True)
            time.sleep(10)
            print("  host far store at the bus:", h(f"mp farstore {bp[0]} {bp[2]} 30"), flush=True)
            for nm, s_ in (("host", h), ("laptop", L)): print(f"  {nm} back:", tp(s_, bp[0] + 10, bp[2] + (4 if s_ is L else 0)), flush=True)
            time.sleep(10)
            until(lambda: ent(h, NET) is not None and ent(L, NET) is not None, 30)
            dC = census("after the far store")
            for nm, s_ in (("host", h), ("laptop", L)):
                gi = s_(f"mount items {NET}")
                for row in gi.get("items", []):
                    if row.split(" ")[0] in ("24", "25", "26", "27") or "LOOSE" in row and not row.startswith("0 "): print(f"     {nm}:", row, flush=True)
            verdict("C FAR STORE: THE BUS COMES BACK WHOLE ON BOTH MACHINES", not dC, f"{len(dC)} differences")
    finally:
        for s_ in (h, L):
            try: s_("autodrive off"); s_("driveout")
            except Exception: pass
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
if SHOTS: fetch_shots()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
