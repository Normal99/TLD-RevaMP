#!/usr/bin/env python3
"""The Head Unit example mod with two players (desktop hosts, laptop joins over the relay): a head unit in the host's
Car01, a video queue started by the host; the laptop, standing at the car, plays the same video at the same moment;
the laptop presses next and back, the host follows; a song from a Spotify album added by the laptop; each player's
own video volume, the sound playing on both. Under Steam's Proton (no H.264) the laptop converts the videos to VP8
(Ytdlp.ToVp8): --fresh-convert turns its conversion off first, so it has to find that out again.
  TLD_TEST_MODS=CarTrailer,HeadUnit tools/headunit2.py [--no-build] [--relay iad|off] [--fresh-convert] [--allow-nocodecs]"""
import os
os.environ.setdefault("TLD_TEST_MODS", "CarTrailer,HeadUnit")
import sys, time, math, pathlib, re
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, clear_road, CAR01

def arg(name, default): return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default
RELAY = arg("--relay", "iad")
VIDS = os.environ.get("HU_VIDEOS", "jNQXAC9IVRw,dQw4w9WgXcQ").split(",")
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def call(side, what):
    try: return str(side("call HeadUnit.Probe." + what)["value"])
    except Exception as e: return "ERROR " + str(e)[:300]

NOCODECS = "can't decode YouTube's video"
ALLOW_NOCODECS = "--allow-nocodecs" in sys.argv
def parse(st):
    """index, count, playing, position, ready+playing from Probe.Video"""
    m = re.search(r"index (-?\d+) of (\d+), (playing|paused) at ([\d.]+); video (ready|not ready) ?(PLAYING)?", st)
    if not m: return None
    return {"index": int(m.group(1)), "count": int(m.group(2)), "playing": m.group(3) == "playing",
            "pos": float(m.group(4)), "shown": m.group(5) == "ready" and m.group(6) == "PLAYING", "nocodecs": NOCODECS in st}

