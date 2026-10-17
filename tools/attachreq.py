#!/usr/bin/env python3
"""A part bolted on by a player who doesn't own it (buglist2m 3b in the full sequence, 2026-10-09: the laptop's player
bolted the host's hubcap back on while the host's car had just claimed it — bolted on the laptop, loose 3 m away on
the host, for good). Two machines over the relay; the host's Car06Full.
  A taken back : the laptop's player takes the hubcap off (theirs, in hand) and drops it; the host picks it up and
                 drops it (the host's now: the laptop's is a copy); the laptop's player bolts the copy on → the host's
                 game bolts it too (AttachReq: the owner does it); both: in the slot, the same owner.
  B refused    : the same, but the host keeps it in hand while the laptop's player bolts the copy on → the owner can't:
                 it says the part is off (DetachSync) → off the car on both machines, not bolted on one only.
  C car taken back: the laptop claims the host's car, takes the hubcap off and bolts it back (its part on its car
                 now); the host claims the car back → the hubcap goes with it (the server's owner cascade); the host
                 takes it off → loose on both, at the same place (playround.py R3: bolted on one machine, loose 6 m
                 away on the other — the part stayed the laptop's).
  tools/attachreq.py [--no-build] [--relay iad|off] [--only A,B,C] [--old: no requests (A, B), no cascade (C)]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
ONLY = arg("--only", "A,B,C").split(",")
T = "TLDRevamp.Net.Entities"; SLOT = "DisztarcsaSlotCol"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}

def counters(s):
    g = lambda k: s(f"get {T}.{k}")["value"]
    return {k: g(k) for k in ("AttachReqsSent", "AttachReqsApplied", "AttachReqsRefused", "AttachesSent", "AttachesApplied", "AttachesSame", "AttachesFailed")}

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    try:
        h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_(f"set {T}.AttachRequests {not OLD}"); s_(f"set {T}.OwnerCascade {not OLD}")
        print(f"  {'OLD (no attach requests)' if OLD else 'NEW'}", flush=True)
        lid = L("mp entities")["me"]
        hp = h("gpos")
        h(f"tpground {hp['gx'] + 15} {hp['gz']}"); L(f"tpground {hp['gx'] + 15} {hp['gz'] + 6}"); time.sleep(4)
        before = set(ents(h))
        print("  host car:", h("devspawn Car06Full"), flush=True)
        net = until(lambda: next((n for n, e in ents(h).items() if n not in before and e["car"] and not e["proxy"]), None), 20)
        if not net or not until(lambda: net in ents(L), 30): sys.exit("laptop never got the car")
        time.sleep(6)
        cp = ents(h)[net]["pos"]

        hub = None
        for case in [c for c in ("A", "B") if c in ONLY]:
            L(f"tpground {cp[0] + 2.5} {cp[2] + 1}"); h(f"tpground {cp[0] + 2.5} {cp[2] - 1.5}"); time.sleep(3)
            lb = set(ents(L))
            L(f"set {T}.TestAsPlayer True")
            print(f"  [{case}] laptop takes the hubcap off:", L(f"mount off {net} {SLOT}"), flush=True)
            L(f"set {T}.TestAsPlayer False")
            prev = hub if case != "A" else None   # bolted back in A, the same hubcap keeps its entity
            hub = until(lambda: next((n for n, e in ents(L).items() if (n not in lb or n == prev) and "Disztarcsa" in e["name"] and e["owner"] == lid), None), 10)
            if not hub: verdict(f"{case}0 THE HUBCAP CAME OFF", False, "no new hubcap entity on the laptop"); continue
            until(lambda: ents(L)[hub]["owner"] == lid and not ents(L)[hub]["proxy"], 10); time.sleep(2)
            L("mp drop"); time.sleep(2)
            print(f"  [{case}] host picks it up:", h(f"mp pickupnet {hub}"), flush=True)
            if case == "B": h(f"mp forceheld {hub}")
            took = until(lambda: ents(L)[hub]["owner"] == 0 and ents(L)[hub]["proxy"], 10)
            if case == "A": h("mp drop"); time.sleep(2)
            el = ents(L)[hub]
            verdict(f"{case}0 THE LAPTOP'S IS A COPY NOW (THE HOST'S)", bool(took), f"laptop sees owner {el['owner']} proxy {el['proxy']}")
            c0 = (counters(h), counters(L))
            L(f"set {T}.TestAsPlayer True")
            print(f"  [{case}] laptop's player bolts the copy on:", L(f"mount on {hub} {net} {SLOT}"), flush=True)
            L(f"set {T}.TestAsPlayer False")
            time.sleep(8)
            if case == "B": h(f"mp forceheld 0"); h("mp drop"); time.sleep(3)
            ph, pl = h(f"mount pose {hub} {net} {SLOT}"), L(f"mount pose {hub} {net} {SLOT}")
            c1 = (counters(h), counters(L))
            d = lambda a, b: {k: b[k] - a[k] for k in a if b[k] != a[k]}
            print(f"  [{case}] counters host {d(c0[0], c1[0])} laptop {d(c0[1], c1[1])}", flush=True)
            print(f"  [{case}] host {ph}\n  [{case}] laptop {pl}", flush=True)
            if case == "A":
                verdict("A1 BOLTED BY THE LAPTOP'S PLAYER: IN THE SLOT ON BOTH", ph.get("inSlot") and pl.get("inSlot"),
                        f"host in slot {ph.get('inSlot')} {ph.get('dist')} m, laptop in slot {pl.get('inSlot')} {pl.get('dist')} m")
                oh, ol = ents(h).get(hub, {}).get("owner"), ents(L).get(hub, {}).get("owner")
                verdict("A2 THE SAME OWNER ON BOTH", oh is not None and oh == ol, f"host {oh}, laptop {ol}")
            else:
                verdict("B1 THE OWNER HOLDS IT: OFF THE CAR ON BOTH (NOT BOLTED ON ONE ONLY)", ph.get("inSlot") == pl.get("inSlot") and not ph.get("inSlot"),
                        f"host in slot {ph.get('inSlot')}, laptop in slot {pl.get('inSlot')} {pl.get('dist')} m")
                if pl.get("inSlot") is False and ph.get("inSlot") is False:
                    # put it back for the next case
                    h(f"mp pickupnet {hub}"); h(f"mount on {hub} {net} {SLOT}"); time.sleep(4)
            if case == "A": time.sleep(2)
        if "C" in ONLY:
            hid = h("mp entities")["me"]
            L(f"tpground {cp[0] + 2.5} {cp[2] + 1}"); h(f"tpground {cp[0] + 2.5} {cp[2] - 1.5}"); time.sleep(3)
            print("  [C] laptop claims the host's car:", L(f"mp claim {net}"), flush=True)
            ok = until(lambda: ents(h)[net]["owner"] == lid and ents(L)[net]["owner"] == lid, 10)
            verdict("C0 THE LAPTOP HAS THE HOST'S CAR", bool(ok), f"host sees owner {ents(h)[net]['owner']}, laptop {ents(L)[net]['owner']}")
            lb = set(ents(L))
            L(f"set {T}.TestAsPlayer True")
            print("  [C] laptop takes the hubcap off:", L(f"mount off {net} {SLOT}"), flush=True)
            L(f"set {T}.TestAsPlayer False")
            hub = until(lambda: next((n for n, e in ents(L).items() if "Disztarcsa" in e["name"] and e["owner"] == lid and (n not in lb or n == hub)), None), 10)
            if not hub: verdict("C1 THE HUBCAP CAME OFF", False, "no hubcap entity of the laptop's")
            else:
                time.sleep(2)
                L(f"set {T}.TestAsPlayer True")
                print("  [C] laptop bolts it back:", L(f"mount on {hub} {net} {SLOT}"), flush=True)
                L(f"set {T}.TestAsPlayer False"); time.sleep(4)
                print(f"  [C] hubcap net {hub}: owner host-side {ents(h)[hub]['owner']}, laptop-side {ents(L)[hub]['owner']}", flush=True)
                print("  [C] host claims its car back:", h(f"mp claim {net}"), flush=True)
                until(lambda: ents(h)[net]["owner"] == hid and ents(L)[net]["owner"] == hid, 10); time.sleep(3)
                oh, ol = ents(h)[hub]["owner"], ents(L)[hub]["owner"]
                verdict("C2 THE HUBCAP WENT BACK WITH THE CAR (THE HOST'S ON BOTH)", oh == hid and ol == hid, f"host sees owner {oh}, laptop {ol}; cascaded {h(f'get {T}.OwnerCascaded')['value']}")
                h(f"set {T}.TestAsPlayer True")
                print("  [C] host takes it off:", h(f"mount off {net} {SLOT}"), flush=True)
                h(f"set {T}.TestAsPlayer False")
                try: h("mp drop")
                except RuntimeError: pass
                time.sleep(6)
                ph, pl = h(f"mount pose {hub} {net} {SLOT}"), L(f"mount pose {hub} {net} {SLOT}")
                eh, el = ents(h).get(hub), ents(L).get(hub)
                gap = math.dist(eh["pos"], el["pos"]) if eh and el else None
                verdict("C3 THE HOST TOOK IT OFF: OFF THE CAR ON BOTH, AT THE SAME PLACE", not ph.get("inSlot") and not pl.get("inSlot") and gap is not None and gap < 0.5,
                        f"host in slot {ph.get('inSlot')}, laptop in slot {pl.get('inSlot')}; {gap and round(gap, 2)} m apart")
        print("  errors:", [s_("errors")["errors"] for s_ in (h, L)], flush=True)
    finally:
        for s_ in (h, L):
            try: s_(f"set {T}.AttachRequests True"); s_(f"set {T}.OwnerCascade True"); s_(f"set {T}.TestAsPlayer False"); s_(f"mp forceheld 0")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
