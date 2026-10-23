#!/usr/bin/env python3
"""The TV Browser example mod (src/Mods/TvBrowser) in a session, two players (the host and the second machine or the
second Steam user): a TV spawned between them, switched on, and its page used from the keyboard.
  A. both machines have the TV, and see it switched on
  B. both browsers run (downloaded once per game copy, with the consent the TV asks for)
  C. the host takes the keyboard and opens an address: both machines show it (same shared address, same page)
  D. typed keys reach the other machine's page: the same text in the same field
  E. scrolling: the other machine's page follows the driver's position
  F. the second player takes the keyboard and goes somewhere: the host follows
  G. local and private addresses are refused
  H. a replay recorded on the host holds the TV's pictures and shows them on the TV's copy
  TLD_TEST_MODS=TvBrowser tools/tvbrowser.py [--no-build] [--relay iad|off]"""
import os
os.environ.setdefault("TLD_TEST_MODS", "TvBrowser")
import sys, time, json, math, pathlib, urllib.parse
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until

def arg(name, default): return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default
RELAY = arg("--relay", "off")
PAGE = arg("--page", "https://example.com/")
LONG = arg("--long", "https://en.wikipedia.org/wiki/Road_trip")
FORM = arg("--form", "https://html.duckduckgo.com/html/")
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, bool(ok))); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def call(side, what, *args):
    try: return str(side("call TvBrowser.Probe." + " ".join([what, *map(str, args)]))["value"])
    except Exception as e: return "ERROR " + str(e)[:300]

def esc(text): return urllib.parse.quote(text, safe="")

def state(side):
    try: return json.loads(call(side, "State"))
    except Exception: return {}

def js(side, expression, timeout=5):
    """an expression in that machine's page; its JSON answer"""
    call(side, "Eval", esc(expression))
    t0 = time.time()
    while time.time() - t0 < timeout:
        a = call(side, "Answer")
        if a != "(waiting)": return a
        time.sleep(0.2)
    return "(no answer)"

