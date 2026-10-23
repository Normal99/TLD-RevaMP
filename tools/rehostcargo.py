#!/usr/bin/env python3
"""What lies in a car when the host stops hosting and hosts again (lobby3.py phase S: a radio and a lamp put on the
seats were gone from the car afterwards). The host alone, the owner's save: two things onto the car's seats, then
`mp stop` + `mp host`; before and after, the car's shared group (mp members), what the game has physics-locked into
it (mp physlocks) and the loose things in its box (mp entities + relpos).
  tools/rehostcargo.py [--save Playing] [--rounds 2]
Needs TLD_PLAYSAVE_DIR (the save's TLDsave*_<name>.* set)."""
import sys, os, time, math, shutil, pathlib, json
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import lock_machines

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
SAVE, ROUNDS = arg("--save", "Playing"), arg("--rounds", 2, int)
SRC = pathlib.Path(os.environ.get("TLD_PLAYSAVE_DIR", "")).expanduser()
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, bool(ok))); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
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
def inside(r): return abs(r[0]) < 1.4 and -0.8 < r[1] < 2.6 and abs(r[2]) < 3.4
def cargo(s, car):
    es = ents(s); c = es.get(car)
    if not c: return {}
    cand = [n for n, e in es.items() if not e["car"] and e["part"] < 0 and not e.get("dormant") and math.dist(e["pos"], c["pos"]) < 5]
    if not cand: return {}
    return {int(k): v for k, v in s(f"mp relpos {car} {' '.join(map(str, cand))}")["items"].items() if inside(v)}
def car_named(s, name):
    me = s("gpos")
    return min(((math.hypot(e["pos"][0] - me["gx"], e["pos"][2] - me["gz"]), n) for n, e in ents(s).items() if e["car"] and e.get("name") == name), default=(0, None))[1]
def look(s, car, tag):
    c = ents(s)[car]["pos"]
    m = s(f"mp members {car}"); pl = s(f"mp physlocks {c[0]} {c[2]} 8")
    print(f"  [{tag}] members of {car}: {json.dumps(m)[:600]}", flush=True)
    print(f"  [{tag}] physlocks near it: {json.dumps(pl)[:600]}", flush=True)
    box = cargo(s, car)
    print(f"  [{tag}] loose in its box: {[(n, ents(s)[n].get('name')) for n in box]}", flush=True)
    return box

if not SRC.is_dir() or not list(SRC.glob(f"TLDsave*_{SAVE}.*")): sys.exit(f"no TLDsave*_{SAVE}.* in TLD_PLAYSAVE_DIR ({SRC})")
lock_machines()
game.deploy_local()
with game.ProtectedSession():
    copied = []
    try:
        if game.running(): game.quit_game()
        game.launch()
        for f in SRC.glob(f"TLDsave*_{SAVE}.*"):
            shutil.copy2(f, game.SAVES / f.name); copied.append(game.SAVES / f.name)
        h = game.loadsave(SAVE)
        h("set TLDRevamp.TestSafety.BlockAutoSave True"); h("bg on"); h("set UnityEngine.QualitySettings.vSyncCount 0")
        h("mp host 27070"); time.sleep(5)
        car = car_named(h, "Car06P(Clone)")
        if not car: sys.exit("no Car06P near the host")
        c0 = ents(h)[car]["pos"]; h(f"tpground {c0[0] - 4} {c0[2] - 4}"); time.sleep(3)
        for k, item in enumerate((320, 39)):
            before = set(ents(h)); h(f"devspawn {item}")
            n = until(lambda: next((x for x, e in ents(h).items() if x not in before and not e["car"]), None), 15)
            if n: print(f"  item {item} (net {n}) onto seat {k}:", h(f"mp putincar {car} {n} {k}"), flush=True)
        time.sleep(5)
        box = look(h, car, "hosting")
        names = sorted(ents(h)[n].get("name") for n in box)
        for r in range(1, ROUNDS + 1):
            h("mp stop"); time.sleep(3)
            look(h, car, f"stopped {r}") if False else None
            h("mp host 27070"); time.sleep(6)
            car = car_named(h, "Car06P(Clone)") if car not in ents(h) or ents(h)[car].get("name") != "Car06P(Clone)" else car
            after = look(h, car, f"hosting again {r}")
            an = sorted(ents(h)[n].get("name") for n in after)
            verdict(f"ROUND {r}: WHAT LAY IN THE CAR STILL LIES IN IT, EACH ITS OWN SHARED OBJECT", set(names) <= set(an), f"before {names}, after {an}")
    finally:
        try: game.quit_game()
        except Exception as e: print("quit:", e)
        for f in copied:
            try: f.unlink()
            except OSError: pass
print(f"REHOSTCARGO: {sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} PASS", flush=True)
