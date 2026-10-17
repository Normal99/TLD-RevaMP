#!/usr/bin/env python3
"""Joining a host who is far from the world's start (two machines, relay iad by default). The host goes ~3 km down the
road (on the ground there), then the laptop joins: it must appear next to the host (v0.65.35: within a few metres),
standing on the ground, alive — not at the start, kilometres away. --start: the host's JoinAtHost off (A: the old way).
  tools/joinfar.py [--no-build] [--relay iad|off] [--km 3] [--start]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g, tp

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
KM = float(sys.argv[sys.argv.index("--km") + 1]) if "--km" in sys.argv else 3.0
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    try:
        start = L("mp players")["me"]
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        # KM away in a straight line, in 500 m steps (each ground-safe: held until the ground is there)
        x, z = rp["gx"], rp["gz"]
        for i in range(1, int(KM * 2) + 1):
            r = tp(h, rp["gx"] + F[0] * 500 * i, rp["gz"] + F[1] * 500 * i); time.sleep(1)
        print("host placed:", r, flush=True); time.sleep(3)
        hp = h("mp players")["me"]
        print(f"host at {hp}, {math.dist(hp[::2], start[::2]) / 1000:.1f} km from the start", flush=True)
        if "--start" in sys.argv: h("set TLDRevamp.Net.MpServer.JoinAtHost False")
        h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
        if not until(lambda: L("mp status")["client"]["ready"], 180): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        print("  joiner's spot:", g(L, "TLDRevamp.Net.MpClient.JoinSpotLast"), flush=True)
        lp0 = L("mp players")["me"]; time.sleep(5); lp = L("mp players")["me"]
        d = math.dist(lp[::2], hp[::2])
        fell = lp0[1] - lp[1]
        alive = g(L, "mainscript.s.player.died")
        hv = next((p for p in h("mp players")["players"] if p["id"] == 1), None)
        print(f"  joiner at {lp} — {d:.1f} m from the host; dropped {fell:.2f} m in 5 s; died {alive}; host sees it at {hv and hv['shown']}", flush=True)
        L("mp lookat 0") if False else None
        L("shot joinfar_laptop"); h("shot joinfar_host")
        if "--start" in sys.argv:
            verdict("A (JoinAtHost off): THE JOINER IS AT THE START, FAR AWAY", d > 1000, f"{d / 1000:.1f} km from the host")
        else:
            verdict("THE JOINER APPEARS NEXT TO THE HOST", d < 10, f"{d:.1f} m")
            verdict("STANDING ON THE GROUND, ALIVE", abs(fell) < 0.5 and alive is not True and abs(lp[1] - hp[1]) < 4, f"dropped {fell:.2f} m, height vs host {lp[1] - hp[1]:+.2f} m, died {alive}")
            verdict("THE HOST SEES IT THERE", hv is not None and math.dist(hv["shown"], lp) < 2, f"{hv and round(math.dist(hv['shown'], lp), 2)} m off")
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        try: h("set TLDRevamp.Net.MpServer.JoinAtHost True")
        except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
