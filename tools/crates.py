#!/usr/bin/env python3
"""Crates (playtest 2026-10-09, "crates are not synced"). A crate (Box) is shared as a group with the loot stored in it;
breaking it destroys the crate and the game lets the loot go (mountStuff.OnDestroy). Before v0.66.2 the owner's loot was
then nobody's entity (never shared again) and the other machines destroyed their copies of it with the crate. Two
machines over the relay.
  1 the host breaks its own crate (the game's Break)
  2 the laptop breaks a crate the host owns (a hit on its copy → the owner breaks it)
After each: everything within 12 m of the crate on both machines, by model and place (`mp sitestate`) — the same loot on
both, nothing extra, nothing missing; the crate gone on both.
  tools/crates.py [--no-build] [--relay iad|off] [--old] [--box 62]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, BOX, OLD = arg("--relay", "iad"), arg("--box", 62, int), "--old" in sys.argv
R = 12
VERDICTS = []
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

def ent(s, net): return next((e for e in s("mp entities")["entities"] if e["net"] == net), None)

def site(s, gx, gz):
    v = s(f"mp sitestate {gx} {gz} {R}")
    return [it for it in v["items"] if "Ragdol" not in it[5]]

def compare(a, b, label, tol=1.0):
    left = list(b); missing = []
    for it in a:
        best, bi = None, -1
        for i, o in enumerate(left):
            if o[0] != it[0]: continue
            d = math.dist(it[1:4], o[1:4])
            if best is None or d < best: best, bi = d, i
        if best is None or best > tol: missing.append((it[5], best and round(best, 2)))
        else: left.pop(bi)
    extra = [(o[5], [round(v, 1) for v in o[1:4]]) for o in left]
    verdict(label, not missing and not extra, f"host {len(a)} items, laptop {len(b)}: only on the host {missing}, only on the laptop {extra}")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_(f"set TLDRevamp.Net.Entities.ReshareSurvivors {not OLD}")
        print(f"  {'OLD (fix off)' if OLD else 'NEW (fix on)'}", flush=True)
        time.sleep(5)
        hp = h("gpos")
        L(f"tpground {hp['gx'] + 4} {hp['gz'] - 6}"); time.sleep(3)
        me = h("mp entities")["me"]
        def apart(label):
            he = {e["net"]: e for e in h("mp entities")["entities"] if not e["dormant"]}
            le = {e["net"]: e for e in L("mp entities")["entities"] if not e["dormant"]}
            off = [(n_, he[n_]["name"], round(math.dist(he[n_]["pos"], le[n_]["pos"]), 2), [round(v, 1) for v in he[n_]["pos"]], [round(v, 1) for v in le[n_]["pos"]], le[n_].get("physical"), le[n_].get("byBody"))
                   for n_ in set(he) & set(le) if math.dist(he[n_]["pos"], le[n_]["pos"]) > 1.0]
            print(f"  census {label}: {len(set(he) & set(le))} on both, apart > 1 m: {off}", flush=True)
            for o in off:
                print(f"    host   {he[o[0]]}", flush=True); print(f"    laptop {le[o[0]]}", flush=True)
            hp_ = h("gpos"); c_ = [hp_["gx"], hp_["gz"]]
            one = lambda A, B: [(n_, A[n_]["name"], [round(v, 1) for v in A[n_]["pos"]]) for n_ in set(A) - set(B) if math.hypot(A[n_]["pos"][0] - c_[0], A[n_]["pos"][2] - c_[1]) < 60]
            print(f"    within 60 m loaded on the host only {one(he, le)}; on the laptop only {one(le, he)}", flush=True)
            ha = {e["net"]: e for e in h("mp entities")["entities"]}; la = {e["net"]: e for e in L("mp entities")["entities"]}
            if 100 in ha or 100 in la: print(f"    net 100: host {ha.get(100)}\n             laptop {la.get(100)}", flush=True)
        apart("after the join")
        for n, who in ((1, "host"), (2, "laptop")):
            before = {e["net"] for e in h("mp entities")["entities"]}
            u = h("gpos")   # spawnpile takes this machine's Unity coordinates
            sx, sz = u["ux"] + 6 * n, u["uz"] + 6
            gy = h(f"ground {sx} {sz}").get("y")
            if gy is None: verdict(f"{n} CRATE PLACED", False, "no ground there"); continue
            print(f"  crate {n}:", h(f"spawnpile {BOX} 1 {sx} {gy + 1.0} {sz} 1 1 0 {n}"), flush=True)
            box = []
            until(lambda: bool(box.extend(e["net"] for e in h("mp entities")["entities"] if e["net"] not in before and e["owner"] == me and e["part"] < 0) or box), 15)
            if not box: verdict(f"{n} CRATE SHARED", False, "no new entity on the host"); continue
            net = box[0]
            until(lambda: (ent(L, net) or {}).get("dormant") is False, 20); time.sleep(4)
            apart(f"crate {n} spawned")
            e0 = ent(h, net); bx, bz = e0["pos"][0], e0["pos"][2]
            print(f"  crate {n} net {net} ({e0.get('name')}) at {bx:.1f},{bz:.1f}; laptop has it: {ent(L, net) is not None}", flush=True)
            compare(site(h, bx, bz), site(L, bx, bz), f"{n}a BEFORE THE BREAK: THE SAME ON BOTH")
            r0h, r0l = h("mp entities"), L("mp entities")
            bh, bl = site(h, bx, bz), site(L, bx, bz)
            br = h(f"mp breakent {net}") if who == "host" else L(f"mp breakent {net} 1000000")
            print(f"  {who} breaks it:", br, flush=True)
            it_names = br.get("items", [])
            time.sleep(8)   # the loot falls and comes to rest; re-shares go out
            gone_h, gone_l = ent(h, net) is None, ent(L, net) is None
            verdict(f"{n}b {who.upper()} BROKE IT: THE CRATE GONE ON BOTH", gone_h and gone_l, f"host gone {gone_h}, laptop gone {gone_l}")
            sh, sl = site(h, bx, bz), site(L, bx, bz)
            def onesided(a, b):
                left = list(b); out = []
                for it in a:
                    m = next((k for k, o in enumerate(left) if o[0] == it[0] and math.dist(o[1:4], it[1:4]) < 1.0), None)
                    if m is None: out.append(it)
                    else: left.pop(m)
                return out
            pre = onesided(bh, bl) + onesided(bl, bh)   # differed before the break already (not the crate's doing)
            keep = lambda xs: [it for it in xs if not any(o[0] == it[0] and math.dist(o[1:4], it[1:4]) < 1.0 for o in pre)]
            if pre:
                print(f"  differed before the break (left out): {[it[5] for it in pre]}", flush=True)
                for it in pre:   # a shared object (whose?) or a local one nobody shares?
                    for nm, s2 in (("host", h), ("laptop", L)):
                        near = [(e["net"], e["name"], e["owner"], e.get("proxy")) for e in s2("mp entities")["entities"]
                                if not e["dormant"] and math.dist(e["pos"], it[1:4]) < 0.6]
                        print(f"    {it[5]} at {[round(v, 1) for v in it[1:4]]}: {nm} entities there {near}", flush=True)
                        for n_ in near:   # the same object on the other machine: where, in what state
                            o = ent(L if nm == "host" else h, n_[0])
                            print(f"      net {n_[0]} on the {'laptop' if nm == 'host' else 'host'}: {o and {k: o.get(k) for k in ('pos', 'dormant', 'owner', 'proxy', 'stored', 'held', 'attached', 'parent')}}", flush=True)
            compare(keep(sh), keep(sl), f"{n}c {who.upper()} BROKE IT: THE SAME LOOT ON BOTH")
            nh = onesided(sh, bh)
            print(f"  moved/appeared on the host: {[it[5] for it in nh]}", flush=True)
            loot = [x for x in it_names if x != "Box(Clone)"]
            verdict(f"{n}d {who.upper()} BROKE IT: THE LOOT ON BOTH", all(any(o[5] == x for o in sh) and any(o[5] == x for o in sl) for x in loot), f"loot {loot}")
            def ctr(j, k):
                def f(o):
                    if isinstance(o, dict):
                        if k in o: return o[k]
                        for v in o.values():
                            r = f(v)
                            if r is not None: return r
                return f(j)
            print(f"  reshared +{(ctr(h('mp entities'), 'reshared') or 0) - (ctr(r0h, 'reshared') or 0)} (host), orphans cleared on the laptop "
                  f"+{(ctr(L('mp entities'), 'orphansCleared') or 0) - (ctr(r0l, 'orphansCleared') or 0)}", flush=True)
    finally:
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
