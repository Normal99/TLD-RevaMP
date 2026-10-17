#!/usr/bin/env python3
"""The IFA that "doesn't drive well" (Fixes/WheelInertia.cs) and the game's car trailer that "pulls its hitch down"
(Fixes/TrailerTongue.cs), playtest 2026-10-15. The desktop alone (host, no joiner). Each part twice, OLD (the fix off)
then NEW (on), each on fresh vehicles; every sample goes to a JSON file.
  ifa   the IFA (item 174) under autodrive: its four driven wheels against the speed it goes (`mount drive`)
          A NEW: the driven wheels turn together (median spread < 15%, median |slip| < 0.2)
          B OLD: the bug shows (median spread > 30%)
          C NEW: it reaches 15 m/s within 13 s (OLD's time is printed: it varies with its slipping wheels)
          D a Car01 is left as the game has it (its wheels keep the masses the game gives them)
  sweep one IFA driving on, the fix's MinLoadShare changed every SECS: off, 1/30, 1/15, 1/8, off (how much is enough)
  tow   a Car01 and the game's trailer (item 53) hitched (`mount hitch`, `mount tow`)
          E OLD: the bug shows (over 35% of the trailer's weight on the ball)
          F NEW: 5-20% of its weight on the ball
          G NEW: the car's front wheels keep 90% of the load they have without the trailer
          H NEW: towed at 15 m/s without swaying (the trailer within 0.5 m of the car's line, the last 10 s)
  tools/ifatow.py [ifa] [tow] [sweep] [--no-build] [--secs 25] [--out FILE]"""
import sys, time, math, json, statistics, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from roadkit import until, g, clear_road

IFA, CAR01, TRAILER = 174, 2, 53
SECS = int(sys.argv[sys.argv.index("--secs") + 1]) if "--secs" in sys.argv else 25
OUT = pathlib.Path(sys.argv[sys.argv.index("--out") + 1]) if "--out" in sys.argv else pathlib.Path("/tmp/ifatow.json")
PARTS = [p for p in ("ifa", "tow", "sweep") if p in sys.argv] or ["ifa", "tow"]
DATA, VERDICTS = {}, []

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def roots(h): return {e["net"]: e for e in h("mp entities")["entities"] if e["part"] < 0}

def spawn(h, item, x, z, yaw):
    before = set(roots(h))
    print(f"  spawn {item}:", h(f"spawncarg {item} 0 {x} {z} {yaw}"), flush=True)
    until(lambda: set(roots(h)) - before, 20)
    new = set(roots(h)) - before
    time.sleep(3)
    return next(iter(new), None)

def sit(h, net):
    for _ in range(6):
        r = h(f"mp sitdriver {net}")
        if "seat" in r and not r.get("stepped"): break
        time.sleep(1.5)
    time.sleep(1)
    return g(h, "mainscript.s.player.Bsitting") is True

def drive(h, net, probe, target, step=0.5):
    """autodrive at `target` m/s for SECS, a `mount <probe> <net>` every `step` s"""
    rows = []
    h(f"autodrive on {target}")
    t0 = time.time()
    try:
        while time.time() - t0 < SECS:
            time.sleep(step)
            st = h("autodrive")
            try: d = h(f"mount {probe} {net}")
            except RuntimeError as e: d = {"error": str(e)[:200]}
            rows.append({"t": round(time.time() - t0, 2), "speed": st.get("speed"), "probe": d})
    finally:
        h("autodrive off")
    return rows

T15_MAX = 13.0   # s: NEW took 10.8 s in both 0.67.2 runs

def wheel_stats(rows):
    """per sample over 8 m/s: the driven wheels' spread around their mean (fraction) and |forward slip|"""
    spread, slip, gears, t15 = [], [], [], None
    for r in rows:
        d = r["probe"]
        if "driven" not in d: continue
        if t15 is None and (r["speed"] or 0) >= 15: t15 = r["t"]
        gears.append(d["gear"])
        if (r["speed"] or 0) < 8: continue
        rpm = [w["rpm"] for w in d["driven"]]
        m = statistics.mean(rpm)
        if m > 1: spread.append(max(abs(x - m) for x in rpm) / m)
        slip.extend(abs(w["fslip"]) for w in d["driven"])
    shifts = sum(1 for a, b in zip(gears, gears[1:]) if a != b)
    return {"spread": statistics.median(spread) if spread else None, "slip": statistics.median(slip) if slip else None,
            "t15": t15, "shifts": shifts, "n": len(spread)}

def forward(yaw): return (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))

