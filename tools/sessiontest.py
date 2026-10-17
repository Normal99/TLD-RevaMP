#!/usr/bin/env python3
"""Session features over the relay (v0.65): password, chat, voice, mute, kick, the multiplayer screen with a real player.
The desktop hosts with a password; the laptop joins.
  1 no password: rejected (code 1), the laptop stays in its own game
  2 a wrong password: rejected (code 1)
  3 the right password: joins; "joined" notice on both
  4 chat both ways (the host's line on the laptop, the laptop's on the host); 4b a line with rich-text tags arrives defused
  5 voice: a test tone from the laptop plays on the host (speaker playing, level > 0), and the other way
  6 mute: the host mutes the laptop: packets still arrive, none played
  7 range: the laptop 200 m away: its tone is not relayed to the host
  8 the screen on the host with two players (avatars, ping, Mute/Kick); screenshots
  9 kick from the screen (Kick → Yes): the laptop goes to the main menu, where its Multiplayer screen opens with the reason;
    "was kicked" notice on the host
 10 the kicked laptop rejoins with the right password: rejected (code 2)
  + Steam capture probe on both machines (the real microphone path; no sound needed)
  tools/sessiontest.py [--no-build] [--relay iad|off]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, tldenv
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
SHOTS = tldenv.GAME_DIR / "BepInEx/tldrevamp-feedback"
PW = "tumble42"
VERDICTS = []

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def shot(b, tag):
    time.sleep(1.2)
    p = b("screenshot")["path"].split("\\")[-1]
    time.sleep(1.0)
    print(f"  shot {tag}: {SHOTS / p}", flush=True)

def lines(s): return [tuple(x) for x in s("mp session")["lines"]]
def said(s, text): return any(x[2] == text for x in lines(s))
def notice(s, part): return any(x[0] == 1 and part in x[2] for x in lines(s))

def speaker(s, sid):
    for sp in s("mp voice")["speakers"]:
        if sp["id"] == sid: return sp
    return None

def tone_heard(talker, listener, sid, secs=3):
    """talker plays a tone; the best sample of the listener's speaker for sid while it lasts"""
    v0 = listener("mp voice")
    talker(f"mp voice tone {secs}")
    best = None
    t = time.time()
    while time.time() - t < secs + 1.5:
        sp = speaker(listener, sid)
        if sp and (best is None or sp["level"] > best["level"]): best = sp
        time.sleep(0.25)
    v1 = listener("mp voice")
    last = speaker(listener, sid)
    if best is not None and last: best = dict(best, underruns=last["underruns"], dropped=last["dropped"], fills=last["fills"])
    return best, v1["in"] - v0["in"], v1["played"] - v0["played"]

