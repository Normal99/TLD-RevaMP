#!/usr/bin/env python3
"""The host's game crashes (two machines, relay iad by default). The laptop's player is driving in the host's world;
the desktop's game process is killed. The laptop must end the session on its own (Steam's timeout), say so, go back to
the main menu — and never write the host's world into its own saves: the whole Saves folder byte-for-byte the same
(checked before the test's own autosave-slot restore), the save block on until the menu is in (v0.65.26).
The desktop's game is started again afterwards.
  tools/hostcrash.py [--no-build] [--relay iad|off] [--hang <s>: pause the host's game instead]"""
import sys, time, math, pathlib, subprocess
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, launchguard
import tldenv
from mplib import Agent, ensure_both, seeded_both, REMOTE_HOST
from roadkit import until, g, car_ready

HANG = float(sys.argv[sys.argv.index("--hang") + 1]) if "--hang" in sys.argv else 0.0
LOG = tldenv.REMOTE_LOG
RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
SSH = tldenv.ssh_args(REMOTE_HOST)
VERDICTS = []
import atexit
atexit.register(lambda: (print("\nSUMMARY"), [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]))
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def laptop_saves():
    r = subprocess.run(SSH + [f'cd "{game.RDOCS}/Saves" && find . -type f -exec md5sum {{}} + | sort -k2'], capture_output=True, text=True)
    return r.stdout

L = Agent()
ensure_both("--no-build" not in sys.argv)
killed = False
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    shift_l = g(L, "settingsscript.s.S.BAutoShift"); god_l = g(L, "kaposztaleves.s.settings.god")
    try:
        L("set kaposztaleves.s.settings.god True"); L("set settingsscript.s.S.BAutoShift True")
        rp = h("roadpoint"); yaw = rp["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        if not car_ready(L, rp["gx"] + R[0] * 1.8, rp["gz"] + R[1] * 1.8, yaw, "laptop"): sys.exit("LAPTOP NOT IN ITS CAR")
        L("autodrive on 11"); time.sleep(8)
        print("laptop in the host's world:", {k: g(L, "TLDRevamp.TestSafety." + k) for k in ("InHostWorld", "LeavingHostWorld", "BlockAutoSave")}, flush=True)
        saves0 = laptop_saves(); print(f"laptop saves: {len(saves0.splitlines())} files", flush=True)
        if HANG:
            n0 = int(subprocess.run(SSH + [f'grep -a -c "chat: \\*" "{LOG}"'], capture_output=True, text=True).stdout.strip() or 0)
            launchguard.fault("pause", seconds=HANG)
            print(f"the desktop's game was paused {HANG} s", flush=True)
            time.sleep(8)
            lines = subprocess.run(SSH + [f'grep -a "chat: \\*" "{LOG}" | tail -n +{n0 + 1}'], capture_output=True, text=True).stdout.strip().splitlines()
            print("  laptop's notices:", lines, flush=True)
            st = L("mp status")["client"]; sil = h("mp entities").get("silentTakeovers")
            verdict("THE PLAYER IS TOLD THE HOST STOPPED, THEN THAT IT'S BACK", any("isn't responding" in x for x in lines) and any("is back" in x for x in lines), "")
            verdict("STILL IN THE SESSION", st.get("ready") is True, str({k: st.get(k) for k in ("ready", "id")}))
            verdict("THE HOST DIDN'T TAKE THE PLAYER'S CAR ON WAKING", sil == 0, f"silentTakeovers {sil}")
            er = [x("errors") for x in (h, L)]
            verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"]) for e in er]))
            L("autodrive off"); L("brakecar")
            raise SystemExit
        t0 = time.monotonic()
        launchguard.fault("crash"); killed = True
        print("the desktop's game killed", flush=True)
        ended, menu, msg = None, None, None
        while time.monotonic() - t0 < 45:
            try:
                st = L("mp status"); t = time.monotonic() - t0
                if ended is None and not (st.get("client") or {}).get("ready"):
                    ended = t; msg = "(see the menu screenshot: hostcrash_menu)"
                    print(f"  t+{t:.1f}s session over on the laptop: {msg!r}; leaving block {g(L, 'TLDRevamp.TestSafety.LeavingHostWorld')}", flush=True)
                if ended is not None and g(L, "DataFromMenuScript.s.mainmenu") is True and menu is None:
                    time.sleep(3); menu = time.monotonic() - t0; print(f"  t+{menu:.1f}s the laptop is in the main menu", flush=True); break
            except Exception as ex: print("  (laptop busy:", str(ex)[:80], ")", flush=True)
            time.sleep(1)
        L("shot hostcrash_menu")
        after = {k: g(L, "TLDRevamp.TestSafety." + k) for k in ("InHostWorld", "LeavingHostWorld", "Blocked")}
        saves1 = laptop_saves()
        verdict("THE LAPTOP NOTICES THE HOST IS GONE", ended is not None and ended < 30, f"after {ended if ended is None else round(ended, 1)} s: {msg!r}")
        verdict("BACK IN THE MAIN MENU", menu is not None, f"after {menu if menu is None else round(menu, 1)} s")
        verdict("ITS OWN SAVES UNTOUCHED", saves0 == saves1, f"{len(saves1.splitlines())} files" + ("" if saves0 == saves1 else " — CHANGED"))
        verdict("SAVING NORMAL AGAIN IN THE MENU", after["InHostWorld"] is False and after["LeavingHostWorld"] is False, str(after))
        er = L("errors"); verdict("NO ERRORS ON THE LAPTOP", er["errors"] == 0 and er["exceptions"] == 0, str((er.get('kinds') or [])[:4]))
    finally:
        try:
            L(f"set settingsscript.s.S.BAutoShift {shift_l}"); L(f"set kaposztaleves.s.settings.god {god_l if god_l is not None else False}")
            L("mp stop"); L("net relay off")
        except Exception as ex: print("laptop restore", ex)
        if not killed:
            try: h("mp stop"); h("net relay off")
            except Exception: pass
        L.close()
if killed:
    game.launch(); print("the desktop's game is back:", game.running(), flush=True)
