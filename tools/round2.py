#!/usr/bin/env python3
"""Two machines, the user's playtest round 2 — one verdict per report, raw numbers printed.
  A #1 dashboard churn   host drives, laptop rides shotgun: the laptop must not re-attach parts (attachReapplied 0)
  B #4 seat swap at speed host jumps out while driving, laptop slides over to the wheel → no crash, speed kept
  C #3 dev-menu spawn    laptop spawns a magnum through the dev menu's own Spawn → desktop has it
  D #2 shooting          laptop shoots the host, host shoots the laptop → damage lands on the victim
  E #5/#6 parts + fuel   a crash-style fall-off on the owner → the other player's copy is un-bolted and pickable, they pick it up;
                         non-owner wrenches a part off (owner dismounts it);
                         both sides fuel the same car
  F #5 real crash        host drives at 90 km/h with the laptop aboard, stops dead (the game's own crash check) →
                         the same parts come off on both, the passenger gets out and picks up every one
  tools/round2.py [--no-build] [--only A,B,...] [--lag] [--relay <datacenter>]   (--lag: 180 ms RTT, 2 % loss, jitter on both, from before the join)"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP

ONLY = set(sys.argv[sys.argv.index("--only") + 1].split(",")) if "--only" in sys.argv else None
def want(p): return ONLY is None or p in ONLY
LAG = "180 2 10 40" if "--lag" in sys.argv else None   # RTT ms, loss %, jitter % of packets, +ms
RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else None   # a Valve datacenter code: real internet via Steam relays

def until(fn, timeout=15, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

verdicts = {}
def verdict(name, ok, detail=""):
    if LAG: name += " @180ms"
    if RELAY: name += f" @relay:{RELAY}"
    verdicts[name] = ok
    print(f"{name}: {'PASS' if ok else 'FAIL'}  {detail}", flush=True)

def seat_idx(side, want_driver):
    seats = side("seatraw")["seats"]
    target = next(s["i"] for s in seats if s["main"] == "null" and (s["d0"] if want_driver else (s["d1"] and not s["d0"])))
    return next(s["i"] for s in seats if s["main"] == f"#{target}")

def ent(side, net): return {e["net"]: e for e in side("mp entities")["entities"]}.get(net)
def speed(side, net, dt=0.5):
    p0 = ent(side, net)["pos"]; time.sleep(dt); p1 = ent(side, net)["pos"]
    return math.dist(p0, p1) / dt
SEAT_WAIT = []   # seconds a seat stayed "not free" before the sit worked (the other player's exit still in flight)
def sit_until(side, idx, tries=4, step=0.2, timeout=6):
    t0 = time.time(); blocked = False
    while time.time() - t0 < timeout:
        try: side(f"sitseat {idx} redirect")
        except RuntimeError as ex:
            if "not free" not in str(ex): raise
            blocked = True; time.sleep(step); continue
        if blocked: SEAT_WAIT.append(round(time.time() - t0, 2))
        time.sleep(1)
        if side("get mainscript.s.player.Bsitting")["value"] is True: return True
        tries -= 1
        if tries <= 0: return False
    return False

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        if LAG: print("simulated internet on both machines:", b(f"net lag {LAG}"), L(f"net lag {LAG}"), flush=True)
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("relay only via", RELAY, ":", b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
        b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: L(f"mp joinid {b('net me')['steamId']}")   # the panel's friend join: Steam P2P
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        shift0 = {id(side): side("get settingsscript.s.S.BAutoShift")["value"] for side in (b, L)}   # the player's setting: restored at the end
        for side in (b, L): side("set kaposztaleves.s.settings.god True"); side("set settingsscript.s.S.BAutoShift True")
        print("session up", flush=True)
        if RELAY:
            route = L("net route"); print("route:", route, flush=True)
            # the local hop can enter the relay network elsewhere (e.g. par → Valve backbone → iad): what counts is a relayed
            # route with the pinned datacenter on it and real long-haul latency
            if not route.get("relayed") or RELAY not in (route.get("popRelay"), route.get("popRemote")) or route.get("ping", 0) < 120:
                sys.exit(f"NOT A REAL RELAY ROUTE VIA {RELAY}: {route}")
        if LAG:
            until(lambda: L("mp status")["client"]["ping"] >= 120, 15)
            ping = L("mp status")["client"]["ping"]; print(f"measured ping {ping} ms", flush=True)
            if ping < 120: sys.exit(f"LAG NOT APPLIED: ping {ping} ms")
        rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        cx, cz = rp["x"] + F[0] * 20, rp["z"] + F[1] * 20
        g = b(f"ground {cx} {cz}")
        b(f"teleportyaw {cx + 5} {g['y'] + 1.5} {cz} 270"); time.sleep(1)
        b(f"spawnpile 2 1 {cx} {g['y']} {cz} 1 1 1.0 4242"); time.sleep(4); b(f"carface {cx} {g['y']} {cz} {yaw}"); time.sleep(2)
        net = min((e for e in b("mp entities")["entities"] if e["part"] < 0 and e["car"]), key=lambda e: math.dist(e["pos"], (cx, g["y"], cz)))["net"]
        until(lambda: ent(L, net) is not None, 20)
        print("host sits:", sit_until(b, seat_idx(b, True)), b("fuelup"))
        pos = ent(b, net)["pos"]; L(f"tpg {pos[0] + 3} {pos[2] + 3}"); time.sleep(7)
        print("laptop sits shotgun:", sit_until(L, seat_idx(L, False)), flush=True)

        if want("A") or want("B"):
            r0 = L("mp entities")["resync"]; h0 = b("mp entities")["resync"]
            b("handbrake off"); print("host drives:", str(b("autodrive on 15"))[:80]); time.sleep(14)
            r1 = L("mp entities")["resync"]; h1 = b("mp entities")["resync"]
            att = r1["attachReapplied"] - r0["attachReapplied"]
            verdict("A #1 DASHBOARD CHURN", att == 0,
                    f"laptop re-attached {att} parts while riding; host sent {h1['itemsSent'] - h0['itemsSent']} item records in "
                    f"{h1['sent'] - h0['sent']} messages; laptop applied lists {r1['listsApplied'] - r0['listsApplied']}, unchanged {r1['listsSame'] - r0['listsSame']}")

        if want("B"):
            v0 = speed(L, net)
            parts0 = (ent(b, net)["attached"], ent(L, net)["attached"]); rep0 = b("mp entities")["partsReported"] + L("mp entities")["partsReported"]
            b("autodrive off"); print("host jumps out at", f"{v0:.1f} m/s:", b("driveout"))
            L("driveout"); ok = sit_until(L, seat_idx(L, True))
            flipped = until(lambda: ent(b, net)["owner"] == 1 and ent(L, net)["owner"] == 1, 6, 0.2)
            v1 = speed(L, net, 0.4)
            time.sleep(2)
            parts1 = (ent(b, net)["attached"], ent(L, net)["attached"]); rep1 = b("mp entities")["partsReported"] + L("mp entities")["partsReported"]
            hs = L("mp entities")["resync"]["handoverDamageSuppressed"]
            if SEAT_WAIT: print(f"  driver seat showed occupied for {SEAT_WAIT[-1]} s after the host's exit", flush=True)
            verdict("B #4 SEAT SWAP AT SPEED", ok and flipped and parts1 == parts0 and rep1 == rep0 and v1 > 0.4 * v0,
                    f"laptop at the wheel {ok}, owner flipped {flipped}; speed {v0:.1f} → {v1:.1f} m/s right after; attached parts "
                    f"{parts0} → {parts1}, part-offs {rep1 - rep0}; crash checks held back during the handover {hs}")
            L("brakecar"); L("handbrake on")

        if want("C") or want("D"):
            for side in (b, L):
                if side("get mainscript.s.player.Bsitting")["value"] is True: side("driveout"); time.sleep(1)
            me = b("mp players")["me"]; L(f"tpg {me[0] + 6} {me[2]}"); time.sleep(7)
            before = {e["net"] for e in b("mp entities")["entities"]}
            print("laptop dev-menu spawn:", L("devspawn 64"))
            spawned = L("mp entities")["entities"]
            got = until(lambda: any(e["net"] not in before and e["part"] < 0 for e in b("mp entities")["entities"]), 10)
            verdict("C #3 DEV-MENU SPAWN", got, "desktop got the laptop's magnum" if got else f"desktop entities: {[e['name'] for e in b('mp entities')['entities'] if e['net'] not in before]}")

        if want("D"):
            for shooter, victim, vid, label in ((L, b, 0, "laptop shoots host"), (b, L, 1, "host shoots laptop")):
                if shooter is b: b("devspawn 64"); time.sleep(2)
                hp0 = victim("get mainscript.s.player.survival.hp")["value"]; t0 = victim("mp combat")["damageTaken"]
                shot = shooter(f"mp shootat {vid}"); time.sleep(1.5)
                hp1 = victim("get mainscript.s.player.survival.hp")["value"]; t1 = victim("mp combat")["damageTaken"]
                verdict(f"D #2 SHOOTING ({label})", t1 > t0, f"shot {shot}; victim damage events {t1 - t0}, hp {hp0} → {hp1}")

        if want("E"):
            for side in (b, L):
                if side("get mainscript.s.player.Bsitting")["value"] is True: side("driveout"); time.sleep(1)
            owner = ent(b, net)["owner"]
            nonowner, ownerside = (b, L) if owner == 1 else (L, b)
            def loose(side, idx):
                return next((e["net"] for e in side("mp entities")["entities"] if e["parent"] == net and e["part"] == idx), None)
            def take_off(how, side, cands):
                """a part off car `net` by `how` on `side`; returns (idx, name, part net on the non-owner) or None"""
                for p in cands:
                    rep0 = ownerside("mp entities")["partsReported"]
                    r = side(f"mp {how} {net} {p['i']}")
                    if "error" in r: continue
                    print(f"{how} on {'desktop' if side is b else 'laptop'}: {r}", flush=True)
                    if not until(lambda: ownerside("mp entities")["partsReported"] > rep0, 8): return (p["i"], p["name"], None)
                    until(lambda: loose(nonowner, p["i"]) is not None, 8)
                    return (p["i"], p["name"], loose(nonowner, p["i"]))
                return None
            parts = ownerside(f"mp attachedparts {net}")["parts"]
            pref = sorted(parts, key=lambda p: 0 if any(k in p["name"] for k in ("oor", "ood", "unk", "ender")) else 1)
            picker = 1 if nonowner is L else 0
            carowner = 0 if picker == 1 else 1
            # #5 A/B: the same crash fall-off replayed the OLD way (bare Detach) on one part, the game's way on another
            fallen = {}
            for mode in ("old", "new"):
                nonowner(f"set TLDRevamp.Net.Entities.ReplayBareDetach {mode == 'old'}")
                got = take_off("falloff", ownerside, [p for p in pref if p["i"] not in [f[0] for f in fallen.values()]])
                nonowner("set TLDRevamp.Net.Entities.ReplayBareDetach False")
                if got is None or got[2] is None:
                    verdict(f"E #5 [{mode}] CRASH FALL-OFF REACHES THE OTHER PLAYER", False, f"{got}"); continue
                idx, name, pnet = got; fallen[mode] = got
                time.sleep(1.5)
                st_o, st_n = ownerside(f"mp lookpick {pnet}"), nonowner(f"mp lookpick {pnet}")
                print(f"  [{mode}] {name} owner's copy   : {st_o}\n  [{mode}] {name} non-owner copy : {st_n}", flush=True)
                r = nonowner(f"mp lookpick {pnet} pick"); print(f"  [{mode}] other player picks it:", r, flush=True)
                took = bool(r.get("picked")) and until(lambda: ent(b, pnet)["owner"] == ent(L, pnet)["owner"] == picker, 6)
                held = nonowner("get mainscript.s.player.pickedUp")["value"]
                ok = st_n["crafted"] == st_o["crafted"] is False and not st_n["inSlot"] and st_n["collidersOn"] == st_o["collidersOn"] and took
                label = "E #5 FALLEN PART PICKABLE BY THE OTHER PLAYER" + (" [old replay — expected to FAIL]" if mode == "old" else "")
                verdict(label, ok if mode == "new" else not ok,
                        f"{name}: crafted owner {st_o['crafted']} / other {st_n['crafted']}; colliders on {st_o['collidersOn']}/{st_o['colliders']} vs {st_n['collidersOn']}/{st_n['colliders']}; "
                        f"game allows pickup {st_n['gameAllows']}, ray hits it {st_n['rayHitsIt']!r}; picked {r.get('picked')}, owner → {ent(b, pnet)['owner']}/{ent(L, pnet)['owner']} (want {picker}); holding {held}")
                if mode == "new" and took:
                    # #6 adding: the other player bolts the part back onto the owner's car (the game's MountFunc: drop + Craft)
                    r = nonowner(f"mp attachto {pnet} {net} -1"); print("  other player bolts it back on:", r, flush=True)
                    on = until(lambda: ownerside(f"mp partin {pnet} {net}")["inSlot"] and ent(b, pnet)["owner"] == ent(L, pnet)["owner"] == carowner, 8)
                    pi_o, pi_n = ownerside(f"mp partin {pnet} {net}"), nonowner(f"mp partin {pnet} {net}")
                    verdict("E #6 OTHER PLAYER BOLTS A PART ONTO THE OWNER'S CAR", on and pi_o["crafted"] and pi_n["inSlot"] and pi_o["slotIdx"] == pi_n["slotIdx"],
                            f"owner's copy {pi_o}; attacher's copy {pi_n}; attach stats attacher {nonowner('mp attach')} owner {ownerside('mp attach')}")
                    # and off again on the owner (the crash path once more): comes off everywhere, still ONE entity for it
                    ownerside(f"mp falloff {net} {idx}")
                    off = until(lambda: not nonowner(f"mp partin {pnet} {net}")["attached"], 8)
                    dup = [e["net"] for e in nonowner("mp entities")["entities"] if e["parent"] == net and e["part"] == idx]
                    verdict("E #6 RE-BOLTED PART FALLS OFF AGAIN EVERYWHERE", off and len(dup) == 1,
                            f"attacher's copy {nonowner(f'mp partin {pnet} {net}')}; entities for it {dup}")
                elif took and nonowner("get mainscript.s.player.pickedUp")["value"] not in (None, "null"):
                    nonowner("dropheld"); time.sleep(1)
            # #6: the non-owner wrenches a part off (the player's E-press dismount on someone else's car)
            parts = nonowner(f"mp attachedparts {net}")["parts"]
            got = take_off("wrench", nonowner, [p for p in parts if "irror" in p["name"] or "Radio" in p["name"]] + parts[::-1])
            if got is None or got[2] is None:
                verdict("E #6 NON-OWNER WRENCH", False, f"{got}")
            else:
                st = nonowner(f"mp lookpick {got[2]}")
                verdict("E #6 NON-OWNER WRENCH", not st["crafted"] and not st["attached"], f"{got[1]} came off; on the wrencher: {st}")
            for who, amount in ((b, 11.5), (L, 23.25)):
                who(f"mp entpoke {net} p:fuel {amount}")
                same = until(lambda: b(f"mp entstate {net}")["tanks"].split()[0] == L(f"mp entstate {net}")["tanks"].split()[0] == f"{amount:.2f}", 10)
                verdict(f"E #6 FUEL ({'desktop' if who is b else 'laptop'} fills)", same,
                        f"tank desktop {b(f'mp entstate {net}')['tanks'].split()[0]} laptop {L(f'mp entstate {net}')['tanks'].split()[0]} (want {amount:.2f})")

        if want("F"):
            # #5 as played: a real crash while driving with the other player aboard; parts end up wherever they fly
            for side in (b, L):
                if side("get mainscript.s.player.Bsitting")["value"] is True: side("driveout"); time.sleep(1)
            pos = ent(b, net)["pos"]; b(f"tpg {pos[0] + 3} {pos[2] - 3}"); time.sleep(5)
            print("host sits:", sit_until(b, seat_idx(b, True)), "laptop shotgun:", sit_until(L, seat_idx(L, False)), flush=True)
            if "--old-replay" in sys.argv: print("OLD REPLAY on the laptop:", L("set TLDRevamp.Net.Entities.ReplayBareDetach True"), flush=True)
            att0 = {side: {p["i"] for p in side(f"mp attachedparts {net}")["parts"]} for side in (b, L)}
            rej0 = (b("mp entities")["partRejects"], L("mp entities")["partRejects"])
            b("handbrake off"); print("host drives:", str(b("autodrive on 25"))[:80], flush=True); time.sleep(9)
            v = speed(L, net); print(f"at {v:.1f} m/s: crash →", b(f"mp crashstop {net}"), flush=True)
            b("autodrive off"); time.sleep(0.3); b("brakecar"); time.sleep(3)
            gone = {side: att0[side] - {p["i"] for p in side(f"mp attachedparts {net}")["parts"]} for side in (b, L)}
            rej1 = (b("mp entities")["partRejects"], L("mp entities")["partRejects"])
            print(f"parts off: desktop(owner) {sorted(gone[b])} laptop {sorted(gone[L])}; rejects desktop {rej1[0][len(rej0[0]):]!r} laptop {rej1[1][len(rej0[1]):]!r}", flush=True)
            verdict("F #5 CRASH: SAME PARTS OFF ON BOTH", len(gone[b]) > 0 and gone[b] == gone[L], f"owner lost {sorted(gone[b])}, passenger's copy lost {sorted(gone[L])}")
            L("driveout"); time.sleep(1.5)
            fell = {e["net"]: e for e in L("mp entities")["entities"] if e["parent"] == net and e["part"] in gone[b]}
            results = []; unreachable = []
            for pnet, e in fell.items():
                st_o = b(f"mp lookpick {pnet}"); r = L(f"mp lookpick {pnet} pick")
                took = bool(r.get("picked")) and until(lambda: ent(b, pnet)["owner"] == ent(L, pnet)["owner"] == 1, 5)
                print(f"  part {e['part']} {r.get('name')}: OWNER {st_o} \n      PASSENGER {r} → took {took}, owner now {ent(b, pnet)['owner']}/{ent(L, pnet)['owner']}", flush=True)
                if not took and not st_o.get("rayHitsIt") and not r.get("rayHitsIt"): unreachable.append(r.get("name")); continue   # geometry: neither copy's ray reaches it
                results.append(took)
                if took:
                    time.sleep(1.0); held = L("get mainscript.s.player.pickedUp")["value"]
                    print(f"      still in hand after 1 s: {held}", flush=True)   # the game lets go of heavy/far items itself
                    if held not in (None, "null"): L("dropheld"); time.sleep(0.8)
            print("  passenger's replay trace:", L("mp attach").get("unmountTrace"), "| car resync skipped split-off parts:", L("mp entities")["resync"].get("splitSkipped"), flush=True)
            verdict("F #5 PASSENGER PICKS UP EVERY PART THE CRASH THREW OFF", len(results) > 0 and all(results) and len(fell) == len(gone[b]),
                    f"{sum(results)}/{len(results)} reachable parts picked ({len(fell)} shared of {len(gone[b])}); out of every test ray's reach on both copies: {unreachable}")
        for side in (b, L): side("set kaposztaleves.s.settings.god False"); side(f"set settingsscript.s.S.BAutoShift {shift0[id(side)]}")
        L("mp stop"); b("mp stop")
finally:
    try: L("set TLDRevamp.Net.Entities.ReplayBareDetach False")   # the A/B switch lives in the running game: never leave it on
    except Exception: pass
    if LAG:
        try: b("net lag 0"); L("net lag 0")
        except Exception: pass
    if RELAY:
        try: b("net relay off"); L("net relay off")
        except Exception: pass
    L.close()
print("\nROUND-2:", "ALL PASS" if verdicts and all(verdicts.values()) else "FAILURES", {k: ("PASS" if v else "FAIL") for k, v in verdicts.items()})
