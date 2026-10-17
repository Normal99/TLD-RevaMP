#!/usr/bin/env python3
"""Bugs 4/5/6 (2026-10-03): another player's item in your car. Desktop only: the host's game plus an item bot (a
loopback client inside it) that owns a loose item and keeps it in the host's car as a remote owner would see it — the
car's pose Lag seconds late. The host sits in its car, the car is launched at SPEED, `crash rec` records it.
  A  cargo claims off (the v0.65.3 behaviour): the kinematic copy rides behind the car and shoves it
  B  cargo claims on: the car's touch makes the item the host's — it rides in the car as in single player
  C  claims on, the item in the bot's hands: no claim (the server refuses), the copy collides with nothing
Each configuration on a freshly started world (same seed).
  tools/cargotest.py [--no-build] [--item regizseblampa] [--offset 0,0.9,-0.9] [--lag 0.2] [--speed 15] [--only A,B,C]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

def arg(name, default): return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default
ITEM, OFFSET, LAG, SPEED = arg("--item", "regizseblampa"), arg("--offset", "0,0.9,-0.9"), float(arg("--lag", "0.2")), float(arg("--speed", "15"))
ONLY = arg("--only", "A,B,C").split(",")
CAR = "Car06Full"
T = "TLDRevamp.Net.Entities"
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

def ents(b): return {e["net"]: e for e in b("mp entities")["entities"]}

def seat_idx(b):
    seats = b("seatraw")["seats"]
    target = next(s["i"] for s in seats if s["main"] == "null" and s["d0"])
    return next(s["i"] for s in seats if s["main"] == f"#{target}")

def run(b, cfg):
    claims, held = cfg != "A", cfg == "C"
    b(f"set {T}.CargoClaims {claims}")
    p = b("gpos"); b(f"tpground {p['gx'] + 15} {p['gz']}"); time.sleep(3)
    b("mp host 27070")
    before = set(ents(b))
    print(f"  [{cfg}] car:", b(f"devspawn {CAR}"), flush=True)
    net = until(lambda: next((n for n, e in ents(b).items() if n not in before and e["car"]), None), 20)
    time.sleep(6)
    cp = ents(b)[net]["pos"]
    b(f"tpground {cp[0] + 10} {cp[2]}"); time.sleep(2)   # the item appears away from the car: it comes in already in its state
    print("  item:", b(f"devspawn {ITEM}"), flush=True); time.sleep(3)
    orig = next((n for n, e in ents(b).items() if n not in before and not e["car"] and not e["proxy"]), None)
    ox, oy, oz = OFFSET.split(",")
    r = b(f"mp botitem {ox} {oy} {oz} {LAG} {1 if held else 0}"); print("  bot:", r, flush=True)
    if orig is not None: b(f"deleteshare {orig}")   # the host's own lamp, captured: only the bot's remains
    print(f"  original net {orig}", flush=True)
    t0 = time.time(); seen = None
    while time.time() - t0 < 10:   # who owns what, as it happens
        rows = [(n, e["name"], e["owner"], e["epoch"], e["proxy"]) for n, e in ents(b).items() if not e["car"] and n not in before]
        cnt = {k: b(f"get {k}")["value"] for k in (f"{T}.Handoffs", f"{T}.ClaimsSent", f"{T}.CargoClaimed", f"{T}.OwnerChanges", f"{T}.SpawnShared")}
        print(f"   t+{time.time() - t0:4.1f} {rows} {cnt}", flush=True)
        if seen is None: seen = next((n for n, nm, o, ep, px in rows if n != orig and ITEM in nm), None)
        time.sleep(1)
    bi = seen
    if bi is None: verdict(f"{cfg}: THE BOT'S ITEM ARRIVED", False); return
    time.sleep(3)
    b(f"tpground {cp[0] + 3} {cp[2] + 3}"); time.sleep(2)
    print("  host sits:", b(f"sitseat {seat_idx(b)} redirect"), flush=True); time.sleep(3)
    e0 = ents(b)
    print(f"  before launch: item {e0.get(bi)}  claims {b(f'get {T}.CargoClaimed')['value']}", flush=True)
    b("crash rec start 30"); time.sleep(0.5)
    print("  launch:", b(f"crash launch {SPEED}"), flush=True)
    time.sleep(8)
    rec = b("crash rec stop")
    car = next((c for c in rec["cars"] if c.get("net") == net), rec["cars"][0] if rec["cars"] else {})
    e1 = ents(b)
    it = e1.get(bi, {})
    claimed = b(f"get {T}.CargoClaimed")["value"]
    print(f"  car: {car}\n  item after: {it}\n  cargo claims {claimed}, held shown {b(f'get {T}.HeldShown')['value']}", flush=True)
    dmg, lost = car.get("damageMaxV", -1), car.get("partsLost", -1)
    detail = f"damage input max {dmg} m/s, parts lost {lost}, peak spin {car.get('peakSpinDeg')}, item owner {it.get('owner')} proxy {it.get('proxy')}, claims {claimed}"
    if cfg == "A": verdict("A CLAIMS OFF: THE COPY SHOVES THE CAR (the bug, for reference)", lost > 0 or dmg > 12, detail)
    if cfg == "B": verdict("B CLAIMS ON: THE ITEM BECOMES THE HOST'S, NO DAMAGE", it.get("owner") == 0 and not it.get("proxy") and lost == 0 and dmg < 12, detail)
    if cfg == "C": verdict("C HELD: NOT CLAIMED, NO DAMAGE", it.get("owner") not in (None, 0) and lost == 0 and dmg < 12, detail)
    b("driveout"); b("mp stop")

if game.running(): game.quit_game()
if "--no-build" not in sys.argv: game.build()
else: game.deploy_local()
game.launch()
with game.ProtectedSession():
    for cfg in ONLY:
        game.newgame()
        b = Bridge()
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        god = b("get kaposztaleves.s.settings.god").get("value"); b("set kaposztaleves.s.settings.god True")
        try: run(b, cfg)
        except Exception as ex: verdict(f"{cfg}: RAN", False, repr(ex))
        finally:
            for k, v in ((f"{T}.CargoClaims", True), ("TLDRevamp.Net.Mp.BotItemHeld", False)):
                try: b(f"set {k} {v}")
                except Exception as ex: print("restore", k, ex)
            try: b("mp stop")
            except Exception: pass
            b(f"set kaposztaleves.s.settings.god {god if god is not None else False}")
game.quit_game()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
