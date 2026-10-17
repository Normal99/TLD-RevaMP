#!/usr/bin/env python3
"""Worn things (hats, caps, glasses, helmets, masks) seen by the other player. Two machines over the relay:
  J  the host wears glasses before hosting; the laptop joins: the glasses on the host's head on the laptop's screen,
     and on nobody else's (a record captured while worn put them on the loader's own player)
  A  the host spawns a hat, the laptop puts it on: on the host's screen it sits on the laptop's body where the laptop
     has it, collides with nothing, stays the laptop's
  B  the host takes a car01 (sharing it while seated), the laptop rides along; 12 s at --kmh: each player's worn item in
     the car's frame the same on both screens; one pair of glasses in the world (the car's record took them along)
  C  the laptop takes the hat off into its hands, then drops it: off the body on the host's screen, where the laptop
     has it, no duplicate; the laptop puts it on again
  D  the laptop leaves and joins again: the hat back on its head (its player save), on the host's screen too; the host's
     glasses on the host's head for the laptop; one hat, one pair of glasses
  The host's auto shift is on for B (the autopilot stays in first gear without it).
  tools/wearsync.py [--no-build] [--relay iad|off] [--kmh 50] [--old: before v0.68.2] [--old-share: before v0.68.5]"""
import sys, os, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import g, car_ready, clear_road

os.environ.setdefault("TLD_TEST_MODS", "CarTrailer,HeadUnit")   # the example mods stay installed

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD, KMH = arg("--relay", "iad"), "--old" in sys.argv, arg("--kmh", 50.0, float)
OLD_SHARE = "--old-share" in sys.argv   # before v0.68.5: a rejoin's hat shared loose, its Worn message after the network id
T = "TLDRevamp.Net.Entities"; CC = "TLDRevamp.Diagnostics.CrashCause"
SWITCHES = [f"{T}.WornSync", "TLDRevamp.Net.RecordCodec.StripWorn", "TLDRevamp.Net.PlayerSave.KeepWorn", "TLDRevamp.Net.ItemSnapshot.SkipPlayersThings"]
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
def worn(s): return s("mp worn")
def item(s, net): return next((i for i in worn(s)["items"] if i["net"] == net), None)
def named(s, part): return [i for i in worn(s)["items"] if part.lower() in i["name"].lower()]
def body_gap(a, b):
    """the same item on its wearer's body on two machines: |height diff|, |axis distance diff| (m)"""
    if not a or not b or a.get("onBody") is None or b.get("onBody") is None: return None
    return max(abs(a["onBody"][0] - b["onBody"][0]), abs(a["onBody"][1] - b["onBody"][1]))
