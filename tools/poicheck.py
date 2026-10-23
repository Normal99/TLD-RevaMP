#!/usr/bin/env python3
"""Main road only: does every machine get the same buildings, whatever order it generated the area in? (14c6057)

  tools/poicheck.py run --from north|south [--draw 25000] [--site -27581] [--travel 20000] [--step 2500]
                        [--settle 30] [--still 60] [--seed 20260924] [--old] [--out FILE]
  tools/poicheck.py compare A.json B.json

run: a new main-road-only game on the test copy (TLD_DIR / TLD_TEST_COMPAT / TLD_BRIDGE_PORT) at draw distance --draw.
The player starts --travel metres north or south of the site on the main road (x 0) and hops toward it in --step
steps, waiting at each for the roads. At the site it waits --settle s, then stands still --still s. Written to --out:
  decisions  every building the game aligned to a road (bridge `poialign`): candidate -> where it went, the last one
             per candidate, and candidates decided more than one way on this machine
  pois       the buildings within 3 km of the site (worldhash rows: position, type, rotation, aligned)
  held       MainRoadOnlyRoads.PoisHeldForLinks at the start and end of standing still, and the buildings still
             waiting (`poiwait`) then: one that waits the whole time with the player standing still waits forever
--old switches PoisWaitForLinks and LineAhead off (v0.69.1's behaviour) before the world is made.

compare: buildings decided on both machines that went to different places (the count 14c6057 takes to 0), and the
buildings near the site only one machine has.
"""
import argparse, json, pathlib, sys, time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game  # noqa: E402

ROAD = "TLDRevamp.WorldGen.MainRoadOnlyRoads"


def roads_ready(report):
    gens = [g for g in report["generators"] if g["listed"] and g["active"]]
    return gens and all(g["needed"] > 0 and g["generated"] > 0 and g["mainRoad"].get("mainChunks", 0) > 0 for g in gens)


def hop(b, z):
    b(f"tpg 0 {z:.0f} hold")
    time.sleep(2)
    b(f"tpground 0 {z:.0f}")
    t0 = time.monotonic()
    while not roads_ready(b("terrain roads")) and time.monotonic() - t0 < 60:
        time.sleep(2)
    time.sleep(3)


def key(gx, gz): return f"{round(gx)},{round(gz)}"


def run(a):
    out = pathlib.Path(a.out or f"poicheck-{a.came_from}-{int(a.draw)}{'-old' if a.old else ''}.json")
    sign = 1 if a.came_from == "north" else -1
    res = {"from": a.came_from, "draw": a.draw, "site": a.site, "seed": a.seed, "old": a.old}
    with game.ProtectedSession():
        try:
            game.launch()
            b = game.Bridge()
            b("terrain select MainRoadOnly")
            if a.old:
                b(f"set {ROAD}.PoisWaitForLinks False")
                b(f"set {ROAD}.LineAhead False")
            b(f"set settingsscript.s.S.FDrawDistance {a.draw}")
            b("poialign on")
            game.newgame(a.seed)
            b = game.Bridge()
            b("set TLDRevamp.TestSafety.BlockAutoSave True")
            b("overlay off")
            b(f"set settingsscript.s.S.FDrawDistance {a.draw}")
            b("poialign on")   # forget the start area; record from the first hop on
            z = a.site + sign * a.travel
            while sign * (z - a.site) > 0:
                hop(b, z)
                print(f"  at z {z:.0f}: held {b(f'get {ROAD}.PoisHeldForLinks')['value']}", flush=True)
                z -= sign * a.step
            hop(b, a.site)
            time.sleep(a.settle)
            h0, t0 = int(b(f"get {ROAD}.PoisHeldForLinks")["value"]), time.monotonic()
            w0 = b(f"poiwait 0 {a.site} 20000")["waiting"]
            time.sleep(a.still)
            h1, dt = int(b(f"get {ROAD}.PoisHeldForLinks")["value"]), time.monotonic() - t0
            w1 = b(f"poiwait 0 {a.site} 20000")
            still = {key(w["gx"], w["gz"]) for w in w0} & {key(w["gx"], w["gz"]) for w in w1["waiting"]}
            res["held"] = {"start": h0, "end": h1, "seconds": round(dt, 1), "waitingStart": w0, "waitingEnd": w1["waiting"],
                           "waitedThroughout": sorted(still), "roadChunks": w1["roadChunks"]}
            rows = b(f"poialign 0 {a.site} 40000")["rows"]
            last, ways = {}, {}
            for r in rows:
                k = key(r["gx"], r["gz"])
                to = key(r["px"], r["pz"])
                last[k] = {"to": to, "px": r["px"], "pz": r["pz"], "ok": r["ok"], "offered": r["offered"], "chunks": r["chunks"]}
                ways.setdefault(k, set()).add(to)
            res["decisions"] = last
            res["decidedTwoWays"] = {k: sorted(v) for k, v in ways.items() if len(v) > 1}
            h = b(f"worldhash 0 {a.site} 3000 detail")
            res["pois"] = next((v.get("rows", []) for k, v in h.items() if k.split(":")[-1] == "pois"), [])
            res["errors"] = {"MainRoadOnlyRoads": b(f"get {ROAD}.Errors")["value"], "last": b(f"get {ROAD}.LastError")["value"]}
        finally:
            if game.running():
                game.quit_game()
    out.write_text(json.dumps(res, indent=1))
    h = res.get("held", {})
    print(f"{a.came_from} at draw {a.draw:.0f}: {len(res.get('decisions', {}))} buildings decided "
          f"({len(res.get('decidedTwoWays', {}))} two ways), {len(res.get('pois', []))} within 3 km of the site; "
          f"held {h.get('start')} -> {h.get('end')} in {h.get('seconds')} s standing still, "
          f"{len(h.get('waitedThroughout', []))} waited throughout -> {out}", flush=True)


