#!/usr/bin/env python3
"""Bugs 1/2 (2026-10-03): the others couldn't hear a player hum/scream or see them pee. Desktop only: the host plus a
bot standing 5 m away that sends the HOST's own pose as its own (Mp.BotPoses) — what the host's player does shows on
the bot's model, through the same message, relay and model code a real player's pose takes.
  1 hum key held (PlayerActs.TestHum): the host's hum plays; on the bot's model the same clip plays
  2 let go: the model's hum fades out and stops
  3 pee key held (bladder filled first): the host's stream emits; the model's copy emits particles, its pour sound plays
  4 let go: the model's stream stops
  tools/actstest.py [--no-build]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

A = "TLDRevamp.Net.PlayerActs"
VERDICTS = []

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def model(b):
    r = b("mp acts"); ms = r["models"]
    return r, (ms[0] if ms else {})

if game.running(): game.quit_game()
if "--no-build" not in sys.argv: game.build()
else: game.deploy_local()
game.launch()
with game.ProtectedSession():
    game.newgame()
    b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True")
    god = b("get kaposztaleves.s.settings.god").get("value"); b("set kaposztaleves.s.settings.god True")
    try:
        p = b("gpos"); b(f"tpground {p['gx'] + 15} {p['gz']}"); time.sleep(3)
        b("set TLDRevamp.Net.Mp.BotCars False"); b("set TLDRevamp.Net.Mp.BotPoses True")
        b("mp host 27070"); print("  bots:", b("mp bots 1 5 0"), flush=True)
        time.sleep(8)   # the bot joins, is ready, its model is placed
        b(f"set {A}.TestHum True"); time.sleep(3)
        r, m = model(b); print("  1 humming:", r, flush=True)
        verdict("1 HUM HEARD ON THE OTHER MODEL", r["local"]["humPlaying"] and m.get("humPlaying") and m.get("humVol", 0) > 0, f"local {r['local']['humPlaying']}, model {m}")
        b(f"set {A}.TestHum False"); time.sleep(3)
        r, m = model(b); print("  2 stopped:", r, flush=True)
        verdict("2 HUM STOPS", not m.get("humPlaying") or m.get("humVol", 1) < 0.02, f"model {m}")
        print("  bladder:", b("mp acts fill")["local"], flush=True)
        b(f"set {A}.TestPiss True"); time.sleep(3)
        r, m = model(b); print("  3 peeing:", r, flush=True)
        verdict("3 PEE SEEN AND HEARD ON THE OTHER MODEL", r["local"]["pissEmit"] and m.get("pissEmit") and m.get("pissParticles", 0) > 0 and m.get("pourPlaying"),
                f"local {r['local']}, model {m}")
        b(f"set {A}.TestPiss False"); time.sleep(1.5)
        r, m = model(b); print("  4a letting go (the game's valve closes over ~4.6 s):", r, flush=True)
        same = m.get("pissEmit") == r["local"]["pissEmit"] and abs(m.get("pissParticles", 0) - r["local"]["pissParticles"]) <= max(30, r["local"]["pissParticles"] * 0.3)
        time.sleep(5)
        r, m = model(b); print("  4b stopped:", r, flush=True)
        verdict("4 PEE WINDS DOWN AS THE PLAYER'S OWN AND STOPS", same and not r["local"]["pissEmit"] and not m.get("pissEmit") and not m.get("pourPlaying"), f"while closing same {same}; after: local {r['local']['pissEmit']} model {m}")
        b("screenshot")
    finally:
        for k, v in ((f"{A}.TestHum", False), (f"{A}.TestPiss", False), ("TLDRevamp.Net.Mp.BotPoses", False)):
            try: b(f"set {k} {v}")
            except Exception as ex: print("restore", k, ex)
        try: b("mp stop")
        except Exception: pass
        b(f"set kaposztaleves.s.settings.god {god if god is not None else False}")
game.quit_game()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
