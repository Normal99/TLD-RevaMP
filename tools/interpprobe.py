#!/usr/bin/env python3
"""Why copies run ~350 ms behind (twocars 2026-10-08: interp delay 337-352 ms, jitter ~250 ms; 2026-09-30: ~100 ms).
One player drives its car ~12 s (within 150 m: every state, 20 Hz); the other machine's interpolator for that car is read
every 2 s: latency deviations over the least delayed state (ms), send gaps (ms), delay. Then the other way round.
  tools/interpprobe.py [--no-build] [--relay iad|off] [--direct: join by LAN IP] [--bot: a loopback bot in the host] [--laptop-bot: the laptop alone hosts a loopback bot]
  [--keepawake-ab: the second run with the laptop's Wi-Fi kept busy by a 20 ms ping] [--secs 12]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, tldenv
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
SECS = int(sys.argv[sys.argv.index("--secs") + 1]) if "--secs" in sys.argv else 12

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def sit(x, net):
    for _ in range(6):
        r = x(f"mp sitdriver {net}")
        if "seat" in r and not r.get("stepped"): return r
        time.sleep(1.5)
    return r

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off" and "--direct" not in sys.argv: h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    if "--laptop-bot" in sys.argv:
        # the laptop alone: it hosts, one bot joins it over loopback inside its own game (no other machine)
        L("mp host 27070"); time.sleep(3)
        rp = L("roadpoint"); L(f"tpground {rp['gx']} {rp['gz']}"); time.sleep(3)
        print("template car:", L(f"spawncarg 1 0 {rp['gx'] + 6} {rp['gz']} 0"), flush=True); time.sleep(3)
        L(f"tpground {rp['gx'] + 8} {rp['gz'] + 1}"); time.sleep(2)
        print("bot car:", L("mp botcar"), L("clearpile"), flush=True)
        L("set TLDRevamp.Net.Mp.BotCars True")
        before = {e["net"] for e in L("mp entities")["entities"]}
        print("bots:", L("mp bots 1 25 8").get("bots"), flush=True)
        until(lambda: any(e["car"] and e["net"] not in before for e in L("mp entities")["entities"]), 30)
        net = next(e["net"] for e in L("mp entities")["entities"] if e["car"] and e["net"] not in before)
        time.sleep(4)
        for k in range(SECS // 2):
            time.sleep(2); d = L(f"mp interp {net}")
            print(f"  t{2*k+2:>2}s bot car on the laptop (loopback): delay {d.get('delayMs')} jitter {d.get('jitterMs')} | gaps {d.get('gapsMs')} | lat dev {d.get('latDevMs')}", flush=True)
        L("mp stop"); sys.exit(0)
    h("mp host 27070")
    if "--direct" in sys.argv: L(f"mp join {DESKTOP_IP}:27070")   # straight over the LAN: no Steam relay in the path
    else: L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    print("route:", L("net route"), flush=True)
    gods = {}
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, s_("get kaposztaleves.s.settings.god").get("value")); s_("set kaposztaleves.s.settings.god True")
        rp = h("roadpoint"); h(f"tpground {rp['gx']} {rp['gz']}"); L(f"tpground {rp['gx'] + 8} {rp['gz']}"); time.sleep(3)
        if "--bot" in sys.argv:
            # one bot client inside the host's game, over loopback: its car's copy on the host (no network between)
            # and on the laptop (host -> laptop over the real connection)
            print("template car:", h(f"spawncarg 1 0 {rp['gx'] + 6} {rp['gz']} 0"), flush=True); time.sleep(3)
            h(f"tpground {rp['gx'] + 8} {rp['gz'] + 1}"); time.sleep(2)
            print("bot car:", h("mp botcar"), h("clearpile"), flush=True)
            h("set TLDRevamp.Net.Mp.BotCars True")
            before = {e["net"] for e in h("mp entities")["entities"]}
            print("bots:", h("mp bots 1 25 8").get("bots"), flush=True)
            until(lambda: any(e["car"] and e["net"] not in before for e in h("mp entities")["entities"]), 30)
            net = next(e["net"] for e in h("mp entities")["entities"] if e["car"] and e["net"] not in before)
            time.sleep(4)
            for k in range(SECS // 2):
                time.sleep(2)
                for nm, s_ in (("host (loopback)", h), ("laptop (via host)", L)):
                    d = s_(f"mp interp {net}")
                    print(f"  t{2*k+2:>2}s bot car on the {nm}: delay {d.get('delayMs')} jitter {d.get('jitterMs')} | gaps {d.get('gapsMs')} | lat dev {d.get('latDevMs')}", flush=True)
        runs = [] if "--bot" in sys.argv else [(L, h, "laptop"), (h, L, "host")]
        if "--keepawake-ab" in sys.argv: runs = [(L, h, "laptop"), (L, h, "laptop")]
        pinger = None
        for ri, (driver, viewer, dn) in enumerate(runs):
            if "--keepawake-ab" in sys.argv and ri == 1:
                # the laptop's Wi-Fi kept busy: a ping to the desktop every 20 ms for this run (changes no setting)
                import subprocess
                pinger = subprocess.Popen(tldenv.ssh_args() + [
                                           f"timeout {SECS + 30} ping -q -i 0.02 {DESKTOP_IP}"], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
                print("=== the laptop's Wi-Fi kept awake (ping every 20 ms)", flush=True)
            elif "--keepawake-ab" in sys.argv: print("=== nothing extra", flush=True)
            before = {e["net"] for e in h("mp entities")["entities"]}
            print(f"{dn}'s car:", driver("devspawn Car01Full"), flush=True)
            if not until(lambda: any(e["car"] and e["net"] not in before for e in h("mp entities")["entities"]), 20): sys.exit("NO CAR")
            net = next(e["net"] for e in h("mp entities")["entities"] if e["car"] and e["net"] not in before)
            time.sleep(3); print("  sits:", sit(driver, net), flush=True); time.sleep(2)
            driver("fuelup") if True else None
            driver("autodrive on 8")
            for k in range(SECS // 2):
                time.sleep(2)
                d = viewer(f"mp interp {net}")
                print(f"  t{2*k+2:>2}s {dn} drives, the other machine's copy: delay {d.get('delayMs')} jitter {d.get('jitterMs')} interval {d.get('intervalMs')}"
                      f" | gaps {d.get('gapsMs')} | lat dev {d.get('latDevMs')}", flush=True)
            driver("autodrive on 0"); time.sleep(3); driver("autodrive off")
            if pinger is not None:
                pinger.terminate()
                try: print("  pinger:", (pinger.communicate(timeout=10)[0] or "").strip()[-200:], flush=True)
                except Exception: pinger.kill()
                pinger = None
            try: driver("driveout")
            except RuntimeError: pass
            time.sleep(2)
            for nm, s_ in (("host", h), ("laptop", L)): print(f"  {nm} frame times: {s_('stats').get('frame', s_('stats'))}", flush=True)
    finally:
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
