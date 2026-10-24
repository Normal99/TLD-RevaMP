#!/usr/bin/env python3
"""Car radio dials jittering in MP (playtest 2026-10-24). Two clients over the relay: the host drives its car01 (its
owner), the joiner rides as a passenger. One side turns the radio's tuning or volume dial with the game's own input
call (`mp dragusable`: 3 s, 60 Hz, always upward), and BOTH sides record the dial every frame (`mp usableprobe`).
A dial that jitters shows as steps back against the turn, or as the value leaving its final position again after it
got there (an older value applied over a newer one). Parked first, then at speed; the owner's hand and the joiner's.
  tools/dialjitter.py [--no-build] [--relay iad|off] [--kmh 60] [--dials tune,vol] [--old: stepped knobs take values between steps, as before]"""
import sys, time, math, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import g, car_ready, clear_road

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, KMH, DIALS, OLD = arg("--relay", "iad"), arg("--kmh", 60.0, float), arg("--dials", "tune,vol").split(","), "--old" in sys.argv
DRAG, PROBE = 3.0, 7.0
T = "TLDRevamp.Net.Entities"
VERDICTS, ROWS = [], []
OUT = pathlib.Path(arg("--out", "/dev/null"))
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def analyse(samples):
    """the dial's value (tunedFloat) frame by frame: steps back against the upward turn, departures after settling"""
    v = [s[1][4] for s in samples]
    if len(v) < 10: return {"frames": len(v)}
    span = max(v) - min(v)
    if span <= 0: return {"frames": len(v), "span": 0}
    eps = span * 0.004
    lo = min(v); start = next(i for i, x in enumerate(v) if x <= lo + eps)
    back = [(v[i - 1] - v[i]) / span for i in range(start + 1, len(v)) if v[i] < v[i - 1] - eps]
    final = v[-1]; reached = None; departures = 0; dev = 0.0
    for i in range(start, len(v)):
        near = abs(v[i] - final) <= eps
        if reached is None:
            if near: reached = i
        elif not near:
            dev = max(dev, abs(v[i] - final) / span)
            if abs(v[i - 1] - final) <= eps: departures += 1
    changes = sum(1 for i in range(1, len(v)) if v[i] != v[i - 1])
    return {"frames": len(v), "changes": changes, "span": round(span, 4), "back": len(back), "backMax": round(max(back, default=0), 4),
            "departures": departures, "devMax": round(dev, 4), "final": round(final, 4),
            "settledAt": round(samples[reached][0] - samples[start][0], 2) if reached is not None else None}

def counters(s):
    e = s("mp entities"); lu = s("mp liveusables")
    return {"live": lu, "resyncApplied": e["resync"]["applied"], "resyncHeld": e["resync"]["held"], "editsSent": e["edits"]["sent"], "editsApplied": e["edits"]["applied"]}