def speed(s): return abs(float(g(s, "mainscript.s.player.Car.speed") or 0))
def crashes(s): return int(s(f"get {CC}.Logged")["value"])
def setall(s, v):
    for k in SWITCHES: s(f"set {k} {v}")
    for k in (f"{T}.WornInShare", "TLDRevamp.Net.PlayerSave.ShareWornLate"): s(f"set {k} {v and not OLD_SHARE}")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True"); setall(s_, not OLD)
    print(f"  {'OLD (before v0.68.2)' if OLD else 'OLD SHARE (before v0.68.5)' if OLD_SHARE else 'CURRENT BUILD'}", flush=True)
    if RELAY != "off": relay_both(h, L, RELAY)
    gods = {}; shift_h = None
    try:
        wl = h("mp wearables")["wearables"]
        hat = next(w for w in wl if w[2] in ("hat", "cap"))
        glasses = next(w for w in wl if w[2] == "glasses")
        print(f"  wearables: {len(wl)} ({', '.join(w[1] + '/' + w[2] for w in wl)}); hat {hat[1]}, glasses {glasses[1]}", flush=True)
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        shift_h = g(h, "settingsscript.s.S.BAutoShift"); h("set settingsscript.s.S.BAutoShift True")   # the autopilot drives in first gear without it
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        h(f"tpground {r0['gx'] + R[0] * 6} {r0['gz'] + R[1] * 6}"); time.sleep(2)
        # ---- J: worn before hosting
        print("  host spawns glasses:", h(f"devspawn {glasses[0]}"), flush=True); time.sleep(1.5)
        print("  host puts them on:", h(f"mp wearnear {glasses[1]}"), flush=True)
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        # (the join loads the host's world: god mode is off again — the laptop died in C once, flung while getting out)
        L("set TLDRevamp.TestSafety.BlockAutoSave True"); setall(L, not OLD); L("set kaposztaleves.s.settings.god True")
        hid, lid = h("mp entities")["me"], L("mp entities")["me"]
        me = h("mp players")["me"]
        L(f"tpground {me[0] + F[0] * 2.5} {me[2] + F[1] * 2.5}"); time.sleep(4)
        hg = until(lambda: next(i for i in named(h, glasses[1]) if i["localSlot"] >= 0), 10)
        gnet = hg["net"] if hg else None
        lg = until(lambda: (lambda i: i if i and (i.get("onBody") is not None or OLD) else None)(item(L, gnet)), 15) or item(L, gnet)
        lslots = worn(L)["slots"]
        verdict("J: THE HOST'S GLASSES ARE NOT ON THE JOINER'S OWN HEAD", not any(glasses[1] in x for x in lslots), f"laptop's slots {lslots}")
        gap = body_gap(hg, lg)
        verdict("J: THE JOINER SEES THEM ON THE HOST'S HEAD", lg is not None and gap is not None and gap < 0.15 and lg["colliders"] == 0,
                f"host {hg and hg['onBody']} laptop {lg and lg.get('onBody')} rides {lg and lg.get('rides')} colliders {lg and lg.get('colliders')} at {lg and lg.get('pos')} vs host {hg and hg.get('pos')}")
        # ---- A: the laptop puts on the host's hat
        before = set(ents(h))
        print("  host spawns a hat:", h(f"devspawn {hat[0]}"), flush=True)
        hnet = until(lambda: next((n for n, e in ents(h).items() if n not in before and not e["car"]), None), 15)
        until(lambda: item(L, hnet), 20); time.sleep(1)
        print("  laptop puts it on:", L(f"mp wear {hnet}"), flush=True); time.sleep(3)
        la, ha = item(L, hnet), item(h, hnet)
        gap = body_gap(la, ha)
        verdict("A: THE LAPTOP WEARS IT (ITS OWN)", la and la["localSlot"] >= 0 and la["owner"] == lid and ha["owner"] == lid, f"laptop {la and (la['localSlot'], la['owner'])}, host's owner {ha and ha['owner']}")
        verdict("A: ON THE LAPTOP'S BODY ON THE HOST'S SCREEN, NO COLLISIONS", gap is not None and gap < 0.15 and ha["colliders"] == 0,
                f"laptop {la and la['onBody']} host {ha and ha.get('onBody')} rides {ha and ha['rides']} colliders {ha and ha['colliders']} rb {ha and ha['rb']}")
        print("  host refuses a claim on it:", h(f"mp claim {hnet}"), flush=True); time.sleep(1.5)
        verdict("A: A CLAIM ON THE WORN HAT IS REFUSED", item(L, hnet)["localSlot"] >= 0 and item(h, hnet)["owner"] == lid and item(L, hnet)["owner"] == lid,
                f"owners {item(h, hnet)['owner']}/{item(L, hnet)['owner']}, refused {h('mp worn')['refused']}")
        h(f"mp lookplayer {lid}"); time.sleep(0.6); print("  shot:", h("shot wear_A"), flush=True)
        # ---- B: a car ride, the host driving
        h(f"tpground {r0['gx'] + R[0] * 4} {r0['gz'] + R[1] * 4}"); time.sleep(1)
        if not car_ready(h, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        me = h("mp players")["me"]
        car = until(lambda: min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"], 10)
        L(f"tpground {me[0] + R[0] * 3} {me[2] + R[1] * 3}"); time.sleep(2)
        for _ in range(4):
            L(f"mp sitpassenger {car}"); time.sleep(1.5)
            if g(L, "mainscript.s.player.Bsitting") is True: break
        print(f"  car net {car}; laptop riding: {g(L, 'mainscript.s.player.Bsitting')}", flush=True)
        time.sleep(2)
        c0 = crashes(h); h(f"autodrive on {KMH / 3.6:.1f}")
        worst, vmax, t0 = {"glasses": 0.0, "hat": 0.0}, 0.0, time.monotonic()
        while time.monotonic() - t0 < 12:
            vmax = max(vmax, speed(h))
            a, b = h(f"mp relpos {car} {gnet} {hnet}"), L(f"mp relpos {car} {gnet} {hnet}")
            pa, pb = a.get("late") or a["items"], b.get("late") or b["items"]
            for nm, n in (("glasses", gnet), ("hat", hnet)):
                if str(n) in pa and str(n) in pb: worst[nm] = max(worst[nm], math.dist(pa[str(n)], pb[str(n)]))
                else: worst[nm] = 99
            time.sleep(0.3)
        h("autodrive on 0"); until(lambda: speed(h) < 3, 20, 0.3); h("autodrive off"); h("brakecar"); h("handbrake on"); time.sleep(2)
        verdict(f"B: DRIVEN AT ~{KMH:.0f} KM/H", vmax > KMH * 0.7 and crashes(h) == c0, f"top {vmax:.0f} km/h, crash causes {crashes(h) - c0}")
        verdict("B: WORN ITEMS IN THE SAME PLACE IN THE CAR ON BOTH SCREENS", max(worst.values()) < 0.25, f"largest gap: glasses (host's) {worst['glasses']:.2f} m, hat (laptop's) {worst['hat']:.2f} m")
        ng, nh = len(named(L, glasses[1])), len(named(h, hat[1]))
        lslots = worn(L)["slots"]
        verdict("B: ONE PAIR OF GLASSES, ONE HAT", ng == 1 and nh == 1 and len(named(h, glasses[1])) == 1 and len(named(L, hat[1])) == 1,
                f"glasses host/laptop {len(named(h, glasses[1]))}/{ng}, hats {nh}/{len(named(L, hat[1]))}, laptop's slots {lslots}")
        # ---- C: off into the hands, dropped
        L("driveout"); h("driveout"); time.sleep(2)
        print("  laptop takes the hat off:", L(f"mp takeoff {hnet}"), flush=True); time.sleep(2)
        la, ha = item(L, hnet), item(h, hnet)
        verdict("C: TAKEN OFF: IN THE LAPTOP'S HANDS ON BOTH SCREENS", la["localSlot"] < 0 and ha["wornBy"] < 0 and not ha["rides"] and math.dist(la["pos"], ha["pos"]) < 0.5,
                f"laptop slot {la['localSlot']} held {la['held']}, host wornBy {ha['wornBy']} held {ha['held']}, apart {math.dist(la['pos'], ha['pos']):.2f} m")
        try: print("  laptop drops it:", L("mp drop"), flush=True)
        except RuntimeError as ex: print("  laptop drops it:", ex, "(the bridge's pick-up had let go already)", flush=True)
        time.sleep(4)
        la, ha = item(L, hnet), item(h, hnet)
        verdict("C: DROPPED: WHERE THE LAPTOP HAS IT, SOLID AGAIN, ONE HAT", math.dist(la["pos"], ha["pos"]) < 0.3 and ha["colliders"] > 0 and len(named(h, hat[1])) == 1,
                f"apart {math.dist(la['pos'], ha['pos']):.2f} m, host colliders {ha['colliders']}, hats {len(named(h, hat[1]))}")
        print("  laptop puts it on again:", L(f"mp wear {hnet}"), flush=True); time.sleep(3)
        # ---- D: rejoin
        sid = h("net me")["steamId"]
        print("  laptop leaves:", L("mp stop"), flush=True); time.sleep(5)
        print(f"  host after the leave: hats {len(named(h, hat[1]))}, player save {h('mp playersave')['last'] if 'error' not in h('mp playersave') else '-'}", flush=True)
        for _ in range(3):
            L(f"mp joinid {sid}")
            if until(lambda: L("mp status")["client"]["ready"], 150): break
            L("mp stop"); time.sleep(3)
        L("set TLDRevamp.TestSafety.BlockAutoSave True"); setall(L, not OLD); L("set kaposztaleves.s.settings.god True")
        time.sleep(8)
        time.sleep(3)
        # (2026-10-17: a hat restored loose and put on only then was claimed by the host on the way: owner log "claim")
        for i in named(L, hat[1]):
            olog = h(f"mp ownerlog {i['net']}")["log"]
            print(f"  laptop's hat: {({k: i.get(k) for k in ('net', 'localSlot', 'proxy', 'owner', 'onBody')})}; host's owner log {olog}", flush=True)
        print(f"  laptop's player save: {L('mp playersave').get('wornLog')}", flush=True)
        lh = until(lambda: next(i for i in named(L, hat[1]) if i["localSlot"] >= 0), 15)
        hh = until(lambda: next(i for i in named(h, hat[1]) if i.get("onBody") is not None), 10) if lh else None
        gap = body_gap(lh, hh)
        verdict("D: THE HAT BACK ON THE LAPTOP'S HEAD", lh is not None, f"laptop's slots {worn(L)['slots']}")
        verdict("D: ON THE LAPTOP'S BODY ON THE HOST'S SCREEN", gap is not None and gap < 0.15, f"laptop {lh and lh['onBody']} host {hh and hh.get('onBody')}")
        lg = until(lambda: next(i for i in named(L, glasses[1]) if i.get("onBody") is not None), 10)
        hg = item(h, gnet)
        gap = body_gap(hg, lg)
        verdict("D: THE HOST'S GLASSES ON THE HOST'S HEAD FOR THE REJOINED LAPTOP", gap is not None and gap < 0.15 and not any(glasses[1] in x for x in worn(L)["slots"]),
                f"host {hg and hg['onBody']} laptop {lg and lg.get('onBody')}, laptop's slots {worn(L)['slots']}")
        counts = [(len(named(s_, hat[1])), len(named(s_, glasses[1]))) for s_ in (h, L)]
        verdict("D: ONE HAT, ONE PAIR OF GLASSES ON EACH MACHINE", all(c == (1, 1) for c in counts), f"(hats, glasses) host {counts[0]} laptop {counts[1]}")
        L(f"mp lookplayer {hid}"); time.sleep(0.6); print("  laptop shot:", L("shot wear_D"), flush=True)
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
        for name, s_ in (("host", h), ("laptop", L)):
            w = worn(s_); print(f"  {name}: worn sent {w['sent']} relayed {w['relayed']} applied {w['applied']} cleared {w['cleared']} refused {w['refused']} records stripped {w['strippedRecords']}", flush=True)
    finally:
        for s_ in (h, L):
            try: s_("autodrive off")
            except Exception: pass
        for s_, v in list(gods.values()): s_(f"set kaposztaleves.s.settings.god {v}")
        try:
            if shift_h is not None: h(f"set settingsscript.s.S.BAutoShift {shift_h}")
        except Exception: pass
        for s_ in (h, L):
            try: setall(s_, True)
            except Exception: pass
        L("mp stop"); h("mp stop")
    print(f"\n{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed", flush=True)
