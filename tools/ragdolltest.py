#!/usr/bin/env python3
"""Bodies of limbs in MP (v0.64.12, Entities.Ragdoll). Two machines over the relay: desktop hosts, laptop joins.
States carried only an item's own transform; a copy held every limb still. A dead player's body (PlayerRagdol, no
body of its own) stayed on other screens in the pose it spawned in; a dead creature fell its own way on every machine.
  A a dead player's body (PlayerRagdol) spawned by the host falls: the laptop's copy limbs where the host's are
  B a mansion worker (Munkas01) of the host dies: the laptop's copy dies too and its limbs lie where the host's do
  C the laptop grabs a limb of that dead worker: it takes the body over, the host's copy follows the laptop's
Limbs compared by global position, every limb, at rest: worst limb < 0.10 m.
  tools/ragdolltest.py [--no-build] [--relay iad|off]"""
import sys, os, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
VERDICTS = []
TOL = 0.10

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}

def spawn(s, who, prefab):
    before = set(ents(s))
    print(f"  {who} spawns {prefab}:", s(f"devspawn {prefab}"), flush=True)
    found = []
    until(lambda: found.extend(n for n, e in ents(s).items() if n not in before and e["name"].startswith(prefab) and not e["proxy"]) or found, 20)
    if not found: raise RuntimeError(f"{who}: {prefab} never became an entity")
    return found[0]

def compare(tag, a, b, an, bn, net):
    ra, rb = a(f"mp ragdoll {net}"), b(f"mp ragdoll {net}")
    if "error" in ra or "error" in rb: print(f"  {tag}: {an} {ra} / {bn} {rb}", flush=True); return None, ra, rb
    d = [math.dist(x[:3], y[:3]) for x, y in zip(ra["limbs"], rb["limbs"]) if x and y]
    worst = max(d) if d else 99
    print(f"  {tag}: worst limb {worst:.3f} m, mean {sum(d) / max(len(d), 1):.3f} m | {an}: proxy {ra['proxy']} limp {ra['limp']} sent {ra['sent']} | "
          f"{bn}: proxy {rb['proxy']} limp {rb['limp']} following {rb['following']} applied {rb['applied']} kinematic {sum(1 for l in rb['limbs'] if l and l[3])}/{len(rb['limbs'])}", flush=True)
    return worst, ra, rb

def spread(r): # how far the limbs are from the item's transform: a body that fell/moved
    return max(math.dist(l[:3], r["root"]) for l in r["limbs"] if l)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    gods = {}
    for name, s_ in (("host", h), ("laptop", L)):
        gods[name] = (s_, s_("get kaposztaleves.s.settings.god").get("value"))
        s_("set kaposztaleves.s.settings.god True")
    try:
        hp = h("gpos")
        h(f"tpground {hp['gx'] + 15} {hp['gz']}"); time.sleep(3)
        hp = h("gpos")
        L(f"tpground {hp['gx'] + 4} {hp['gz'] + 6}"); time.sleep(3)
        # A
        net = spawn(h, "host", "PlayerRagdol")
        if not until(lambda: net in ents(L), 20): verdict("A", False, "the laptop never got the body")
        else:
            time.sleep(1.0)
            compare("A falling (t+1 s)", h, L, "host", "laptop", net)
            time.sleep(6)
            w, ra, rb = compare("A at rest", h, L, "host", "laptop", net)
            verdict("A A DEAD PLAYER'S BODY LIES THE SAME", w is not None and w < TOL and rb["following"],
                    f"worst limb {w if w is None else round(w, 3)} m; limbs {spread(ra):.2f} m from the item's transform on the host")
        # B
        net = spawn(h, "host", "Munkas01")
        if not until(lambda: net in ents(L), 20): verdict("B", False, "the laptop never got the worker")
        else:
            time.sleep(3)
            print("  host kills it:", h(f"mp ragdoll {net} kill"), flush=True)
            time.sleep(1.0)
            compare("B falling (t+1 s)", h, L, "host", "laptop", net)
            time.sleep(6)
            w, ra, rb = compare("B at rest", h, L, "host", "laptop", net)
            verdict("B A DEAD CREATURE LIES THE SAME", w is not None and w < TOL and rb["limp"] and rb["following"],
                    f"worst limb {w if w is None else round(w, 3)} m, the laptop's copy dead {rb.get('limp')}")
            # C
            lb = ra["limbs"][0]
            L(f"tpground {lb[0] + 1.2} {lb[2] + 1.2}"); time.sleep(3)
            g = L(f"mp ragdoll {net} grab"); print("  laptop grabs:", g, flush=True)
            time.sleep(4)
            le, he = ents(L)[net], ents(h)[net]
            w, rl, rh = compare("C held by the laptop", L, h, "laptop", "host", net)
            verdict("C GRABBING A LIMB TAKES THE BODY", not le["proxy"] and he["proxy"] and w is not None and w < 0.3,
                    f"laptop owns {not le['proxy']}, host copy {he['proxy']}, worst limb {w if w is None else round(w, 3)} m (held: moving)")
    finally:
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
