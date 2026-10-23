#!/usr/bin/env python3
"""Proximity voice between the two machines (relay iad by default), with the test tone (`mp voice tone <s>`: PCM16 through
the real send -> server relay -> playback path, no microphone needed).
  1 the laptop talks 5 m from the host: the host's speaker for it fills, has level, plays, and the body talks
  2 the host talks: the same on the laptop
  3 the laptop 150 m away (beyond Voice.RangeM): nothing reaches the host
  4 the laptop 150 m away holds "talk to everyone" (`mp voice tone <s> all`): the host hears it, 2D (spatialBlend 0)
  5 the host's session setting "everyone hears everyone": the laptop's plain voice from 150 m reaches the host, and the
    host's reaches the laptop, both 2D; the laptop's snapshot says so; off again: nothing from 150 m
  6 back near: it works again, 3D
  7 bursty Wi-Fi: gaps in the host's voice on the laptop (--ab: the fixed 80 ms buffer first)
  8 the MP screen's Session settings panel on both: four rows, the Voice row toggles on the host (the laptop's row and
    snapshot follow), the laptop's click changes nothing; screenshots `shot voice-everyone-panel-host|laptop`
  tools/voicesync.py [--no-build] [--relay iad|off] [--ab]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def speaker(x, pid):
    st = x("mp voice"); sp = next((s for s in st["speakers"] if s["id"] == pid), None)
    return st, sp

def talk(talker, listener, pid, secs=3.0, everyone=False):
    st0, sp0 = speaker(listener, pid)
    talker(f"mp voice tone {secs}" + (" all" if everyone else ""))
    best, talking = None, False
    t0 = time.monotonic()
    while time.monotonic() - t0 < secs + 1.0:
        st, sp = speaker(listener, pid)
        if sp: best = sp if best is None or sp["level"] > best["level"] else best; talking = talking or sp["talking"]
        time.sleep(0.4)
    st1, sp1 = speaker(listener, pid)
    pin = st1["in"] - st0["in"]; played = st1["played"] - st0["played"]
    return pin, played, best, talking

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    h("mp voice everyone off")   # the host remembers its panel choice: nearby voice is where the test starts
    try:
        rp = h("roadpoint"); x, z = rp["gx"], rp["gz"]
        h(f"tpground {x} {z}"); L(f"tpground {x + 5} {z}"); time.sleep(4)
        pin, played, best, talking = talk(L, h, 1)
        print("  host hears the laptop:", pin, "packets in,", played, "played, best", best, flush=True)
        verdict("1 THE HOST HEARS THE LAPTOP", pin > 10 and played > 10 and best and best["level"] > 0.001 and best["playing"] and talking, f"in {pin}, played {played}, level {best and best['level']}, talking {talking}")
        pin, played, best, talking = talk(h, L, 0)
        print("  laptop hears the host:", pin, "packets in,", played, "played, best", best, flush=True)
        verdict("2 THE LAPTOP HEARS THE HOST", pin > 10 and played > 10 and best and best["level"] > 0.001 and best["playing"] and talking, f"in {pin}, played {played}, level {best and best['level']}, talking {talking}")
        L(f"tpground {x + 150} {z}"); time.sleep(5)
        pin, played, best, talking = talk(L, h, 1)
        verdict("3 OUT OF EARSHOT (150 m): NOTHING REACHES THE HOST", pin == 0, f"in {pin}, played {played}")
        pin, played, best, talking = talk(L, h, 1, everyone=True)
        verdict("4 TALK TO EVERYONE FROM 150 m: THE HOST HEARS IT, 2D", pin > 10 and played > 10 and best and best["level"] > 0.001 and best.get("everyone") and best.get("spatialBlend") == 0,
                f"in {pin}, played {played}, level {best and best['level']}, everyone {best and best.get('everyone')}, blend {best and best.get('spatialBlend')}")
        h("mp voice everyone on"); time.sleep(2)
        seen = L("mp voice").get("sessionEveryone")
        pin, played, best, talking = talk(L, h, 1)
        pin2, played2, best2, talking2 = talk(h, L, 0)
        verdict("5a SESSION 'EVERYONE': PLAIN VOICES REACH 150 m BOTH WAYS, 2D, THE JOINER SEES THE SETTING",
                seen and pin > 10 and played > 10 and best and best.get("everyone") and pin2 > 10 and played2 > 10 and best2 and best2.get("everyone") and best2.get("spatialBlend") == 0,
                f"joiner sees {seen}; to host in {pin} played {played}; to laptop in {pin2} played {played2}, blend {best2 and best2.get('spatialBlend')}")
        h("mp voice everyone off"); time.sleep(2)
        pin, played, best, talking = talk(L, h, 1)
        verdict("5b SESSION BACK TO NEARBY: NOTHING FROM 150 m", pin == 0 and not L("mp voice").get("sessionEveryone"), f"in {pin}")
        L(f"tpground {x + 5} {z}"); time.sleep(5)
        pin, played, best, talking = talk(L, h, 1)
        verdict("6 BACK NEAR: HEARD AGAIN, 3D", pin > 10 and played > 10 and best and best["level"] > 0.001 and not best.get("everyone") and best.get("spatialBlend") == 1,
                f"in {pin}, played {played}, level {best and best['level']}, blend {best and best.get('spatialBlend')}")
        # 7 the laptop's bursty Wi-Fi (power saving, ~300 ms bursts): mid-speech gaps in the host's voice on the laptop
        #   (underruns beyond the one at the end of each utterance), adaptive buffer off (A) then on (B)
        for adaptive in ([False, True] if "--ab" in sys.argv else [True]):
            L(f"set TLDRevamp.Net.Voice.AdaptiveJitter {adaptive}")
            gaps, primes = 0, []
            for k in range(3):
                u0 = (speaker(L, 0)[1] or {}).get("underruns", 0)
                pin, played, best, talking = talk(h, L, 0, 6.0)
                sp = speaker(L, 0)[1] or {}
                gaps += max(0, sp.get("underruns", 0) - u0 - 1); primes.append(sp.get("primeMs"))
                time.sleep(1.5)
            tag = "B ADAPTIVE BUFFER" if adaptive else "A FIXED 80 MS (before v0.65.31)"
            verdict(f"7 {tag}: THE HOST'S VOICE ON THE LAPTOP WITHOUT GAPS (one while it learns the link)", gaps <= 1 if adaptive else True, f"{gaps} gaps in 3 x 6 s; buffer {primes} ms")
        L("set TLDRevamp.Net.Voice.AdaptiveJitter True")
        # 8 the MP screen's Session settings panel: four rows, the Voice row toggles on the host, read-only on the laptop
        # opened the player's way: the pause menu first, its Multiplayer button a moment later
        for s_ in (h, L): s_("call mainscript.s.PressedEscape False False")
        time.sleep(1)
        for s_ in (h, L): s_("call menuhandler.s.PressedMultiplayerInGame")
        time.sleep(1.5)
        for s_ in (h, L): s_("mp screen settings")
        time.sleep(1)
        sh, sl = h("mp screen"), L("mp screen")
        h("mp screen setting 3"); time.sleep(1.5)
        on_h, on_l = h("mp screen")["settingRows"][3], L("mp screen")["settingRows"][3]
        heard = L("mp voice")["sessionEveryone"]
        shots = [s_(f"shot voice-everyone-panel-{n}").get("path") for s_, n in ((h, "host"), (L, "laptop"))]
        rev0 = h("devaccess")["settings"]["revision"]
        L("mp screen setting 3"); time.sleep(1)
        rev1, still = h("devaccess")["settings"]["revision"], L("mp screen")["settingRows"][3]
        h("mp screen setting 3"); time.sleep(1.5)
        off_h, off_l = h("mp screen")["settingRows"][3], L("mp voice")["sessionEveryone"]
        verdict("8 SESSION PANEL: FOUR ROWS, VOICE TOGGLES ON THE HOST, READ-ONLY ON THE LAPTOP",
                sh["settingsOpen"] and sl["settingsOpen"] and all(r and "waiting" not in r for r in sh["settingRows"] + sl["settingRows"])
                and len(sh["settingRows"]) == 4 and sh["settingRows"][3] == "Voice chat: Nearby only" and sh["settingsHostEditable"]
                and not sl["settingsHostEditable"] and on_h == on_l == "Voice chat: Everyone hears everyone" and heard
                and rev1 == rev0 and still == on_l and off_h == "Voice chat: Nearby only" and not off_l,
                f"open: host {sh['open']}/{sh['settingsOpen']}, laptop {sl['open']}/{sl['settingsOpen']}; editable: host {sh['settingsHostEditable']}, "
                f"laptop {sl['settingsHostEditable']}; host rows {sh['settingRows']}, laptop rows {sl['settingRows']}; toggled: host {on_h!r}, laptop {on_l!r}, snapshot {heard}; "
                f"laptop's click: revision {rev0} → {rev1}, row {still!r}; back: {off_h!r}, laptop {off_l}; shots {shots}")
        for s_ in (h, L): s_("mp screen settings"); s_("mp screen close"); s_("call mainscript.s.PressedEscape False False")
        er = [s_("errors") for s_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        try: h("mp voice everyone off")
        except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
