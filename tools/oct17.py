#!/usr/bin/env python3
"""Playtest 2026-10-16/17 fixes and the security pass, two machines over the relay (host's physics lock at 3, a car01 on
the road), one run:
  C  CONTAINER: the host's jerry can on its car's back seat (stored by the car); the host takes it out and sets it down
     ~3 m to the side: on the laptop it is out of the car, where the host has it (it rode the car until 5 m before)
  T  TAKEOVER: the laptop takes the wheel of the host's car: the car the laptop's on both machines, the host's handover
     applied, none refused
  D  RADIATOR: the laptop's (now) car's radiator holds a little gas and goes off (tankscript.TryExplode, as a crash or a
     shot): gone on the laptop, and on the host too (only whole objects' deletions went out before)
  E  HAND LIGHT: the host's lamp switched by the host lying and in hand, then by the laptop next to it (a cooperative
     edit): lit the same on both; the laptop 40 m away switching it: refused by the host, the lamp unchanged
  F  POCKET + REJOIN: the laptop's own lamp switched in its hand (same on the host), then in its pocket: hidden on the
     host; the laptop leaves and joins again: the lamp back in its pocket (player save) and hidden on the host, the
     radiator still gone on the laptop
  I  IDENTITY: the laptop joining with a Steam ID that isn't its connection's: refused; then joins as itself
  R  REPORT: the laptop's report with sharing on reaches the host; another one started right after: dropped
  tools/oct17.py [--no-build] [--relay iad|off] [--old: MemberGoneSync, CopyLockByBox off]"""
import sys, os, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import g, car_ready, clear_road

os.environ.setdefault("TLD_TEST_MODS", "CarTrailer,HeadUnit")   # the example mods stay installed

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
T = "TLDRevamp.Net.Entities"; REP = "TLDRevamp.Net.Reports"
SWITCHES = [f"{T}.MemberGoneSync", f"{T}.CopyLockByBox"]
JERRYCAN, LAMP = 72, "regizseblampa"
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
def inside(p): return abs(p[0]) < 1.3 and -0.6 < p[1] < 2.0 and abs(p[2]) < 3.0   # in the car's box (car frame, m)
def new_item(s, what):
    before = set(ents(s)); s(f"devspawn {what}")
    return until(lambda: next((n for n, e in ents(s).items() if n not in before and not e["car"]), None), 15)
