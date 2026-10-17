#!/usr/bin/env python3
"""A long session with many players, as close to a real one as two machines allow (architecture §9a). The desktop
hosts; the laptop joins as a real player over the Steam relay (≈190 ms) with simulated packet loss on top, and runs the
other players as bots in its own game, each its own Steam connection to the host (Mp.AddRemoteBots): nothing loopback,
every byte over the internet. Players: half the bots near the host, half spread up to --spread km away. During the
session bots leave and new ones join every --churn s. The laptop's player drives about the whole time.
Each minute: host frame p50/p99/max, players the host has, bots ready on the laptop, the laptop still in, errors.
  A  everyone gets in: the laptop and every bot joined at each step (within 90 s)
  B  nobody drops: the laptop stays in the whole session; no bot the test didn't remove is lost
  C  the host keeps up: frame p99 under --p99 ms in every minute
  D  no exceptions on either machine; [anomaly] lines counted
  tools/manyplayers.py [--no-build] [--minutes 45] [--bots 14] [--spread 20] [--loss 2] [--churn 180] [--p99 50]"""
import sys, time, math, pathlib, statistics
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, g, clear_road, car_ready

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
MINUTES, BOTS, SPREAD, LOSS, CHURN, P99 = arg("--minutes", 45, float), arg("--bots", 14, int), arg("--spread", 20.0, float), arg("--loss", 2.0, float), arg("--churn", 180, int), arg("--p99", 50.0, float)
RELAY = arg("--relay", "iad")
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def bots_ready(L):
    b = L("mp status").get("bots") or {}
    return b.get("count", 0), b.get("ready", 0)

L = Agent()
ensure_both("--no-build" not in sys.argv)
rows, joins_ok, laptop_lost, lost_bots = [], [], False, 0
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
        if LOSS > 0: print("  laptop loss:", L(f"net lag 0 {LOSS}"), flush=True)
        sid = h("net me")["steamId"]
        # the laptop's player in a car on the road, driving about
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
        R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        h(f"tpground {r0['gx'] - R[0] * 6} {r0['gz'] - R[1] * 6}"); time.sleep(3)
        if car_ready(L, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "laptop"):
            L("set settingsscript.s.S.BAutoShift True"); L("handbrake off"); L("autodrive on 14")
        # bots in steps: near ones, then far ones
        steps = [(BOTS // 4, 0.0), (BOTS // 4, SPREAD), (BOTS - 2 * (BOTS // 4), SPREAD / 2)]
        for n, spread in steps:
            want = bots_ready(L)[0] + n
            print(f"  + {n} bots (spread {spread:g} km):", str(L(f"mp remotebots {n} {sid} 40 6 {spread}"))[:160], flush=True)
            ok = until(lambda: bots_ready(L)[1] >= want, 90, 2)
            joins_ok.append(ok); print(f"    ready {bots_ready(L)} after the step; host players {len(h('mp players')['players'])}", flush=True)
        h("stats"); L("stats")   # start the windows
        t0 = time.time(); next_churn = t0 + CHURN; removed = 0; minute = 0
        while time.time() - t0 < MINUTES * 60:
            time.sleep(60); minute += 1
            fh, fl = h("stats"), L("stats")
            st = L("mp status"); c = st.get("client") or {}; cnt, rdy = bots_ready(L)
            if not c.get("ready"): laptop_lost = True
            if rdy < cnt: lost_bots = max(lost_bots, cnt - rdy)
            n_host = len(h("mp players")["players"])
            rows.append({"min": minute, "hp50": fh["p50ms"], "hp99": fh["p99ms"], "hmax": fh["maxms"], "lp99": fl["p99ms"], "players": n_host + 1, "bots": f"{rdy}/{cnt}"})
            print(f"  min {minute}: host p50/p99/max {fh['p50ms']}/{fh['p99ms']}/{fh['maxms']} ms, laptop p99 {fl['p99ms']}, players {n_host + 1}, bots {rdy}/{cnt}, laptop in {bool(c.get('ready'))}", flush=True)
            if laptop_lost: print("  the laptop dropped:", c.get("reject") or c.get("last"), flush=True); break
            if time.time() > next_churn:
                next_churn += CHURN
                L("mp dropbots 2"); removed += 2
                want = bots_ready(L)[0] + 2
                L(f"mp remotebots 2 {sid} 40 6 {SPREAD if minute % 2 else 0}")
                ok = until(lambda: bots_ready(L)[1] >= want, 90, 2); joins_ok.append(ok)
                print(f"    churn: 2 left, 2 joined; ready {bots_ready(L)}", flush=True)
        L("autodrive off")
        er = [x("errors") for x in (h, L)]
        an = [x("mp anomalies") for x in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get("kinds") or [])[:3]) for e in er], flush=True)
        print("  anomalies:", [str(a)[:300] for a in an], flush=True)
        print("  plausibility (host):", h("mp plausibility"), flush=True)
        verdict("A EVERYONE GETS IN (each step and each churn)", all(joins_ok), f"{sum(joins_ok)}/{len(joins_ok)}")
        if laptop_lost: print("INVALID: the laptop's connection dropped (the user's rule: not counted as a failure)", flush=True)
        verdict("B NO BOT DROPS" + (" (the laptop dropped: INVALID run)" if laptop_lost else ", THE LAPTOP STAYS IN"), lost_bots == 0 and not laptop_lost, f"laptop lost {laptop_lost}, bots not ready at worst {lost_bots}")
        verdict(f"C THE HOST KEEPS UP (frame p99 < {P99:g} ms every minute)", rows and all(r["hp99"] < P99 for r in rows),
                f"worst p99 {max((r['hp99'] for r in rows), default=-1)}, median p50 {statistics.median([r['hp50'] for r in rows]) if rows else -1}")
        verdict("D NO EXCEPTIONS", all(e["exceptions"] == 0 for e in er))
    finally:
        try: L("autodrive off"); L("net lag 0")
        except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"MANYPLAYERS: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")