with game.ProtectedSession():
    if game.running(): game.quit_game()
    if "--no-build" not in sys.argv: game.build()
    else: game.deploy_local()
    game.launch()
    h = Bridge()
    print("build:", h("ping"), flush=True)
    seeded.restart_same_seed()
    h("set TLDRevamp.TestSafety.BlockAutoSave True")
    try:
        h("mp host 27070"); time.sleep(2)
        h("set kaposztaleves.s.settings.god True")
        h("set TLDRevamp.DriveLab.Recover False")
        rp = h("roadpoint"); F = forward(rp["yaw"])
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
        F = forward(yaw); R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        x, z = r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8

        def next_spot(net):
            """the next vehicles start on a clear road beyond where this one stopped"""
            global x, z, yaw, F, R
            h("driveout"); time.sleep(1)
            p = roots(h)[net]["pos"]
            r = clear_road(h, p[0] + F[0] * 60, p[2] + F[1] * 60, F[0], F[1]); yaw = r["yaw"]
            F = forward(yaw); R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
            x, z = r["gx"] + R[0] * 1.8, r["gz"] + R[1] * 1.8

        if "ifa" in PARTS:
            res = {}
            for label, item, on in (("OLD", IFA, False), ("NEW", IFA, True), ("car01", CAR01, True)):
                h(f"set TLDRevamp.Fixes.WheelInertia.Enabled {on}")
                h(f"tpground {x - R[0] * 6} {z - R[1] * 6}"); time.sleep(2)
                net = spawn(h, item, x, z, yaw)
                if not net: print(f"  {label}: NOT SPAWNED", flush=True); continue
                print(f"  {label} net {net}; sits: {sit(h, net)}; fuel: {h('fuelup')}", flush=True)
                time.sleep(3)
                print(f"  wheel inertia: {g(h, 'TLDRevamp.Fixes.WheelInertia.Stats')}", flush=True)
                rows = drive(h, net, "drive", 25)
                DATA[f"ifa_{label}"] = rows
                res[label] = wheel_stats(rows)
                res[label]["masses"] = sorted({w["m"] for r in rows if "driven" in r["probe"] for w in r["probe"]["driven"]})
                print(f"  {label}: {res[label]}", flush=True)
                if label == "OLD":   # NEW starts where OLD did (the same road ahead): OLD has driven far on by now
                    h("driveout"); time.sleep(1)
                else: next_spot(net)
            o, n, c = res.get("OLD", {}), res.get("NEW", {}), res.get("car01", {})
            verdict("A NEW: THE IFA'S DRIVEN WHEELS TURN TOGETHER", n.get("spread") is not None and n["spread"] < 0.15 and n["slip"] < 0.2,
                    f"spread {n.get('spread')}, slip {n.get('slip')}, shifts {n.get('shifts')}")
            verdict("B OLD: THE BUG SHOWS", o.get("spread") is not None and o["spread"] > 0.3, f"spread {o.get('spread')}, slip {o.get('slip')}, shifts {o.get('shifts')}")
            # against a fixed time, not OLD's: OLD's slipping wheels made its time vary 8.5-12.6 s from run to run on the same
            # road (NOTES 0.67.2), so "no later than OLD" failed on OLD's luck; NEW took 10.8 s in both of those runs
            verdict(f"C NEW: IT REACHES 15 M/S WITHIN {T15_MAX:g} S", n.get("t15") is not None and n["t15"] <= T15_MAX,
                    f"NEW {n.get('t15')} s (OLD {o.get('t15')} s, for comparison only)")
            verdict("D A CAR01 IS LEFT AS THE GAME HAS IT", c.get("masses") == [21.0],
                    f"car01 wheel masses {c.get('masses')}, spread {c.get('spread')}; IFA OLD {o.get('masses')} NEW {n.get('masses')}")
            DATA["ifa_stats"] = res

        if "sweep" in PARTS:
            h("set TLDRevamp.Fixes.WheelInertia.Enabled False")
            h(f"tpground {x - R[0] * 6} {z - R[1] * 6}"); time.sleep(2)
            net = spawn(h, IFA, x, z, yaw)
            print(f"  sweep IFA net {net}; sits: {sit(h, net)}; fuel: {h('fuelup')}", flush=True)
            res = {}
            for label, share in (("off", None), ("1/30", 1 / 30), ("1/15", 1 / 15), ("1/8", 1 / 8), ("off again", None)):
                if share is None: h("set TLDRevamp.Fixes.WheelInertia.Enabled False")
                else: h(f"set TLDRevamp.Fixes.WheelInertia.MinLoadShare {share}"); h("set TLDRevamp.Fixes.WheelInertia.Enabled True")
                rows = drive(h, net, "drive", 20)
                DATA[f"sweep_{label}"] = rows
                res[label] = wheel_stats(rows)
                res[label]["masses"] = sorted({round(w["m"]) for r in rows if "driven" in r["probe"] for w in r["probe"]["driven"]})
                print(f"  sweep {label}: {res[label]}", flush=True)
            DATA["sweep_stats"] = res
            h("set TLDRevamp.Fixes.WheelInertia.MinLoadShare 0.0666667"); h("set TLDRevamp.Fixes.WheelInertia.Enabled True")
            next_spot(net)

        if "tow" in PARTS:
            res = {}
            for label, on in (("OLD", False), ("NEW", True)):
                h(f"set TLDRevamp.Fixes.TrailerTongue.Enabled {on}")
                h(f"tpground {x - R[0] * 6} {z - R[1] * 6}"); time.sleep(2)
                car = spawn(h, CAR01, x, z, yaw)
                print("  car sits:", sit(h, car), h("fuelup"), flush=True); time.sleep(4)
                alone = h(f"mount drive {car}")
                front_alone = sum(w["load"] for w in alone["steer"])
                h("driveout"); time.sleep(2)
                tr = spawn(h, TRAILER, x - F[0] * 7, z - F[1] * 7, yaw)
                if not tr: sys.exit("NO TRAILER")
                time.sleep(3)
                print("  hitch:", h(f"mount hitch {car} {tr}"), flush=True)
                rest = []
                for k in range(8):
                    time.sleep(1); rest.append(h(f"mount tow {tr}"))
                last = rest[-3:]
                weight = last[-1]["body"]["mass"] * 9.81
                ball = statistics.median(abs(r["trailerJoint"]["force"][1]) for r in last)
                front = statistics.median(sum(w[3] for w in r["car"]["wheels(name,z,grounded,load,compress)"] if w[0].startswith("F")) for r in last)
                print(f"  {label} at rest: ball {ball:.0f} N of {weight:.0f} N; front wheels {front:.0f} N (alone {front_alone:.0f}); trailer pitch {last[-1]['pitch']}, com {last[-1]['body']['com']}", flush=True)
                print("  tongue:", g(h, "TLDRevamp.Fixes.TrailerTongue.Stats"), flush=True)
                print("  car sits:", sit(h, car), flush=True)
                rows = drive(h, tr, "tow", 15)
                late = [r for r in rows if r["t"] > SECS - 10 and "car" in r["probe"] and r["probe"]["car"]]
                sway = max((abs(r["probe"]["car"]["trailerAtCar"][0]) for r in late), default=None)
                top = max((r["speed"] or 0) for r in rows)
                flipped = any(abs(r["probe"].get("roll", 0)) > 30 for r in rows if "roll" in r["probe"])
                res[label] = {"ball": ball, "weight": weight, "share": ball / weight, "front": front, "frontAlone": front_alone,
                              "pitch": last[-1]["pitch"], "sway": sway, "top": top, "flipped": flipped}
                DATA[f"tow_{label}"] = {"alone": alone, "rest": rest, "drive": rows}
                print(f"  {label}: {res[label]}", flush=True)
                next_spot(car)
            o, n = res.get("OLD", {}), res.get("NEW", {})
            verdict("E OLD: THE BUG SHOWS (OVER 35% ON THE BALL)", o.get("share", 0) > 0.35, f"{o.get('share', 0):.0%}")
            verdict("F NEW: 5-20% OF THE TRAILER'S WEIGHT ON THE BALL", 0.05 <= n.get("share", 0) <= 0.20, f"{n.get('share', 0):.0%}")
            verdict("G NEW: THE CAR'S FRONT WHEELS KEEP 90% OF THEIR LOAD", n.get("front", 0) >= 0.9 * n.get("frontAlone", 1),
                    f"{n.get('front', 0):.0f} N of {n.get('frontAlone', 0):.0f} N (OLD {o.get('front', 0):.0f} of {o.get('frontAlone', 0):.0f})")
            verdict("H NEW: TOWED AT 15 M/S WITHOUT SWAYING", n.get("sway") is not None and n["sway"] < 0.5 and n["top"] >= 12 and not n["flipped"],
                    f"sway {n.get('sway')}, top {n.get('top')} m/s, flipped {n.get('flipped')} (OLD sway {o.get('sway')})")
            DATA["tow_stats"] = res
        er = h("errors"); print("  errors:", er["errors"], er["exceptions"], (er.get("kinds") or [])[:3], flush=True)
        DATA["errors"] = er
    finally:
        OUT.write_text(json.dumps(DATA, indent=1))
        print("written", OUT, flush=True)
        for cmd in ("autodrive off", "set TLDRevamp.Fixes.WheelInertia.Enabled True", "set TLDRevamp.Fixes.TrailerTongue.Enabled True"):
            try: h(cmd)
            except Exception: pass
        h("mp stop")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"IFATOW: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")
