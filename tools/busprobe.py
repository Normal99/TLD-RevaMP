#!/usr/bin/env python3
"""One machine: does a bus's trapdoor survive the game's item streaming? (farworld ho3: a BusTrapDoor vanished each
time a machine streamed a building site out.) Fresh seeded game; a full bus (item 106) spawned by the road; every
item within 40 m listed by model; the player goes 5 km away (beyond itemRemoveDist: the bus goes into the save's far
store) and comes back. Counts per model before / stored / after, and the trapdoor rows (107) in full.
  --mp: two machines over relay iad — the LAPTOP (a client) spawns, stores and reloads the bus 5 km from the host
        (farworld ho3: the client streaming a site out lost the trapdoor); the host's far store is listed too
  --diag: single machine, the share group of the spawned bus + every item near it outside the group (how it's held)
  tools/busprobe.py [--no-build] [--id 106] [--mp] [--diag]"""
import sys, time, collections, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from roadkit import until, tp

BUS = int(sys.argv[sys.argv.index("--id") + 1]) if "--id" in sys.argv else 106
MP = "--mp" in sys.argv
if MP:
    from mplib import Agent, ensure_both, seeded_both
    ensure_both("--no-build" not in sys.argv)
    L = Agent()
    with game.ProtectedSession():
        h = seeded_both(L)
        for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
        h("net relay iad"); L("net relay iad")
        h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        b = L
        import math
        # follow the road 2.5 km out (250 m hops, each onto the nearest road point): a straight line leaves the road and
        # a bus parked on a slope can roll out of the 40 m view
        rp = b("roadpoint"); start = (rp["gx"], rp["gz"]); hops = 0
        while math.dist(start, (rp["gx"], rp["gz"])) < 2500 and hops < 30:
            D = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
            # keep heading away from the start (a road point's yaw can point either way)
            ox, oz = rp["gx"] - start[0], rp["gz"] - start[1]
            if hops > 0 and ox * D[0] + oz * D[1] < 0: D = (-D[0], -D[1])
            last = rp
            tp(b, rp["gx"] + D[0] * 250, rp["gz"] + D[1] * 250); hops += 1
            nxt = None
            for _ in range(20):
                try: nxt = b("roadpoint"); break
                except RuntimeError: time.sleep(1)
            if nxt is None:
                if math.dist(start, (last["gx"], last["gz"])) >= 1500: rp = last; break   # far enough: the last road point
                sys.exit(f"lost the road after {hops} hops")
            rp = nxt
        print(f"laptop on the road {math.dist(start, (rp['gx'], rp['gz'])):.0f} m from the host after {hops} hops:", tp(b, rp["gx"], rp["gz"]), flush=True)
        time.sleep(4)
        cx, cz = rp["gx"], rp["gz"]
        print("on its road:", tp(b, cx, cz), flush=True); time.sleep(2)
        print("bus (laptop spawns):", b(f"devspawn {BUS}"), flush=True); time.sleep(6)
        def view(tag):
            v = b(f"mp sitestate {cx} {cz} 40")["items"]
            c = collections.Counter(r[0] for r in v)
            print(f"{tag}: {len(v)} items; trapdoors {[r for r in v if r[0] == 107]}", flush=True)
            return v, c
        v0, c0 = view("SPAWNED")
        print("host far store:", h(f"mp farstore {cx} {cz} 40"), flush=True)
        print("laptop 5 km away:", tp(b, cx + 5000, cz), flush=True); time.sleep(8)
        print("STORED laptop far store:", b(f"mp farstore {cx} {cz} 40"), flush=True)
        hs = h(f"mp farstore {cx} {cz} 40"); ls = b(f"mp farstore {cx} {cz} 40")
        print("STORED host far store  :", hs, flush=True)
        print("VERDICT HOST COPY:", "COMPLETE" if hs.get("models") == ls.get("models") else
              f"DIFFERS (host {hs.get('records')} records, laptop {ls.get('records')})", flush=True)
        print("laptop share stats:", {k: b("mp entities").get(k) for k in ("spawnShared",)}, flush=True)
        print("back:", tp(b, cx, cz), flush=True); time.sleep(8)
        v1, c1 = view("BACK")
        diff = {k: (c0[k], c1[k]) for k in set(c0) | set(c1) if c0[k] != c1[k]}
        print("\nmodel count changes (before, after):", diff or "none", flush=True)
        print("VERDICT:", "TRAPDOOR SURVIVES" if c1[107] == c0[107] else f"TRAPDOOR LOST ({c0[107]} -> {c1[107]})", flush=True)
        L("mp stop"); h("mp stop"); h("net relay off"); L("net relay off")
    L.close()
    sys.exit(0)

if "--no-build" not in sys.argv: game.build()
game.quit_game(); game.launch()
with game.ProtectedSession():
    seeded.restart_same_seed()
    b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True")
    rp = b("roadpoint"); cx, cz = rp["gx"], rp["gz"]
    print("road:", tp(b, cx, cz), flush=True); time.sleep(2)
    near0 = {tuple(r[1:4]) for r in b(f"mp sitestate {cx} {cz} 40")["items"]}
    print("bus:", b(f"devspawn {BUS}"), flush=True); time.sleep(6)
    def view(tag):
        v = b(f"mp sitestate {cx} {cz} 40")["items"]
        c = collections.Counter(r[0] for r in v)
        print(f"{tag}: {len(v)} items; trapdoors {[r for r in v if r[0] == 107]}", flush=True)
        return v, c
    v0, c0 = view("SPAWNED")
    if "--diag" in sys.argv:
        import json
        tp(b, cx + 3, cz); time.sleep(1)
        d = b("itemsnap groupdiag 15")
        print("GROUP:", d.get("car"), d.get("group"), "members:", d.get("members"), flush=True)
        for row in d.get("outside", []): print("  OUTSIDE:", json.dumps(row), flush=True)
        for row in d.get("roots", []): print("  ROOT:", json.dumps(row), flush=True)
        rt = b("itemsnap group")
        print("ROUND TRIP (copy from the record, 6 m away):", {k: rt.get(k) for k in ("car", "items", "spawned", "identical", "diffAt")}, flush=True)
        if not rt.get("identical"): print("  orig:", rt.get("orig"), "\n  copy:", rt.get("copy"), flush=True)
        time.sleep(3)
        cp = b(f"mp sitestate {cx} {cz} 40")["items"]
        print("after the copy:", len(cp), "items;", collections.Counter(r[0] for r in cp), flush=True)
        sys.exit(0)
    print("far store before leaving:", b(f"mp farstore {cx} {cz} 40"), flush=True)
    print("5 km away:", tp(b, cx + 5000, cz), flush=True); time.sleep(8)
    fs = b(f"mp farstore {cx} {cz} 40")
    print("STORED far store:", fs, flush=True)
    print("back:", tp(b, cx, cz), flush=True); time.sleep(8)
    v1, c1 = view("BACK")
    diff = {k: (c0[k], c1[k]) for k in set(c0) | set(c1) if c0[k] != c1[k]}
    print("\nmodel count changes (before, after):", diff or "none", flush=True)
    print("VERDICT:", "TRAPDOOR SURVIVES" if c1[107] == c0[107] else f"TRAPDOOR LOST ({c0[107]} -> {c1[107]})", flush=True)
