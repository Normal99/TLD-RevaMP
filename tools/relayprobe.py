#!/usr/bin/env python3
"""Both machines: Steam IDs and ping to every Valve relay datacenter (the relay test picks its cluster from this).
Through one pinned relay the player-to-player round trip is about ping(A→relay) + ping(B→relay).
  tools/relayprobe.py [--no-build]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from bench import Bridge
from mplib import Agent, ensure_both

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    b = Bridge()
    print("desktop:", b("net me"), " laptop:", L("net me"), flush=True)
    pops = {}
    t0 = time.time()
    while time.time() - t0 < 30:
        pops = {name: side("net pops") for name, side in (("desktop", b), ("laptop", L))}
        if all(sum(1 for p in v["pops"] if p["ping"] > 0) >= 5 for v in pops.values()): break
        time.sleep(2)
    d = {p["pop"]: p for p in pops["desktop"]["pops"]}; l = {p["pop"]: p for p in pops["laptop"]["pops"]}
    rows = sorted((d[k]["ping"] + l.get(k, {"ping": -1})["ping"], k) for k in d if d[k]["ping"] > 0 and l.get(k, {"ping": -1})["ping"] > 0)
    print(f"{len(d)} datacenters; estimated player↔player round trip through each (desktop ping + laptop ping):")
    for est, k in rows:
        print(f"  {k:5s} ~{est:4d} ms   (desktop {d[k]['ping']} via {d[k]['via']}, laptop {l[k]['ping']} via {l[k]['via']})")
finally:
    L.close()
