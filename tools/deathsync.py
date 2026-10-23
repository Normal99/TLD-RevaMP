#!/usr/bin/env python3
"""Death/respawn sync (#31): the laptop player dies; the host sees the actual PlayerRagdol, its limbs track the
fall and settle, and carried items drop at the death point rather than the ragdoll endpoint.

  tools/deathsync.py [--no-build] [--relay iad|off] [--ab]

--ab runs the four switch combinations on the same build and also requires the bridge's deterministic deathfling."""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP

def until(fn, timeout=25, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def body(who, pid):
    try:
        for p in who("mp players")["players"]:
            if p["id"] == pid: return p
    except Exception: pass
    return None

ensure_both("--no-build" not in sys.argv)
AB = "--ab" in sys.argv
L = Agent()
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail)); print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  {detail}" if detail else ""), flush=True)
def set_switches(ragdoll, anchor, host, client):
    for s in (host, client):
        s(f"set TLDRevamp.Net.Entities.DeathRagdollShare {ragdoll}")
        s(f"set TLDRevamp.Net.SessionRules.DeathDropAnchor {anchor}")
def corpse_for_remote():
    # A corpse is a PlayerRagdol-owning entity, not the remote player's ordinary pose capsule.
    for e in b("mp entities").get("entities", []):
        if "Ragdol" not in e.get("name", ""): continue   # one bridge call per entity took the whole 15 min test budget
        try:
            n = int(e.get("net", 0)); st = b(f"mp ragdoll {n}")
            if st.get("name", "").startswith("PlayerRagdol"):
                return n, st
        except Exception:
            pass
    return None, None
def item_positions(side):
    rows = side("mp itemslist 0 0 999999999")["items"]
    return {int(r[0]): tuple(float(v) for v in r[1:4]) for r in rows if len(r) >= 4}
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
        if RELAY != "off": b(f"net relay {RELAY}"); L(f"net relay {RELAY}")
        b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY != "off": L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 150)
        time.sleep(4)
        for ragdoll, anchor in (((True, True), (False, True), (True, False), (False, False)) if AB else ((True, True),)):
            set_switches(ragdoll, anchor, b, L)
            # A deterministic fling: the bridge clears god/fly/ground immunity, creates and grabs two carried items,
            # then calls the game's own Death. A few seconds give the shares time to reach the host before the fall.
            L("set kaposztaleves.s.settings.god False")
            fling = L("mp deathfling 32 4 0 320 63")
            time.sleep(2)
            verdict("REAL DEATH VIA Death()", fling.get("died") is True, str(fling))
            anchor_xyz = fling.get("anchor")
            ragnet, rag = None, None
            for _ in range(40):
                time.sleep(.5)
                ragnet, rag = corpse_for_remote()
                if ragnet is not None: break
            if ragdoll:
                verdict("CORPSE SHARE ON REAL DEATH", ragnet is not None, f"net {ragnet}" if ragnet else "no PlayerRagdol entity on host")
                samples = []
                if ragnet is not None:
                    for _ in range(12):
                        time.sleep(.25)
                        st = b(f"mp ragdoll {ragnet}")
                        if st.get("limbs"): samples.append(tuple(st["limbs"]))
                    verdict("CORPSE LIMBS STREAM", len(samples) >= 3 and len({str(x) for x in samples}) > 1, f"{len(samples)} samples")
            else:
                verdict("CORPSE SHARE DISABLED PATH", True, f"found local/world corpse entity {ragnet}" if ragnet is not None else "no corpse assertion")
            dead_at = anchor_xyz
            r0 = L("get TLDRevamp.Net.SessionRules.Respawns")["value"]
            respawned = until(lambda: L("get TLDRevamp.Net.SessionRules.Respawns")["value"] > r0, 90)
            time.sleep(3)
            stats = L("mp deathstats")
            after = item_positions(L)
            # All carried items should be near the recorded anchor if the anchor path is on. With it off, the test
            # documents the old ragdoll-endpoint behaviour while still requiring a successful respawn.
            near = 0
            for p in after.values():
                if dead_at and ((p[0]-dead_at[0])**2 + (p[2]-dead_at[2])**2) ** .5 <= 2.0: near += 1
            verdict("RESPAWNED IN SESSION", respawned and stats.get("hasAnchor") is False, f"anchors={stats.get('anchors')} drops={stats.get('anchorDrops')}")
            if anchor:
                verdict("DETERMINISTIC ITEMS CREATED", len(fling.get("spawned", [])) >= 2, f"spawned {fling.get('spawned')}")
                verdict("DEATH ANCHOR CAPTURED", stats.get("anchors", 0) > 0 and anchor_xyz is not None, str(stats))
                verdict("DROP ANCHOR APPLIED", stats.get("anchorDrops", 0) > 0, str(stats))
                print(f"  {near} item(s) of any kind within 2 m of {anchor_xyz}", flush=True)
                # this death's own carried things, by network id, on both machines: not "any two items nearby"
                # a joiner's spawns get their network ids from the host after the fling: read them now
                nets = [p[1] for p in L("mp deathinfo").get("itemIds", [])]
                print("  the death's things [save, net]:", L("mp deathinfo").get("itemIds"), flush=True)
                for name, side in (("host", b), ("joiner", L)):
                    try:
                        rows = {r["net"]: r["pos"] for r in side("mp shared")["items"]}
                        ds = [round(((rows[n][0]-dead_at[0])**2 + (rows[n][2]-dead_at[2])**2) ** .5, 2) if n in rows and dead_at else None for n in nets]
                        verdict(f"{name.upper()}: CARRIED ITEMS DROPPED AT ANCHOR", len(ds) >= 2 and all(d is not None and d <= 2.0 for d in ds),
                                f"the death's things {nets} lie {ds} m from {anchor_xyz}")
                    except Exception as e:
                        verdict(f"{name.upper()}: CARRIED ITEMS DROPPED AT ANCHOR", False, str(e)[:200])
                verdict("PLAYER SAVE RECORD PRESENT", "deathAnchor" in L("mp playersave"), L("mp playersave").get("last", ""))
            else:
                print("  A/B OFF: old behaviour path exercised; see stats above", flush=True)
            L("set kaposztaleves.s.settings.god True")
            L("set TLDRevamp.TestSafety.BlockAutoSave True")
            b("mp stop"); L("mp stop")
            b("mp host 27070")
            if RELAY != "off": L(f"mp joinid {b('net me')['steamId']}")
            else: L(f"mp join {DESKTOP_IP}:27070")
            until(lambda: L("mp status")["client"]["ready"], 150); time.sleep(3)
        ok = all(v[1] for v in VERDICTS)
        print("B8 VERDICT:", "PASS" if ok else "FAIL")
        for n, passed, detail in VERDICTS: print(f"  {'PASS' if passed else 'FAIL'}  {n}")
        b("mp stop"); L("mp stop")
finally:
    L.close()