def turn(h, L, actor, dial, addr, tag):
    sides = {"host": h, "joiner": L}
    who = sides[actor]
    before = {n: counters(s) for n, s in sides.items()}
    for s in sides.values(): s(f"mp usableprobe {addr[0]} {addr[1]} {addr[2]} {PROBE}")
    # the volume knob in whole steps, a few a second, as the scroll wheel turns it (one sample per step: the receiver
    # interpolates over the gap between them); the tuning dial smoothly, every frame, as a mouse drag does
    r = who(f"mp dragusable {addr[0]} {addr[1]} {addr[2]} {DRAG} {4 if dial == 'vol' else 60}")
    if "error" in r: print("  drag:", r, flush=True); return None
    time.sleep(PROBE + 0.7)
    res = {}
    for n, s in sides.items():
        p = s("mp usableprobe")
        res[n] = analyse(p.get("samples", [])); res[n]["updatesPerS"] = p.get("updatesPerS"); res[n]["tuned"] = [round(x[1][4], 4) for x in p.get("samples", [])]
    after = {n: counters(s) for n, s in sides.items()}
    radios = {n: s(f"mp radiocar {addr[0]}")["radios"] for n, s in sides.items()}
    row = {"tag": tag, "actor": actor, "dial": dial, "res": res, "before": before, "after": after, "radios": radios}
    ROWS.append(row)
    print(f"  {tag} {dial} turned by the {actor}:", flush=True)
    for n in sides: print(f"    {n:6}: {({k: v for k, v in res[n].items() if k != 'tuned'})}", flush=True)
    for n in sides:
        b, a = before[n], after[n]
        print(f"    {n:6}: live sent {a['live']['sent'] - b['live']['sent']} applied {a['live']['applied'] - b['live']['applied']} stale {a['live']['stale'] - b['live']['stale']}"
              f" | resync applied {a['resyncApplied'] - b['resyncApplied']} held {a['resyncHeld'] - b['resyncHeld']} | edits sent {a['editsSent'] - b['editsSent']} applied {a['editsApplied'] - b['editsApplied']}"
              f" | radio {[(x['fr'], x['volume']) for x in radios[n]]}", flush=True)
    clean = all(res[n].get("back", 1) == 0 and res[n].get("departures", 1) == 0 for n in sides)
    same = abs(res["host"].get("final", 0) - res["joiner"].get("final", 1)) <= max(res["host"].get("span", 1), 1e-6) * 0.004
    verdict(f"{tag.upper()}: {dial.upper()} TURNED BY THE {actor.upper()} MOVES ONE WAY ON BOTH AND ENDS THE SAME", clean and same,
            f"back {res['host'].get('back')}/{res['joiner'].get('back')} (max {res['host'].get('backMax')}/{res['joiner'].get('backMax')}), "
            f"departures {res['host'].get('departures')}/{res['joiner'].get('departures')} (dev {res['host'].get('devMax')}/{res['joiner'].get('devMax')}), "
            f"final {res['host'].get('final')}/{res['joiner'].get('final')}")
    return res

