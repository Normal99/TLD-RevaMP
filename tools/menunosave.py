#!/usr/bin/env python3
"""The host goes to the main menu without saving (PlayerSave.WithSaveFile). Two machines over the relay:
  the laptop has a lamp in its pocket and a jerry can of the host's lies by it; the host saves. After the save the laptop
  pockets the can, then the host stops without saving (as the main menu does: a scene load, no
  session-end save) and loads its save again; the laptop joins again.
  A  the world is the saved one: the can lies where it lay, the lamp is back in the laptop's pocket - as many of each
     as at the save (before: the records were written at the stop, newer than the world: the can twice)
  C  the host saves again: the records saved with autosave1 move to autosave2 with it (the autosave rotation)
  tools/menunosave.py [--no-build] [--relay iad|off] [--old: WithSaveFile off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import g

def until(fn, timeout=30, step=0.5):   # the value fn returned (roadkit's returns True)
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
LAMP, CAN = 320, 72
SW = "TLDRevamp.Net.PlayerSave.WithSaveFile"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def ents(s): return s("mp entities")["entities"]
def kinds(s):   # how many lamps and cans this machine has, and where
    out = {"lamp": [], "can": []}
    for e in ents(s):
        n = e.get("name", "")
        k = "lamp" if n.startswith("regizseblampa") else "can" if n.startswith("benzineskanna") else None
        if k: out[k].append((e["net"], e["owner"], e["stored"]))
    return out
def spawn(s, item):
    before = {e["net"] for e in ents(s)}
    s(f"devspawn {item}")
    n = until(lambda: next((e["net"] for e in ents(s) if e["net"] not in before and not e["car"] and not e["proxy"]), None), 15)
    if not n: sys.exit(f"NO ITEM {item}")
    return n
def recfile():   # the host's newest players file (next to a save): name, size, age
    try:
        f = sorted(game.tldenv.DOCS.glob("Saves/*.players"), key=lambda p: p.stat().st_mtime)[-1]
        return f.name, f.stat().st_size, f"{time.time() - f.stat().st_mtime:.0f} s old"
    except Exception as ex: return str(ex)
def join(h, L):
    sid = h("net me")["steamId"]
    for _ in range(3):
        L(f"mp joinid {sid}")
        if until(lambda: L("mp status")["client"]["ready"], 150): return True
        L("mp stop"); time.sleep(3)
    return False

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")   # no save but the test's own `mp save`
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        h(f"set {SW} {not OLD}")
        print(f"  {'OLD (records written on leave and stop)' if OLD else 'CURRENT BUILD'}", flush=True)
        rp = h("roadpoint"); x, z = rp["gx"], rp["gz"]
        h(f"tpground {x} {z}"); L(f"tpground {x + 3} {z}"); time.sleep(4)
        lamp = spawn(L, LAMP); time.sleep(1.5)
        print("  laptop pockets its lamp:", L(f"mp pickupnet {lamp}"), flush=True); time.sleep(2.5)
        n_inv = int(g(L, "mainscript.s.player.inventory.Count")); s0 = int(g(L, "mainscript.s.player.selectedInv"))
        L(f"call mainscript.s.player.InvSwitchTo {(s0 + 1) % n_inv} False"); time.sleep(2)   # the lamp stored, an empty hand
        h(f"tpground {x + 3} {z + 2}"); time.sleep(2)
        can = spawn(h, CAN); time.sleep(3)
        h(f"tpground {x} {z}"); time.sleep(2)
        print("  record:", L("mp playersave record").get("last"), flush=True); time.sleep(3)
        print("  host saves:", h("mp save"), "| before:", kinds(h), "| file:", recfile(), flush=True)

        # ---- after the save: the laptop pockets the host's can (the lamp stays in its pocket)
        k0 = kinds(h)
        print("  laptop pockets the can:", L(f"mp pickupnet {can}"), flush=True); time.sleep(2.5)
        L(f"call mainscript.s.player.InvSwitchTo {(s0 + 2) % n_inv} False"); time.sleep(2)
        L("mp playersave record"); time.sleep(3)
        print("  now (host):", kinds(h), "| laptop:", kinds(L), flush=True)

        # ---- the host stops without saving and loads its save
        print("  host stops (no save):", h("mp stop"), flush=True); time.sleep(5)
        print("  host's records:", h("mp playersave").get("last"), "| file:", recfile(), flush=True)
        L("mp stop"); time.sleep(3)
        print("  host loads its save:", h("loadsave autosave1"), flush=True)
        until(lambda: h("get DataFromMenuScript.s.mainmenu")["value"] is False and h("get mainscript.s.player")["value"] is not None, 180)
        time.sleep(15); h("set TLDRevamp.TestSafety.BlockAutoSave True")
        h(f"set {SW} {not OLD}")
        if RELAY != "off": relay_both(h, L, RELAY)
        print("  file before hosting again:", recfile(), flush=True)
        h("mp host 27070"); time.sleep(3)
        print("  host's records:", h("mp playersave"), flush=True)
        if not join(h, L): sys.exit("LAPTOP NEVER REJOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True"); time.sleep(12)
        k = kinds(h)
        print("  after:", k, "| laptop:", kinds(L), "| laptop's restore:", L("mp playersave").get("last"), flush=True)
        verdict("A: AS MANY LAMPS AND CANS AS AT THE SAVE (THE SAVED WORLD, THE RECORDS SAVED WITH IT)",
                len(k["lamp"]) == len(k0["lamp"]) and len(k["can"]) == len(k0["can"]),
                f"at the save lamps {len(k0['lamp'])} cans {len(k0['can'])}; now lamps {k['lamp']}, cans {k['can']}")
        if not OLD:   # ---- the records move with the autosave rotation (autosave1 -> 2), as the lease tables do
            sv = game.tldenv.DOCS / "Saves"
            a1, a2 = sv / "TLDsaveData_autosave1.tlds.players", sv / "TLDsaveData_autosave2.tlds.players"
            b1 = a1.read_bytes() if a1.exists() else None
            print("  host saves again:", h("mp save"), flush=True); time.sleep(2)
            verdict("C: THE SAVE'S RECORDS MOVE TO AUTOSAVE2, NEW ONES WITH AUTOSAVE1",
                    b1 is not None and a2.exists() and a2.read_bytes() == b1 and a1.exists(),
                    f"autosave1 before {len(b1) if b1 else None} B; now autosave2 {a2.stat().st_size if a2.exists() else None} B, autosave1 {a1.stat().st_size if a1.exists() else None} B")
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        try: h(f"set {SW} True")
        except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print(f"\n{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed", flush=True)