def both(h, L):
    a, b = call(h, "Video"), call(L, "Video")
    return a, b, parse(a), parse(b)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    for s_ in (h, L): s_("set kaposztaleves.s.settings.god True"); s_("set HeadUnit.Video.Trace True")
    for kv in filter(None, os.environ.get("HU_SET_LAPTOP", "").split(";")): print(" laptop set", kv, L("set " + kv))
    if "--fresh-convert" in sys.argv: print(" laptop converts:", call(L, "SetConvert False"), flush=True)
    rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
    r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
    R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
    cx, cz = r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8
    print(" car:", h(f"spawncarg {CAR01} 0 {cx} {cz} {yaw}"), flush=True); time.sleep(3)
    h(f"tpground {cx + R[0] * 3} {cz + R[1] * 3}"); time.sleep(2)
    print(" unit:", h("devspawn 9100"), flush=True); time.sleep(3)
    print(" mount:", call(h, "Mount"), flush=True); time.sleep(2)
    print(" share:", h("mp share"), flush=True); time.sleep(3)
    L(f"tpground {cx + R[0] * 3 + F[0] * 1.5} {cz + R[1] * 3 + F[1] * 1.5}"); time.sleep(5)
    print(" laptop sees:", call(L, "List"), flush=True)
    t = call(L, "Tools"); print(" laptop tools:", t, flush=True)
    if "yt-dlp False" in t or "ffmpeg False" in t:
        print(" laptop fetches the tools:", call(L, "GetTools"), flush=True)
        until(lambda: "yt-dlp True" in call(L, "Tools") and "downloading False" in call(L, "Tools"), 300)
        print(" laptop tools:", call(L, "Tools"), flush=True)
    for side in (h, L): call(side, "Press video")
    call(h, "Press pwr")
    for v in VIDS: print(" host adds:", call(h, f"Add {v}"), flush=True)
    t0 = time.time()
    while time.time() - t0 < 240:
        a, b, pa, pb = both(h, L)
        if pa and pb and pa["shown"] and (pb["shown"] or (ALLOW_NOCODECS and pb["nocodecs"])): break
        time.sleep(3)
    print(" host:", a, "\n laptop:", b, flush=True)
    verdict("A. THE LAPTOP SEES THE QUEUE", pb is not None and pa is not None and pb["count"] == pa["count"] == len(VIDS), f"{pa} / {pb}")
    if pb and pb["nocodecs"]: print(" THE LAPTOP'S PROTON CAN'T DECODE VIDEO: it shows the message, the rest follows the shared queue", flush=True)
    def shows(p): return bool(p and (p["shown"] or (ALLOW_NOCODECS and p["nocodecs"])))
    print(" laptop:", call(L, "Sound"), "|", call(L, "Player"), flush=True)
    verdict("B. THE VIDEO PLAYS ON BOTH", bool(pa and pa["shown"] and shows(pb)), f"{pa} / {pb}")
    i0 = pa["index"] if pa else 0
    if "--players" in sys.argv:
        for _ in range(6): print(" players: host", call(h, "Player"), "| laptop", call(L, "Player"), flush=True); time.sleep(2)
    time.sleep(5); a, b, pa, pb = both(h, L)
    verdict("C. AT THE SAME MOMENT (shared position within 0.5 s)", bool(pa and pb and abs(pa["pos"] - pb["pos"]) < 0.5), f"{pa} / {pb}")
    snd = [call(h, "Sound"), call(L, "Sound")]
    for _ in range(4):
        if all("peak 0.000" not in x for x in snd): break
        time.sleep(0.5); snd = [call(h, "Sound"), call(L, "Sound")]
    peak = [float(re.search(r"peak ([\d.]+)", x).group(1)) if "peak" in x else 0.0 for x in snd]
    verdict("C2. SOUND PLAYING ON BOTH", peak[0] > 0.001 and peak[1] > 0.001, f"host {snd[0]} / laptop {snd[1]}")
    vol = lambda x: re.search(r"volume ([\d.]+)", x).group(1) if "volume" in x else "?"
    v0 = [vol(x) for x in snd]
    for _ in range(2): call(h, "Press vol+")
    call(L, "Press vol-"); time.sleep(1)
    v1 = [vol(call(h, "Sound")), vol(call(L, "Sound"))]
    verdict("C3. EACH PLAYER'S OWN VOLUME (host up 2, laptop down 1)", v0[0] != "?" and abs(float(v1[0]) - min(1.0, float(v0[0]) + 0.2)) < 0.01 and abs(float(v1[1]) - max(0.0, float(v0[1]) - 0.1)) < 0.01, f"host {v0[0]} -> {v1[0]}, laptop {v0[1]} -> {v1[1]}")
    for _ in range(2): call(h, "Press vol-")
    call(L, "Press vol+")
    print(" laptop presses next:", call(L, "Press next"), flush=True)
    ok = until(lambda: (parse(call(h, "Video")) or {}).get("index") == i0 + 1 and shows(parse(call(L, "Video"))), 120)
    a, b, pa, pb = both(h, L)
    verdict("D. THE LAPTOP'S NEXT: BOTH ON THE NEXT VIDEO", bool(ok and pa and pb and pa["index"] == pb["index"] == i0 + 1 and pa["shown"] and shows(pb)), f"{a} / {b}")
    time.sleep(1)
    # within its first 5 s PREV goes back; later it starts this one again (a second press then goes back)
    print(" laptop presses prev:", call(L, "Press prev"), flush=True)
    if (parse(call(h, "Video")) or {}).get("index") != i0: time.sleep(0.5); print(" and again:", call(L, "Press prev"), flush=True)
    ok = until(lambda: (parse(call(h, "Video")) or {}).get("index") == i0 and shows(parse(call(L, "Video"))), 60)
    a, b, pa, pb = both(h, L)
    verdict("E. AND BACK: BOTH ON THE EARLIER ONE AGAIN", bool(ok and pa and pb and pa["index"] == pb["index"] == i0 and pa["shown"] and shows(pb)), f"{a} / {b}")
    print(" host pauses:", call(h, "Press play"), flush=True); time.sleep(3)
    a, b, pa, pb = both(h, L)
    verdict("F. THE HOST'S PAUSE: PAUSED ON BOTH", bool(pa and pb and not pa["playing"] and not pb["playing"]), f"{a} / {b}")
    call(h, "Press play"); time.sleep(2)
    n0 = (parse(call(h, "Video")) or {}).get("count", 0)
    print(" laptop adds a Spotify album:", call(L, "Link https://open.spotify.com/album/4m2880jivSbbyEGAKfITCa"), flush=True)
    until(lambda: (parse(call(h, "Video")) or {}).get("count", 0) >= n0 + 13 and (parse(call(L, "Video")) or {}).get("count", 0) >= n0 + 13, 120)
    a, b, pa, pb = both(h, L)
    verdict("G. THE LAPTOP'S SPOTIFY SONGS IN THE SHARED QUEUE", bool(pa and pb and pa["count"] >= n0 + 10 and pb["count"] == pa["count"]), f"{n0} -> {pa and pa['count']} / {pb and pb['count']}")
    n0 = (parse(call(h, "Video")) or {}).get("count", 0)
    print(" laptop searches:", call(L, "Search rick_astley_never_gonna_give_you_up"), flush=True)
    until(lambda: not call(L, "SearchResults").startswith("searching"), 60)
    print(" laptop results:", call(L, "SearchResults")[:160], flush=True)
    print(" laptop taps the first:", call(L, "Press r0")[:80], flush=True); call(L, "Press done")
    until(lambda: (parse(call(h, "Video")) or {}).get("count", 0) == n0 + 1, 30)
    a, b, pa, pb = both(h, L)
    verdict("G2. THE LAPTOP'S SEARCH RESULT IN THE SHARED QUEUE", bool(pa and pb and pa["count"] == pb["count"] == n0 + 1), f"{n0} -> {pa and pa['count']} / {pb and pb['count']}")
    verdict("H. BOTH GAMES STILL RUNNING", game.running() and "ERROR" not in call(L, "Video"), "")
    er = [x_("errors") for x_ in (h, L)]
    print("  errors host/laptop:", er[0].get("count"), er[1].get("count"), flush=True)
    for s_ in (h, L):
        try: s_("mp stop")
        except Exception: pass
L.close()
print(f"\nSUMMARY headunit2: {sum(1 for _, ok in VERDICTS if ok)}/{len(VERDICTS)} PASS")
