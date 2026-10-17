#!/usr/bin/env python3
"""Headless feasibility spike: launch the game with Unity's -batchmode -nographics and report how far the mod
gets — BepInEx up, bridge up, world startable, MP hostable — and what breaks. Decides the dedicated-server plan."""
import sys, time, subprocess, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, tldenv
from bench import Bridge

game.quit_game()
time.sleep(2)
print("launching with -batchmode -nographics")
subprocess.Popen(["steam", "-applaunch", "1017180", "-batchmode", "-nographics"],
                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
t0 = time.time()
up = False
while time.time() - t0 < 240:
    try:
        b = Bridge(); b("ping"); up = True; break
    except Exception:
        time.sleep(3)
if not up:
    print("RESULT: bridge never came up in 240 s — BepInEx/Unity likely failed in batchmode")
    lg = tldenv.LOG
    if lg.exists():
        print("--- last BepInEx log lines:")
        print("\n".join(lg.read_text(errors="replace").splitlines()[-15:]))
    sys.exit(0)
b = Bridge()
print(f"bridge up after {time.time()-t0:.0f} s — batchmode at least boots the game + BepInEx")
checks = {}
for name, cmd in [("mainmenu", "get DataFromMenuScript.s.mainmenu"),
                  ("player", "get mainscript.s.player"),
                  ("savedata", "get savedatascript.s.items.Count"),
                  ("map", "get menuhandler.s.currentMainMap")]:
    try: checks[name] = b(cmd)["value"]
    except Exception as e: checks[name] = f"ERR {str(e)[:60]}"
print("state:", checks)
if checks.get("mainmenu") is True:
    print("starting a world in batchmode...")
    b("set DataFromMenuScript.s.seed 20260924")
    b("call menuhandler.s.PressedStart")
    ok = False
    for _ in range(60):
        time.sleep(3)
        try:
            if (b("get DataFromMenuScript.s.mainmenu")["value"] is False
                    and b("get mainscript.s.player")["value"] is not None): ok = True; break
        except Exception as e:
            print("  world-start error:", str(e)[:120]); break
    print("world started in batchmode:", ok)
    if ok:
        time.sleep(10)
        try: print("  items:", b("get savedatascript.s.items.Count")["value"])
        except Exception as e: print("  items ERR:", str(e)[:80])
        print("  mp host:", b("mp host 27070"))
        time.sleep(2)
        try: print("  entities:", b("mp entities")["count"])
        except Exception as e: print("  entities ERR:", str(e)[:80])
        try: b("mp stop")
        except Exception: pass
b("quit") if False else None
game.quit_game()
