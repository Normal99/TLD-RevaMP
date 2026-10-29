#!/usr/bin/env python3
"""The shipwreck barrels of a real world on two machines (playtest 2026-10-28: "fluids in barrels do not sync, they are
different at the shipwreck"). The host loads a world save (TLD_PLAYSAVE_DIR holds TLDsave*_<name>.*, TLD_PLAYSAVE the
name, default autosave1) and hosts it, the joiner joins and walks to the barrels. Every BARREL the two machines both
have must show the same fluids: first as they arrive, then after the joiner has carried some, then the host.
  tools/barrelworld.py [--no-build] [--relay iad|off] [--center x z]"""
import sys, os, time, math, shutil, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, join_both, DESKTOP_IP
from roadkit import until

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY = arg("--relay", "off")
if RELAY == "off": RELAY = None
SRC = pathlib.Path(os.environ.get("TLD_PLAYSAVE_DIR", "")).expanduser()
SRC_NAME = os.environ.get("TLD_PLAYSAVE", "autosave1")
SAVE = "shipwreck"

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def ents(s): return {e["net"]: e for e in s("mp entities 400")["entities"]}

def tanks(s, net):
    try: r = s(f"mp fluids {net}")
    except RuntimeError: return None
    if "error" in r: return None
    return [(t["ti"], t.get("by", ""), t.get("amount")) for t in r["tanks"]]

def same(a, b):
    if a is None or b is None or len(a) != len(b): return False
    for x, y in zip(a, b):
        if x[2] is None or y[2] is None or abs(x[2] - y[2]) > 0.05: return False
        if sorted(x[1].split()) != sorted(y[1].split()): return False
    return True

def compare(tag, h, L, wait=8):
    time.sleep(wait)
    eh, el = ents(h), ents(L)
    nets = [n for n, e in eh.items() if e.get("name", "").startswith("BARREL") and not e.get("dormant") and not e.get("stored")]
    bad, seen = [], 0
    for n in nets:
        if n not in el or el[n].get("dormant"): continue
        seen += 1
        a, c = tanks(h, n), tanks(L, n)
        if not same(a, c): bad.append((n, f"owner {eh[n]['owner']}/{el[n]['owner']}", a, c))
    for x in bad[:12]: print("   DIFFERENT:", x, flush=True)
    verdict(f"{tag}: EVERY BARREL BOTH HAVE SHOWS THE SAME FLUIDS", seen > 0 and not bad, f"{seen} barrels on both, {len(bad)} differ (host has {len(nets)})")
    return nets

FRESH = "--fresh" in sys.argv   # no save: a new world (TLD_SEED), both players walk to --center and the game makes the barrels now
if not FRESH and (not SRC.is_dir() or not list(SRC.glob(f"TLDsave*_{SRC_NAME}.*"))): sys.exit(f"no TLDsave*_{SRC_NAME}.* in TLD_PLAYSAVE_DIR ({SRC})")
L = Agent()
ensure_both("--no-build" not in sys.argv)
copied = []
with game.ProtectedSession():
    try:
        for f in ([] if FRESH else SRC.glob(f"TLDsave*_{SRC_NAME}.*")):
            dst = game.SAVES / f.name.replace(f"_{SRC_NAME}.", f"_{SAVE}.")
            shutil.copy2(f, dst); copied.append(dst)
        print(f"  the save's {len(copied)} files copied into the test prefix", flush=True)
        if FRESH:
            import seeded, threading
            t = threading.Thread(target=lambda: L("!seeded")); t.start()
            seeded.restart_same_seed(); t.join()
            from bench import Bridge
            h = Bridge()
        else:
            L("!seeded")
            h = game.loadsave(SAVE)
        for s_ in (h, L):
            s_("set TLDRevamp.TestSafety.BlockAutoSave True"); s_("bg on"); s_("set UnityEngine.QualitySettings.vSyncCount 0")
        me = h("gpos"); print(f"  host in the save at {me['gx']:.0f},{me['gz']:.0f}", flush=True)
        if RELAY: print("relay:", h(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
        if not join_both(h, L): sys.exit("JOINER NEVER JOINED")
        time.sleep(8)
        # the barrels: the host's own list, nearest the centre (the shipwreck) first
        if "--center" in sys.argv:
            i = sys.argv.index("--center"); cx, cz = float(sys.argv[i + 1]), float(sys.argv[i + 2])
        else:
            bs = [e for e in ents(h).values() if e.get("name", "").startswith("BARREL")]
            print(f"  barrels the host lists near it: {len(bs)}", flush=True)
            cx, cz = (me["gx"], me["gz"]) if not bs else (sum(e["pos"][0] for e in bs) / len(bs), sum(e["pos"][2] for e in bs) / len(bs))
        print(f"  going to {cx:.0f},{cz:.0f}", flush=True)
        h(f"tpground {cx} {cz}"); time.sleep(25 if FRESH else 0); L(f"tpground {cx + 5} {cz}"); time.sleep(30 if FRESH else 20)
        print("  barrels now: host", len([1 for e in ents(h).values() if e.get("name", "").startswith("BARREL")]), "joiner", len([1 for e in ents(L).values() if e.get("name", "").startswith("BARREL")]), flush=True)
        nets = compare("1 AS THEY ARRIVE", h, L, 10)
        for who, name in ((L, "joiner"), (h, "host")):
            for k in range(4):
                es = ents(who); gp = who("gpos")
                near = sorted((math.hypot(e["pos"][0] - gp["gx"], e["pos"][2] - gp["gz"]), n) for n, e in es.items() if e.get("name", "").startswith("BARREL") and not e.get("dormant") and not e.get("stored"))
                if not near: break
                n = near[k % len(near)][1]
                print(f"  {name} picks up barrel {n}:", who(f"mp holdnet {n}"), flush=True); time.sleep(2)
                if k % 2 == 0: print(f"  {name} lets go:", who("dropheld"), flush=True)
                else: print(f"  {name} throws:", who(f"mp throwpoint {gp['gx'] + 14} {gp['gz']} 0.8"), flush=True)
                time.sleep(3)
            compare(f"2 AFTER THE {name.upper()} CARRIED AND THREW SOME", h, L, 10)
        L("mp stop"); h("mp stop")
        if RELAY: h("net relay off"); L("net relay off")
    finally:
        for f in copied:
            try: f.unlink()
            except OSError: pass
        L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}  {d}")