def tanks(s, car): return s(f"mp fluids {car}").get("tanks", [])
def coolant(s, car): return next((t for t in tanks(s, car) if "Coolant" in t.get("item", "")), None)
def num(s, k): return int(s(f"get {k}")["value"])
def same_light(a, b): return a["lightsOn"] == b["lightsOn"] and a["turn"] == b["turn"]

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    gods = {}; lock_h = g(h, "settingsscript.s.S.IPhysicsLock")
    share0 = L(f"get {REP}.Share")["value"]; ask0 = h(f"get {REP}.AskPlayers")["value"]
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L):
            for sw in SWITCHES: s_(f"set {sw} {not OLD}")
        print(f"  {'OLD (switches off)' if OLD else 'CURRENT BUILD'}", flush=True)
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        h("set settingsscript.s.S.IPhysicsLock 3")
        hid, lid = h("mp entities")["me"], L("mp entities")["me"]
        print(f"  identity: refused {num(h, 'TLDRevamp.Net.MpServer.IdentityRefused')}, without a player save {num(h, 'TLDRevamp.Net.MpServer.IdentityUnsaved')}", flush=True)
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        cx, cz = r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8
        if not car_ready(h, cx, cz, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        me = h("mp players")["me"]
        car = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
        L(f"tpground {cx + R[0] * 6} {cz + R[1] * 6}"); time.sleep(2)
        until(lambda: car in ents(L), 20)

        # ---- C: out of the container
        can = new_item(h, JERRYCAN)
        print(f"  jerry can {can} on the back seat:", h(f"mp putincar {car} {can} 0"), flush=True); time.sleep(5)
        lp0 = until(lambda: L(f"mp relpos {car} {can}")["items"].get(str(can)), 20)
        print(f"  laptop: the can in the car's frame {lp0}", flush=True)
        h("driveout"); time.sleep(1.5)
        print("  host takes it out, 3 m to the side:", h(f"mp takeout {can} {R[0] * 3:.2f} {R[1] * 3:.2f}"), flush=True)
        def out_on_laptop():
            p = L(f"mp relpos {car} {can}")["items"].get(str(can))
            a, b = ents(h).get(can), ents(L).get(can)
            return p is not None and not inside(p) and a and b and math.dist(a["pos"], b["pos"]) < 0.6 and (p, round(math.dist(a["pos"], b["pos"]), 2))
        got = until(out_on_laptop, 12)
        hp = h(f"mp relpos {car} {can}")["items"].get(str(can)); lp = L(f"mp relpos {car} {can}")["items"].get(str(can))
        verdict("C: TAKEN OUT ON THE HOST, OUT OF THE CAR ON THE LAPTOP", bool(got), f"car frame: host {hp}, laptop {lp}")

        # ---- T: the laptop takes the host's car
        ha0, hr0 = num(L, f"{T}.HandoversApplied"), num(h, f"{T}.HandoversRefused")
        L(f"tpground {cx + R[0] * 3} {cz + R[1] * 3}"); time.sleep(2)
        for _ in range(4):
            L(f"mp sitdriver {car}"); time.sleep(1.5)
            if g(L, "mainscript.s.player.Car") is not None: break
        owners = until(lambda: (lambda a, b: (a, b) if a == lid and b == lid else None)(ents(h)[car]["owner"], ents(L)[car]["owner"]), 10)
        time.sleep(2)
        verdict("T: TAKEOVER: THE CAR THE LAPTOP'S, THE HANDOVER APPLIED, NONE REFUSED",
                bool(owners) and num(L, f"{T}.HandoversApplied") > ha0 and num(h, f"{T}.HandoversRefused") == hr0,
                f"owners host/laptop {ents(h)[car]['owner']}/{ents(L)[car]['owner']} (laptop {lid}), handovers applied +{num(L, f'{T}.HandoversApplied') - ha0}, refused +{num(h, f'{T}.HandoversRefused') - hr0}")
        L("driveout"); time.sleep(1.5)

        # ---- D: the radiator goes off (the owner's machine: the laptop)
        ct = coolant(L, car)
        if not ct: verdict("D: THE CAR HAS A RADIATOR TANK", False, str(tanks(L, car)))
        else:
            hc0 = until(lambda: coolant(h, car), 10)
            print(f"  radiator: tank {ct['ti']} of item {ct['item']} (index {ct['index']}); host sees {hc0 and hc0['item']}", flush=True)
            print("  fill:", L(f"mp fill {car} {ct['ti']} gas 2"), flush=True)
            h(f"tpground {cx - F[0] * 25} {cz - F[1] * 25}"); L(f"tpground {cx - F[0] * 25 + R[0] * 3} {cz - F[1] * 25 + R[1] * 3}"); time.sleep(2)
            try: print("  set off:", L(f"mp tankexplode {car} {ct['ti']} 1000"), flush=True)
            except RuntimeError as ex: print("  set off: the game threw:", str(ex)[:160], flush=True)   # MeshSlicer (game code) on Car01Coolant
            gone_l = until(lambda: coolant(L, car) is None, 5)
            gone_h = until(lambda: coolant(h, car) is None, 10)
            verdict("D: RADIATOR GONE ON THE LAPTOP (OWNER)", bool(gone_l), str(L("mp membergone")))
            verdict("D: RADIATOR GONE ON THE HOST TOO", bool(gone_h), f"host {h('mp membergone')}")

        # ---- E: a hand light, and edits
        h(f"tpground {cx + R[0] * 4} {cz + R[1] * 4}"); L(f"tpground {cx + R[0] * 6} {cz + R[1] * 6}"); time.sleep(2)
        lamp = new_item(h, LAMP)
        until(lambda: lamp in ents(L), 15); time.sleep(1)
        def lit(): return h(f"mp lights {lamp}"), L(f"mp lights {lamp}")
        print("  lamp at first:", lit(), flush=True)
        print("  host switches it (lying):", h(f"mp switch {lamp}"), flush=True); time.sleep(3)
        a, b = lit(); verdict("E: HOST'S LAMP SWITCHED LYING: SAME ON THE LAPTOP", same_light(a, b), f"host {a} laptop {b}")
        print("  host picks it up:", h(f"mp pickupnet {lamp}"), flush=True); time.sleep(2)
        print("  host switches it (in hand):", h(f"mp switch {lamp}"), flush=True); time.sleep(3)
        a, b = lit(); verdict("E: HOST'S LAMP SWITCHED IN HAND: SAME ON THE LAPTOP", same_light(a, b), f"host {a} laptop {b}")
        h("dropheld"); time.sleep(2)
        ea0, er0 = num(h, f"{T}.EditsApplied"), num(h, f"{T}.EditsOutOfReach")
        before = h(f"mp lights {lamp}")
        print("  laptop switches the host's lamp next to it:", L(f"mp switch {lamp}"), flush=True); time.sleep(3)
        a, b = lit()
        verdict("E: COOPERATIVE EDIT: THE LAPTOP SWITCHED THE HOST'S LAMP, SAME ON BOTH", same_light(a, b) and a["turn"] != before["turn"] and num(h, f"{T}.EditsOutOfReach") == er0,
                f"before {before['turn']}, host {a} laptop {b}, edits applied on the host +{num(h, f'{T}.EditsApplied') - ea0}")
        L(f"tpground {cx + R[0] * 6 + F[0] * 40} {cz + R[1] * 6 + F[1] * 40}"); time.sleep(3)
        before = h(f"mp lights {lamp}")
        print("  laptop 40 m away switches it:", L(f"mp switch {lamp}"), flush=True); time.sleep(3)
        a = h(f"mp lights {lamp}")
        verdict("E: AN EDIT FROM 40 M AWAY: REFUSED, THE HOST'S LAMP UNCHANGED", a["turn"] == before["turn"] and num(h, f"{T}.EditsOutOfReach") > er0,
                f"host before {before['turn']} after {a['turn']}, out of reach +{num(h, f'{T}.EditsOutOfReach') - er0}")
        L(f"tpground {cx + R[0] * 6} {cz + R[1] * 6}"); time.sleep(3)

        # ---- F: the laptop's own lamp, in hand and in its pocket, across a rejoin
        mine = new_item(L, LAMP)
        until(lambda: mine in ents(h), 15); time.sleep(1)
        print("  laptop picks up its lamp:", L(f"mp pickupnet {mine}"), flush=True); time.sleep(2.5)
        print("  laptop switches it in hand:", L(f"mp switch {mine}"), flush=True); time.sleep(3)
        a, b = L(f"mp lights {mine}"), h(f"mp lights {mine}")
        verdict("F: LAPTOP'S LAMP SWITCHED IN ITS HAND: SAME ON THE HOST", same_light(a, b), f"laptop {a} host {b}")
        n_inv = g(L, "mainscript.s.player.inventory.Count"); s0 = g(L, "mainscript.s.player.selectedInv"); s1 = (int(s0) + 1) % int(n_inv)
        L(f"call mainscript.s.player.InvSwitchTo {s1} False"); time.sleep(2.5)
        a, b = ents(L).get(mine), ents(h).get(mine); lpp = L("mp players")["me"]
        verdict("F: IN THE LAPTOP'S POCKET, HIDDEN ON THE HOST", bool(a and b and a["stored"] and b["shown"] is False),
                f"laptop {a and (a['stored'], a['shown'])} | host's copy {b and (b['stored'], b['shown'], round(math.dist(b['pos'], lpp), 1))}")
        print("  laptop leaves:", L("mp stop"), flush=True); time.sleep(5)

        # ---- I: a forged identity, then the real one
        sid_l = L("net me")["steamId"]; ir0 = num(h, "TLDRevamp.Net.MpServer.IdentityRefused")
        L(f"set TLDRevamp.Net.MpClient.TestClaimSteamId {int(sid_l) + 1}")
        L(f"mp joinid {h('net me')['steamId']}")
        refused = until(lambda: num(h, "TLDRevamp.Net.MpServer.IdentityRefused") > ir0, 40)
        time.sleep(2)
        st = L("mp status").get("client") or {}
        verdict("I: A JOIN CLAIMING ANOTHER STEAM ID: REFUSED", bool(refused) and not st.get("ready"), f"refused +{num(h, 'TLDRevamp.Net.MpServer.IdentityRefused') - ir0}, laptop {st.get('last')}")
        L("set TLDRevamp.Net.MpClient.TestClaimSteamId 0"); L("mp stop"); time.sleep(3)
        if not join_both(h, L): verdict("F: REJOINED", False)
        else:
            L("set TLDRevamp.TestSafety.BlockAutoSave True"); time.sleep(8)
            lid2 = L("mp entities")["me"]
            hc = next((e for e in h("mp entities")["entities"] if LAMP in e.get("name", "") and e["owner"] == lid2), None)
            lc = next((e for e in L("mp entities")["entities"] if LAMP in e.get("name", "") and not e["proxy"]), None)
            lpp = L("mp players")["me"]
            verdict("F: AFTER THE REJOIN: IN THE POCKET (PLAYER SAVE), HIDDEN ON THE HOST", bool(lc and lc["stored"] and hc and hc["shown"] is False),
                    f"laptop {lc and (lc['net'], lc['stored'], lc['shown'])}, host's copy {hc and (hc['net'], hc['stored'], hc['shown'], round(math.dist(hc['pos'], lpp), 1))}, restore {h('mp playersave')}")
            if ct:
                until(lambda: car in ents(L), 20)
                verdict("F: THE RADIATOR STILL GONE ON THE LAPTOP AFTER THE REJOIN", coolant(L, car) is None, str(L("mp membergone")))

            # ---- R: a report with sharing on, another right after
            L(f"set {REP}.Share True"); h(f"set {REP}.AskPlayers False"); h(f"set {REP}.StartGapS 600")
            r0_ = h("mp reports")
            print("  laptop's report:", L("mp report oct17 test"), flush=True)
            ok = until(lambda: h("mp reports")["received"] > r0_["received"], 120, 1.0)
            verdict("R: THE LAPTOP'S REPORT REACHES THE HOST", bool(ok), f"host received +{h('mp reports')['received'] - r0_['received']}, laptop sent {L('mp reports')['sent']}")
            r1 = h("mp reports")
            print("  laptop's second report:", L("mp report oct17 again"), flush=True)
            dropped = until(lambda: h("mp reports")["dropped"] > r1["dropped"], 60, 1.0)
            time.sleep(3); r2 = h("mp reports")
            verdict("R: A SECOND ONE STARTED WITHIN THE GAP: DROPPED, NOT SAVED", bool(dropped) and r2["received"] == r1["received"],
                    f"dropped +{r2['dropped'] - r1['dropped']}, received +{r2['received'] - r1['received']}, last drop {h(f'get {REP}.LastDrop')['value']}")
        sq = h("mp sharequota")
        try: thr = h("get TLDRevamp.Net.Mp.Server.T.Throttled")
        except Exception as ex: thr = {"value": str(ex)[:80]}
        verdict("NO SHARES DROPPED IN NORMAL PLAY", sq.get("dropped") == 0, f"{sq}, receive throttled {thr.get('value')}")
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        for s_, v in list(gods.values()): s_(f"set kaposztaleves.s.settings.god {v}")
        for k, s_, v in ((f"{REP}.Share", L, share0), (f"{REP}.AskPlayers", h, ask0), (f"{REP}.StartGapS", h, 30), ("TLDRevamp.Net.MpClient.TestClaimSteamId", L, 0),
                         ("settingsscript.s.S.IPhysicsLock", h, lock_h)):
            try: s_(f"set {k} {v}")
            except Exception: pass
        for s_ in (h, L):
            try:
                for sw in SWITCHES: s_(f"set {sw} True")
            except Exception: pass
        try: L("mp stop"); h("mp stop")
        except Exception: pass
    print(f"\n{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed", flush=True)