def screen(h, L, car, tag, secs=5):
    """the tuning dial as each player's camera draws it (Diagnostics.AimLab): steps and back-and-forth in mm. Information
    for the pose code (the car's interpolation is another thread's), not a verdict on the dial's value."""
    for s in (h, L): s(f"aimlab start {car}")
    time.sleep(secs)
    r = {n: s("aimlab stop") for n, s in (("host", h), ("joiner", L))}
    ROWS.append({"tag": tag, "screen": r})
    for n in r: print(f"  {tag} screen, {n:6}: frames {r[n].get('camFrames')} step avg {r[n].get('camStepAvg')} max {r[n].get('camStepMax')} mm,"
                      f" jerk avg {r[n].get('camJerkAvg')} max {r[n].get('camJerkMax')} mm, ray hits {r[n].get('hits')}/{r[n].get('frames')}", flush=True)
    return r

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    gods = {}; shift_h = g(h, "settingsscript.s.S.BAutoShift")
    try:
        if not join_both(h, L): sys.exit("JOINER NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for name, s_ in (("host", h), ("joiner", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
            s_(f"set TLDRevamp.Net.UsableMerge.SnapSteps {not OLD}")
        print(f"  {'OLD (stepped knobs take values between steps)' if OLD else 'CURRENT BUILD'}: SnapSteps {g(h, 'TLDRevamp.Net.UsableMerge.SnapSteps')}/{g(L, 'TLDRevamp.Net.UsableMerge.SnapSteps')}", flush=True)
        h("set settingsscript.s.S.BAutoShift True")
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
        R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        if not car_ready(h, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        me = h("mp players")["me"]
        car = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
        # held: an unbraked car01 rolled off on a slope and the joiner's seat tries started 37-41 m away (dialjitter-4)
        h(f"set {T}.TestHandbrake 1"); time.sleep(2)
        def beside(s_):   # next to the car where it stands now
            at = next(e["pos"] for e in h("mp entities")["entities"] if e["net"] == car)
            s_(f"tpground {at[0] + R[0] * 3} {at[2] + R[1] * 3}"); time.sleep(3)
        for _ in range(3):
            beside(L)
            print("  passenger:", L(f"mp sitpassenger {car}"), flush=True); time.sleep(2)
            if g(L, "mainscript.s.player.Bsitting") is True: break
        verdict("SETUP: THE JOINER RIDES ALONG", g(L, "mainscript.s.player.Bsitting") is True)
        radios = h(f"mp radiocar {car}")["radios"]
        print("  radios:", radios, "| copy:", L(f"mp radiocar {car}"), flush=True)
        if not radios or radios[0]["tune"] is None: sys.exit("no radio dial in the car")
        addr = {"tune": radios[0]["tune"], "vol": radios[0]["vol"]}
        h(f"set {T}.TestIgnition 1"); time.sleep(1)   # the radio needs power
        screen(h, L, car, "parked")
        for actor in ("joiner", "host"):
            for dial in DIALS: turn(h, L, actor, dial, addr[dial], "parked")
        h(f"set {T}.TestHandbrake -1"); h(f"autodrive on {KMH / 3.6:.1f}")
        t0 = time.monotonic(); v = 0.0
        while time.monotonic() - t0 < 40:
            v = abs(float(g(h, "mainscript.s.player.Car.speed") or 0))
            if v > KMH * 0.85: break
            time.sleep(0.5)
        print(f"  at {v:.0f} km/h", flush=True)
        screen(h, L, car, f"at {KMH:.0f} km/h")
        for actor in ("joiner", "host"):
            for dial in DIALS: turn(h, L, actor, dial, addr[dial], f"at {KMH:.0f} km/h")
        h("autodrive off")
        # swapped: the joiner drives (the car becomes its own), the host rides along as the non-owner
        t0 = time.monotonic()
        while time.monotonic() - t0 < 30 and abs(float(g(h, "mainscript.s.player.Car.speed") or 0)) > 2: time.sleep(0.5)
        print("  out:", L("driveout"), h("driveout"), flush=True); time.sleep(2)
        for _ in range(3):
            beside(L)
            print("  joiner drives:", L(f"mp sitdriver {car}"), flush=True); time.sleep(2)
            if g(L, "mainscript.s.player.Bsitting") is True: break
        L(f"set {T}.TestHandbrake 1")
        for _ in range(3):
            beside(h)
            print("  host rides:", h(f"mp sitpassenger {car}"), flush=True); time.sleep(2)
            if g(h, "mainscript.s.player.Bsitting") is True: break
        t0 = time.monotonic(); owner = None
        while time.monotonic() - t0 < 15:
            owner = next((e["owner"] for e in h("mp entities")["entities"] if e["net"] == car), None)
            if owner not in (None, 0): break
            time.sleep(1)
        verdict("SETUP: THE JOINER DRIVES AND OWNS THE CAR, THE HOST RIDES", g(L, "mainscript.s.player.Bsitting") is True and g(h, "mainscript.s.player.Bsitting") is True and owner not in (None, 0),
                f"owner {owner}")
        if owner not in (None, 0):
            L(f"set {T}.TestIgnition 1"); time.sleep(1)
            for actor in ("host", "joiner"):
                for dial in DIALS: turn(h, L, actor, dial, addr[dial], "swapped, parked")
        er = [x_("errors") for x_ in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er], flush=True)
    finally:
        try: h("autodrive off"); h(f"set {T}.TestIgnition -1"); L(f"set {T}.TestIgnition -1"); h(f"set {T}.TestHandbrake -1"); L(f"set {T}.TestHandbrake -1"); h("aimlab stop"); L("aimlab stop")
        except Exception: pass
        for s_, v_ in gods.values():
            try: s_(f"set kaposztaleves.s.settings.god {v_}"); s_("set TLDRevamp.Net.UsableMerge.SnapSteps True")
            except Exception: pass
        h(f"set settingsscript.s.S.BAutoShift {shift_h}")
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
        if str(OUT) != "/dev/null": OUT.write_text(json.dumps(ROWS, indent=1))
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"DIALJITTER: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")
sys.exit(1 if fails else 0)