def compare(pa, pb):
    A, B = json.loads(pathlib.Path(pa).read_text()), json.loads(pathlib.Path(pb).read_text())
    da, db = A["decisions"], B["decisions"]
    both = sorted(set(da) & set(db))
    diff = [(k, da[k]["to"], db[k]["to"]) for k in both if da[k]["to"] != db[k]["to"]]
    pa_, pb_ = set(A["pois"]), set(B["pois"])
    print(f"A: from the {A['from']} at draw {A['draw']:.0f}{' (old)' if A['old'] else ''}; "
          f"B: from the {B['from']} at draw {B['draw']:.0f}{' (old)' if B['old'] else ''}")
    print(f"buildings decided on both: {len(both)} (A {len(da)}, B {len(db)}); went to different places: {len(diff)}")
    for k, ta, tb in diff[:10]:
        print(f"  candidate {k}: A -> {ta}, B -> {tb}")
    print(f"buildings within 3 km of the site: A {len(pa_)}, B {len(pb_)}; only on A {len(pa_ - pb_)}, only on B {len(pb_ - pa_)}")
    for r in sorted(pa_ - pb_)[:5]: print(f"  only A: {r}")
    for r in sorted(pb_ - pa_)[:5]: print(f"  only B: {r}")
    for n, X in (("A", A), ("B", B)):
        h = X["held"]
        print(f"{n}: two-way decisions {len(X['decidedTwoWays'])}; held {h['start']} -> {h['end']} in {h['seconds']} s still; "
              f"waiting at the start {len(h['waitingStart'])}, at the end {len(h['waitingEnd'])}, throughout {h['waitedThroughout'][:6]}; errors {X['errors']}")
    ok = not diff and pa_ == pb_
    print("PASS" if ok else "FAIL")
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run")
    r.add_argument("--from", dest="came_from", choices=("north", "south"), required=True)
    r.add_argument("--draw", type=float, default=25000.0)
    r.add_argument("--site", type=float, default=-27581.0)
    r.add_argument("--travel", type=float, default=20000.0)
    r.add_argument("--step", type=float, default=2500.0)
    r.add_argument("--settle", type=int, default=30)
    r.add_argument("--still", type=int, default=60)
    r.add_argument("--seed", type=int, default=game.DEFAULT_SEED)
    r.add_argument("--old", action="store_true")
    r.add_argument("--out")
    c = sub.add_parser("compare")
    c.add_argument("a"); c.add_argument("b")
    a = ap.parse_args()
    if a.cmd == "run":
        run(a)
        return 0
    return compare(a.a, a.b)


if __name__ == "__main__":
    sys.exit(main())