def same_page(h, L, want, timeout=30):
    ok = until(lambda: all((state(s).get("tabUrl") or "").startswith(want) and not state(s).get("loading") for s in (h, L)), timeout)
    return ok, state(h).get("tabUrl"), state(L).get("tabUrl")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
    if RELAY != "off": relay_both(h, L, RELAY)
    if not join_both(h, L): sys.exit("THE SECOND PLAYER NEVER JOINED")
    try:
        # A. a TV between the two players, facing the host
        g = h("gpos"); x, z = float(g["gx"]), float(g["gz"])
        L(f"tpground {x + 3} {z}"); time.sleep(3)
        print(" spawn TV:", h("devspawn ujtv")); time.sleep(4)
        until(lambda: state(h).get("screen") and state(L).get("screen"), 30)
        verdict("A. both machines have the TV", state(h).get("screen") and state(L).get("screen"), f"host {call(h, 'List')[:200]} | second {call(L, 'List')[:200]}")
        print(" power:", call(h, "Power", True))
        until(lambda: state(L).get("on"), 15)
        verdict("A. the second player sees it switched on", state(h).get("on") and state(L).get("on"), f"host on {state(h).get('on')}, second on {state(L).get('on')}")

        # B. the browsers (each game copy downloads its own once, as a player agrees to on the TV)
        for side in (h, L):
            if not state(side).get("installed"): print(" accept:", call(side, "Accept"))
        t0 = time.time()
        until(lambda: all(state(s).get("installed") for s in (h, L)) or any(state(s).get("browser") == "Failed" for s in (h, L)), 600)
        print(f" browsers after {time.time() - t0:.0f} s: host {call(h, 'Browser_')} | second {call(L, 'Browser_')}")
        verdict("B. both browsers installed", all(state(s).get("installed") for s in (h, L)))

        # C. the host takes the keyboard and opens a page: the shared address and the page on both machines
        print(" host focus:", call(h, "Focus")[:200])
        until(lambda: all(state(s).get("ready") for s in (h, L)), 90)
        verdict("C. both machines' pages are ready", all(state(s).get("ready") for s in (h, L)), f"host {call(h, 'Browser_')} | second {call(L, 'Browser_')}")
        verdict("C. the host drives", state(h).get("driver") == state(h).get("me") and state(L).get("driver") == state(h).get("me"),
                f"driver {state(h).get('driver')}/{state(L).get('driver')}, host is {state(h).get('me')}")
        print(" go:", call(h, "Go", esc(PAGE)))
        ok, a, b = same_page(h, L, PAGE)
        verdict("C. both machines show the address", ok and state(L).get("url") == PAGE, f"host '{a}', second '{b}', shared '{state(L).get('url')}'")
        time.sleep(2)
        for label, side in (("host", h), ("second", L)):
            sc = call(side, "Screen"); print(f" {label} screen:", sc)
            verdict(f"C. the page is on the {label}'s TV", "on material True" in sc and state(side).get("frames", 0) > 0, sc)
            print(f" {label} shot:", call(side, "Shot", f"tv_{label}_page"))
        a, b = js(h, "document.title"), js(L, "document.title")
        verdict("C. the same page title", a == b and a.startswith('"') and len(a) > 2, f"{a} / {b}")

        # D. typing: the host's keys into a field on both machines
        call(h, "Go", esc(FORM)); same_page(h, L, FORM)
        time.sleep(1)
        for _ in range(6):
            if js(h, "document.activeElement && document.activeElement.name") == '"q"': break
            call(h, "Key", 9, 0); time.sleep(0.4)
        call(h, "Type", esc("long drive mods")); time.sleep(2)
        a = js(h, "document.querySelector('input[name=q]') && document.querySelector('input[name=q]').value")
        b = js(L, "document.querySelector('input[name=q]') && document.querySelector('input[name=q]').value")
        verdict("D. typed text in the same field on both machines", a == b == '"long drive mods"', f"host {a}, second {b}")
        print(f" keys sent {state(h).get('sent')}, received by the second {state(L).get('received')}")
        call(h, "Key", 13, 0); time.sleep(6)
        a, b = js(h, "document.title"), js(L, "document.title")
        verdict("D. Enter submits on both machines alike", a == b and "long drive" in a.lower(), f"host {a}, second {b}")

        # E. scrolling: the second player's page follows the driver's position
        call(h, "Go", esc(LONG)); same_page(h, L, LONG, 40); time.sleep(2)
        for _ in range(4): call(h, "Wheel", 600); time.sleep(0.3)
        call(h, "Key", 34, 0); time.sleep(4)
        hy, ly = js(h, "Math.round(scrollY)"), js(L, "Math.round(scrollY)")
        try: gap = abs(float(hy) - float(ly)); far = float(hy)
        except ValueError: gap, far = 9999, 0
        verdict("E. both pages scrolled to the same place", far > 1000 and gap <= 40, f"host {hy}, second {ly}")
        print(" shots:", call(h, "Shot", "tv_host_scrolled"), call(L, "Shot", "tv_second_scrolled"))

        # F. the second player takes the keyboard and goes somewhere; the host follows
        L(f"tpground {x + 1.5} {z}"); time.sleep(2)
        print(" second focus:", call(L, "Focus")[:160])
        until(lambda: state(h).get("driver") == state(L).get("me"), 10)
        verdict("F. the second player drives now", state(h).get("driver") == state(L).get("me"), f"driver {state(h).get('driver')}, second is {state(L).get('me')}")
        print(" second go:", call(L, "Go", esc(PAGE)))
        ok, a, b = same_page(h, L, PAGE)
        verdict("F. the host follows", ok, f"host '{a}', second '{b}'")
        print(" back:", call(L, "Back")); time.sleep(5)
        a, b = state(h).get("tabUrl"), state(L).get("tabUrl")
        verdict("F. Back on both machines", (a or "").startswith(LONG.split("#")[0]) and a == b, f"host '{a}', second '{b}'")

        # G. local and private addresses
        for bad in ("http://127.0.0.1:8080/", "http://localhost/", "http://" + ".".join(["192", "168", "1", "1"]) + "/"):
            r = call(L, "Go", esc(bad))
            verdict(f"G. refused: {bad}", r == "refused", r)
        print(" second unfocus:", call(L, "Unfocus"))

        # H. a replay on the host: the TV's pictures recorded and shown on its copy
        print(" host focus:", call(h, "Focus")[:120])
        r = h("replay record"); print(" record:", r)
        call(h, "Go", esc(PAGE)); time.sleep(4)
        call(h, "Go", esc(LONG)); time.sleep(5)
        for _ in range(3): call(h, "Wheel", 500); time.sleep(1)
        time.sleep(3)
        st = h("replay status")["recorder"]; print(" recorder:", st)
        stop = h("replay stop"); print(" stop:", stop)
        verdict("H. TV pictures recorded", st.get("modKeys", 0) >= 2 and state(h).get("recorded", 0) >= 2, f"modKeys {st.get('modKeys')}, TV recorded {state(h).get('recorded')}, failed {state(h).get('failed')}")
        call(h, "Unfocus")
        shown0 = state(h).get("shown", 0)
        print(" play:", h("replay play")); time.sleep(6)
        v = h("replay status")["viewer"]; print(" viewer:", v)
        print(" replay:", call(h, "ReplayStatus"))
        verdict("H. the replay shows the TV's pictures", v.get("modShown", 0) >= 1 and state(h).get("shown", 0) > shown0, f"modShown {v.get('modShown')}, TV shown {state(h).get('shown')}")
        try: print(" replay shot:", h("screenshot").get("path"))
        except Exception as e: print(" replay shot failed:", e)
        h("replay end")

        for label, side in (("host", h), ("second", L)):
            e = side("errors"); print(f" {label} errors:", str(e)[:400])
            verdict(f"no errors on the {label}", e.get("errors", 1) == 0 and e.get("exceptions", 1) == 0, str(e)[:200])
    finally:
        for side in (h, L):
            try: call(side, "Unfocus"); side("mp stop")
            except Exception: pass

print(f"{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed")
sys.exit(0 if all(ok for _, ok in VERDICTS) else 1)
