#!/usr/bin/env python3
"""Debug reports shared with the host (Net.Reports, v0.66.0). Two machines over the relay: desktop hosts, laptop joins.
  1 SHARING OFF (the default): the host saves a report → the laptop is asked, sends nothing (hint shown once)
  2 SHARING ON: the host saves a report → the laptop saves its own and sends it; the host has the same bytes
    (players/<name>-<time>.zip, md5 = the laptop's zip); the game keeps going meanwhile (the laptop's ping)
  3 the laptop's own report key → sent to the host without being asked
  4 the host's "Ask players" off → the laptop isn't asked
  5 the keys: none of the game's (F1–F10, Home, End)
The laptop's sharing setting is set in memory for the test and put back (never written to its settings file).
  tools/reportshare.py [--no-build] [--relay iad|off]"""
import sys, time, pathlib, hashlib, subprocess
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, tldenv
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def md5_local(path):
    return hashlib.md5(pathlib.Path(path).read_bytes()).hexdigest()

def win_to_local(p):
    """a path the game reports under Proton (Z:\\home\\... or C:\\users\\...) as a Linux path on this machine"""
    p = p.replace("\\", "/")
    if p[:2].upper() == "Z:": return p[2:]
    if p[:2].upper() == "C:": return f"{tldenv.PREFIX}/drive_c{p[2:]}"
    if p[:2].upper() == "S:": return f"{tldenv.STEAM}/steamapps{p[2:]}"   # Proton's drive for the Steam library
    return p

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    sid = h("net me")["steamId"]
    share0 = L("get TLDRevamp.Net.Reports.Share")["value"]; ask0 = h("get TLDRevamp.Net.Reports.AskPlayers")["value"]
    try:
        h("mp host 27070"); L(f"mp joinid {sid}")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        time.sleep(5)

        # ---- 1: sharing off
        L("set TLDRevamp.Net.Reports.Share False"); h("set TLDRevamp.Net.Reports.AskPlayers True")
        l0, h0 = L("mp reports"), h("mp reports")
        print("host report 1:", h("mp report test1 sharing off"), flush=True)
        asked = until(lambda: L("mp reports")["asksIn"] > l0["asksIn"], 20)
        time.sleep(25)
        l1, h1 = L("mp reports"), h("mp reports")
        verdict("1 SHARING OFF: ASKED, SENDS NOTHING", asked and l1["asksIgnored"] == l0["asksIgnored"] + 1 and l1["chunksSent"] == l0["chunksSent"]
                and h1["received"] == h0["received"] and h1["chunksIn"] == h0["chunksIn"],
                f"asked {asked}, ignored +{l1['asksIgnored'] - l0['asksIgnored']}, chunks sent +{l1['chunksSent'] - l0['chunksSent']}, host received +{h1['received'] - h0['received']}")

        # ---- 2: sharing on, the host asks
        L("set TLDRevamp.Net.Reports.Share True")
        print("host report 2:", h("mp report test2 sharing on"), flush=True)
        pings = []
        def got2():
            pings.append(L("mp status")["client"].get("ping"))
            return h("mp reports")["received"] > h1["received"]
        ok = until(got2, 120, 1.0)
        h2, l2 = h("mp reports"), L("mp reports")
        lzip = L("get TLDRevamp.FeedbackReporter.LastZip")["value"]
        hfile = win_to_local(h2["lastReceived"])
        same, detail = False, ""
        try:
            # the host's copy must be one of the laptop's own recent reports, byte for byte
            out = subprocess.run(tldenv.ssh_args() + [
                                  f"cd {tldenv.remote_quote(tldenv.REMOTE_GAME + '/BepInEx/tldrevamp-feedback')} && ls -t TLDRevamp-report-*.zip | head -4 | xargs md5sum"],
                                 capture_output=True, text=True, timeout=30).stdout.split("\n")
            sums = {l.split()[0]: l.split()[1] for l in out if l.strip()}
            hs = md5_local(hfile)
            same = hs in sums
            detail = f"host {hs} = laptop's {sums.get(hs, '(none of its last 4)')}"
        except Exception as ex: detail = f"compare failed: {ex}"
        ps = [p for p in pings if isinstance(p, (int, float))]
        verdict("2 SHARING ON: THE HOST GOT THE LAPTOP'S REPORT", ok and l2["sent"] >= 1, f"{h2['lastReceived']}; laptop sent {l2['sent']}")
        verdict("2 SHARING ON: THE SAME BYTES", same, detail)
        verdict("2 THE GAME KEPT GOING MEANWHILE (laptop ping)", bool(ps) and max(ps) < 1000, f"ping max {max(ps) if ps else None} ms over {len(ps)} samples")

        # ---- 3: the laptop's own key
        print("laptop report 3:", L("mp report test3 laptop key"), flush=True)
        ok3 = until(lambda: h("mp reports")["received"] > h2["received"], 120, 1.0)
        verdict("3 THE LAPTOP'S OWN REPORT REACHES THE HOST", ok3, f"host received {h('mp reports')['received']}")

        # ---- 4: the host doesn't ask
        h("set TLDRevamp.Net.Reports.AskPlayers False")
        l4 = L("mp reports")
        print("host report 4:", h("mp report test4 no ask"), flush=True)
        time.sleep(8)
        verdict("4 ASK OFF: THE LAPTOP ISN'T ASKED", L("mp reports")["asksIn"] == l4["asksIn"])

        print("host screenshot in the session (overlay):", h("screenshot"), flush=True)

        # ---- 5: keys
        keys = {k: str(h(f"get TLDRevamp.Plugin.{k}.Value")["value"]) for k in ("KeyMpScreen", "KeyOverlay", "KeyReport")}
        game_keys = {"F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "Home", "End"}
        verdict("5 THE MOD'S KEYS ARE NONE OF THE GAME'S", not any(v.split(" + ")[-1] in game_keys for v in keys.values()), str(keys))
    finally:
        try: L(f"set TLDRevamp.Net.Reports.Share {share0}"); h(f"set TLDRevamp.Net.Reports.AskPlayers {ask0}")
        except Exception as ex: print("restore:", ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
