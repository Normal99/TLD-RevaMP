#!/usr/bin/env python3
"""Death/respawn sync (#31), the host dies: the desktop (host) is flung and killed with two carried items; the laptop (joiner)
must show the same corpse, limb for limb, while it flies and settles, and the items must drop at the death point,
not where the body ended up. Only read-only commands on the joiner (the session keeps its host-only developer policy).
It complements tools/deathsync.py (the joiner dies), which needs the joiner to have developer access.

  tools/deathsync_host.py [vx vy vz]     (default 10 15 0: the fling the host's body gets)"""
import sys, time, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both

def until(fn, timeout=25, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

V = []
def verdict(name, ok, detail=""):
    V.append((name, ok)); print(f"  {'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def dist(a, b, flat=False):
    return (((a[0]-b[0])**2 + (0 if flat else (a[1]-b[1])**2) + (a[2]-b[2])**2)) ** .5

def limbs(side, net):
    try: r = side(f"mp ragdoll {net}")
    except Exception as e: return None, str(e)[:160]
    if "limbs" not in r: return None, str(r)[:160]
    return [tuple(l[:3]) for l in r["limbs"] if l != "null" and l is not None], r.get("following")

def centre(ls): return tuple(sum(l[k] for l in ls) / len(ls) for k in range(3))

ARGS = [a for a in sys.argv[1:] if not a.startswith("--")]
VEL = " ".join(ARGS[:3]) if len(ARGS) >= 3 else "10 15 0"
ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        b("net relay iad"); L("net relay iad")
        b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        L(f"mp joinid {b('net me')['steamId']}")
        verdict("JOINED", until(lambda: L("mp status")["client"]["ready"], 150))
        time.sleep(5)
        r0 = b("get TLDRevamp.Net.SessionRules.Respawns")["value"]
        b("set kaposztaleves.s.settings.god False")
        t0 = time.time()
        fling = b(f"mp deathfling {VEL} 320 63")
        print("fling:", json.dumps(fling)[:900], flush=True)
        verdict("HOST DIED", fling.get("died") is True and fling.get("flung") is True)
        anchor = fling.get("anchor")
        net = fling.get("corpseNetId") or 0
        verdict("ANCHOR CAPTURED", anchor is not None, str(anchor))
        if not net:
            time.sleep(1); net = b("mp deathinfo").get("corpseNetId", 0)
        verdict("CORPSE SHARED", net > 0, f"net {net}")
        # the corpse on both machines while it flies and settles, until the respawn takes it away
        gaps, centres, jfollow, jerr = [], [], None, None
        while time.time() - t0 < 30:
            h, _ = limbs(b, net)
            j, jf = limbs(L, net)
            if h is None: break                          # gone on the host: the respawn released it
            if j is None: jerr = jf
            else:
                jfollow = jf
                if len(j) == len(h): gaps.append(max(dist(a, c) for a, c in zip(h, j)))
            centres.append((round(time.time() - t0, 1), centre(h)))
            time.sleep(0.25)
        if jerr: print("joiner read error:", jerr, flush=True)
        for t, c in centres[::3]:
            print(f"  t+{t}s host corpse centre {tuple(round(v, 2) for v in c)}" +
                  (f", {dist(c, anchor, True):.1f} m from the death point" if anchor else ""), flush=True)
        verdict("JOINER SHOWS THE CORPSE", len(gaps) >= 3, f"{len(gaps)} samples, following={jfollow}")
        if gaps:
            settled = gaps[-3:]
            print("  largest limb gap per sample (m):", [round(g, 2) for g in gaps], flush=True)
            med = sorted(gaps)[len(gaps) // 2]
            verdict("LIMBS MATCH WHILE IT FLIES", med < 1.0, f"median {med:.2f} m")
            verdict("LIMBS MATCH ONCE SETTLED", max(settled) < 0.3, f"last {[round(g, 2) for g in settled]}")
        if centres and anchor:
            print(f"  the body ended {dist(centres[-1][1], anchor, True):.1f} m from the death point", flush=True)
        respawned = until(lambda: b("get TLDRevamp.Net.SessionRules.Respawns")["value"] > r0, 30, 0.25)
        verdict("RESPAWNED", respawned, f"{time.time() - t0:.0f} s after the death")
        time.sleep(2)
        st = b("mp deathstats")
        verdict("DROP ANCHOR APPLIED", st.get("anchorDrops", 0) > 0, f"drops {st.get('anchorDrops')} fallbacks {st.get('fallbacks')}")
        print("death items [save, net]:", b("mp deathinfo").get("itemIds"), flush=True)
        def near(side):
            rows = side("mp itemslist 0 0 999999999")["items"]
            out = []
            for r in rows:
                if len(r) < 4 or not anchor: continue
                d = dist(tuple(float(v) for v in r[1:4]), anchor, True)
                if d <= 2.0: out.append((int(r[0]), round(d, 2)))
            return out
        # this death's own two things, by network id, on both machines
        nets = [p[1] for p in b("mp deathinfo").get("itemIds", [])]
        for name, side in (("host", b), ("joiner", L)):
            try:
                rows = {r["net"]: r for r in side("mp shared")["items"]}
                ds = [round(dist(rows[n]["pos"], anchor, True), 2) if n in rows else None for n in nets]
                print(f"{name}: the death's things {nets} lie {ds} m from the death point", flush=True)
                verdict(f"{name.upper()}: CARRIED THINGS AT THE DEATH POINT", len(ds) >= 2 and all(d is not None and d <= 2.0 for d in ds), str(ds))
            except Exception as e:
                verdict(f"{name.upper()}: CARRIED THINGS AT THE DEATH POINT", False, str(e)[:200])
        print("host respawned at", b("gpos"), flush=True)
        try: gone = "limbs" not in L(f"mp ragdoll {net}")
        except Exception as e: gone = "no such entity" in str(e)
        verdict("CORPSE GONE ON THE JOINER TOO", gone)
        b("set kaposztaleves.s.settings.god True")
        b("mp stop"); L("mp stop")
        print("VERDICT:", "PASS" if all(v[1] for v in V) else "FAIL")
finally:
    L.close()