def in_menu(s):
    try:
        if s("get DataFromMenuScript.s.mainmenu").get("value") is True: return True
    except Exception: pass
    try: return s("get mainscript.s.player").get("value") is None
    except Exception: return False

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    print("  probe host:", h("mp voice probe"), "laptop:", L("mp voice probe"), flush=True)
    h(f"mp host 27070 {PW}")
    hid = h("net me")["steamId"]; lsid = L("net me")["steamId"]
    print("  session:", h("mp session"), flush=True)
    gods = {}
    try:
        # 1 no password
        L(f"mp joinid {hid}")
        r = until(lambda: (lambda x: x if x["endCode"] or x["reject"] else None)(L("mp session")), 60)
        print("  1:", r, flush=True)
        verdict("1 NO PASSWORD: REJECTED", bool(r) and r["endCode"] == 1 and not in_menu(L), f"code {r and r['endCode']}, reason {r and r['endReason']}, in menu {in_menu(L)}")
        time.sleep(3)
        # 2 a wrong password
        L(f"mp joinid {hid} wrong")
        r = until(lambda: (lambda x: x if x["endCode"] or x["reject"] else None)(L("mp session")), 60)
        print("  2:", r, flush=True)
        verdict("2 WRONG PASSWORD: REJECTED", bool(r) and r["endCode"] == 1, f"code {r and r['endCode']}, reason {r and r['endReason']}")
        time.sleep(3)
        # 3 the right password
        L(f"mp joinid {hid} {PW}")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        lid = L("mp status")["client"].get("id")
        for nm, s_ in (("host", h), ("laptop", L)):
            gods[nm] = (s_, s_("get kaposztaleves.s.settings.god").get("value"))
            s_("set kaposztaleves.s.settings.god True")
        time.sleep(3)
        hs, ls = h("mp session"), L("mp session")
        print("  3 host:", hs, "\n    laptop:", ls, flush=True)
        verdict("3 RIGHT PASSWORD: JOINED, NOTICE ON BOTH", until(lambda: notice(h, "joined"), 10) and until(lambda: notice(L, "joined"), 10) and len(hs["players"]) == 2,
                f"players host {len(hs['players'])} laptop {len(ls['players'])}")
        # 4 chat
        h("mp chat say hello from the host"); L("mp chat say hi from the laptop")
        ok = until(lambda: said(L, "hello from the host") and said(h, "hi from the laptop"), 10)
        print("  4 host lines:", lines(h), "\n    laptop lines:", lines(L), flush=True)
        verdict("4 CHAT BOTH WAYS", bool(ok))
        # 4b rich text from a player is shown as text, not applied (v0.65.54: "<size=300>" filled everyone's screen)
        L("mp chat say <size=300>BIG</size> <3")
        ok = until(lambda: any("BIG" in x[2] for x in lines(h)), 10)
        got = next((x[2] for x in lines(h) if "BIG" in x[2]), None)
        verdict("4b CHAT: A PLAYER'S RICH-TEXT TAGS ARRIVE DEFUSED", bool(ok) and got is not None and "<" not in got, f"the host shows {got!r}")
        pl = h("mp session")["players"]
        names = [x.get("name") if isinstance(x, dict) else x[1] if isinstance(x, list) else x for x in pl]   # rows are [id, name, steamId, ...]
        verdict("4c NAMES: NO TAGS, AT MOST 32 CHARACTERS", len(names) == 2 and all(isinstance(n, str) and "<" not in n and len(n) <= 32 for n in names), f"names {names}")
        # 5 voice, near (10 m apart on the road)
        p = h("gpos"); h(f"tpground {p['gx']} {p['gz']}"); L(f"tpground {p['gx'] + 10} {p['gz']}"); time.sleep(6)
        sp, n_in, n_played = tone_heard(L, h, lid)
        print(f"  5a laptop→host: in {n_in} played {n_played} speaker {sp}", flush=True)
        ok_a = sp and sp["playing"] and sp["level"] > 0.2 and n_played >= 65   # 3 s at 25 blocks/s = 75; the tone's RMS is 0.26
        sp2, n_in2, n_played2 = tone_heard(h, L, 0)
        print(f"  5b host→laptop: in {n_in2} played {n_played2} speaker {sp2}", flush=True)
        ok_b = sp2 and sp2["playing"] and sp2["level"] > 0.2 and n_played2 >= 65
        verdict("5 VOICE BOTH WAYS", bool(ok_a and ok_b), f"laptop→host played {n_played} (level {sp and sp['level']}), host→laptop played {n_played2} (level {sp2 and sp2['level']})")
        # 6 mute
        h(f"mp voice mute {lsid}")
        _, n_in, n_played = tone_heard(L, h, lid, 2)
        h(f"mp voice mute {lsid}")   # unmute (toggle)
        print(f"  6 muted: in {n_in} played {n_played}", flush=True)
        verdict("6 MUTE: ARRIVES, NOT PLAYED", n_in > 20 and n_played == 0, f"in {n_in}, played {n_played}")
        # 7 range
        L(f"tpground {p['gx'] + 200} {p['gz']}"); time.sleep(6)
        _, n_in, n_played = tone_heard(L, h, lid, 2)
        print(f"  7 200 m: in {n_in} played {n_played}", flush=True)
        verdict("7 OUT OF RANGE: NOT RELAYED", n_in == 0, f"in {n_in}")
        L(f"tpground {p['gx'] + 10} {p['gz']}"); time.sleep(4)
        # 8 the screen with two players
        h("call mainscript.s.PressedEscape False False"); time.sleep(1)
        h("call menuhandler.s.PressedMultiplayerInGame"); time.sleep(2.5)   # the avatars load
        sc = h("mp screen"); print("  8 screen:", sc, flush=True)
        shot(h, "8 host screen (self)")
        sc = h("mp screen tile 1"); print("    tile 1:", sc, flush=True)
        shot(h, "8 host screen (laptop selected)")
        verdict("8 SCREEN LISTS BOTH, LAPTOP SELECTABLE", len(sc.get("tiles", [])) == 2 and sc.get("kick") and sc.get("mute") == "Mute", f"tiles {sc.get('tiles')}")
        L("mp chat open"); time.sleep(0.5); shot(L, "8 laptop chat input")
        L("mp chat close")
        # 9 kick through the screen
        print("    kick:", h("mp screen kick"), flush=True); shot(h, "9 kick confirm")
        print("    yes:", h("mp screen yes"), flush=True)
        menu = until(lambda: in_menu(L), 40)
        ls = L("mp session")
        print("  9 laptop:", ls, "\n    host lines:", lines(h), flush=True)
        verdict("9 KICK: LAPTOP TO THE MENU, NOTICE", bool(menu) and notice(L, "kicked by the host") and until(lambda: notice(h, "kicked"), 10),
                f"menu {bool(menu)}, laptop's last line {ls['lines'][-1:]}")   # the screen opening takes the end reason
        if menu:
            lsc = until(lambda: (lambda x: x if x.get("open") else None)(L("mp screen")), 15)
            print("    laptop screen:", lsc, flush=True)
            verdict("9b THE MENU OPENS THE SCREEN WITH THE REASON", bool(lsc) and "kicked" in (lsc.get("message") or ""), f"message {lsc and lsc.get('message')}")
            shot(L, "9 laptop main menu")
        h("mp screen close"); h("call mainscript.s.PressedEscape False False")
        # 10 rejoin
        time.sleep(3)
        L(f"mp joinid {hid} {PW}")
        r = until(lambda: (lambda x: x if x["endCode"] or x["reject"] else None)(L("mp session")), 60)
        print("  10:", r, flush=True)
        verdict("10 KICKED: REJOIN REJECTED", bool(r) and r["endCode"] == 2, f"code {r and r['endCode']}, reason {r and r['endReason']}")
        print("  host voice:", h("mp voice"), "\n  host session:", h("mp session"), flush=True)
    finally:
        for nm, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", nm, ex)
        for s_ in (L, h):
            try: s_("mp stop")
            except Exception as ex: print("stop", ex)
        if RELAY != "off": h("net relay off"); L("net relay off")
L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
