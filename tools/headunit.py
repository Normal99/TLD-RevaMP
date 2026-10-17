#!/usr/bin/env python3
"""The Head Unit example mod (src/Mods/HeadUnit) in single player, on this machine: a Car01 and a head unit spawned,
the unit put into the car's radio slot as a player would, its screen's place and buttons, presses on them along a
ray as the crosshair does, and pictures of the screen (TheLongDrive_Data/headunit_*.png).
  TLD_TEST_MODS=CarTrailer,HeadUnit tools/headunit.py [--no-build]"""
import os
os.environ.setdefault("TLD_TEST_MODS", "CarTrailer,HeadUnit")
import sys, time, pathlib, math
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
from roadkit import clear_road, CAR01

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def call(b, what):
    try: return str(b("call HeadUnit.Probe." + what)["value"])
    except Exception as e: return "ERROR " + str(e)[:300]

with game.ProtectedSession():
    if "--no-build" not in sys.argv: game.build()
    else: game.local_mods()
    game.quit_game(); game.launch(); game.newgame()
    b = Bridge()
    time.sleep(3)
    for kv in filter(None, os.environ.get("HU_SET", "").split(";")):   # A/B: "HeadUnit.Video.MakeGapS 0;..."
        print(" set", kv, b("set " + kv))
    # the car on the road outside, the player beside it (the start house hides the screen from the pictures)
    rp = b("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
    r0 = clear_road(b, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
    R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
    cx, cz = r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8
    print(" spawn car:", b(f"spawncarg {CAR01} 0 {cx} {cz} {yaw}")); time.sleep(3)
    b(f"tpground {cx + R[0] * 3} {cz + R[1] * 3}"); time.sleep(2)
    print(" spawn unit:", b("devspawn 9100")); time.sleep(3)

    print(" units:", call(b, "List"))
    m = call(b, "Mount"); print(" mount:", m)
    verdict("mounts in the radio slot", "mounted" in m, m)
    time.sleep(2)
    print(" units:", call(b, "List"))
    w = call(b, "Where"); print(" where:", w)
    try: facing = float(w.split("faces the seat ")[1].split(" ")[0])
    except Exception: facing = -9
    verdict("screen faces the driver", facing > 0.3, w)
    verdict("radio found", "radio MISSING" not in w, w)
    print(" shot:", call(b, "Shot radio"))

    def radio(): return call(b, "Where").split("; radio ")[-1].split(";")[0]
    for bid in ("pwr", "vol+", "seek+"):
        before = call(b, "Video") + call(b, "Where")
        r = call(b, f"Press {bid}"); time.sleep(0.8)
        after = call(b, "Where")
        print(f" press {bid}:", r, "|", after.split("; buttons")[0])
        verdict(f"button {bid} works", r.startswith("pressed"), r)
    r = call(b, "Press video"); time.sleep(1.0)
    verdict("YOUTUBE tab opens", "mode Video" in call(b, "Video") or "tab Video" in call(b, "Video"), call(b, "Video"))
    print(" shot:", call(b, "Shot video"))

    # a video: added, downloaded, playing; then the tab switch that crashed the game (2026-10-15), next, back
    VIDS = os.environ.get("HU_VIDEOS", "jNQXAC9IVRw,dQw4w9WgXcQ").split(",")
    for v in VIDS: print(" add:", call(b, f"Add {v}"))
    t0 = time.time(); st = ""
    while time.time() - t0 < 240:
        st = call(b, "Video")
        if "PLAYING" in st or "problem can't" in st or "yt-dlp:" in st: break
        time.sleep(3)
    print(" video:", st)
    verdict("first video downloads and plays", "PLAYING" in st, st)
    snd = [call(b, "Sound") for _ in range(5) if not time.sleep(0.5)]
    print(" sound:", snd, "|", call(b, "Player"))
    verdict("its sound plays", any(float(x.split("peak ")[1].split()[0]) > 0.001 for x in snd if "peak " in x), snd[-1])
    print(" shot:", call(b, "Shot playing"))
    call(b, "Press nav"); time.sleep(3)
    st = call(b, "Video"); print(" on NAV:", st)
    verdict("video keeps playing on NAV", "PLAYING" in st, st)
    print(" shot:", call(b, "Shot nav"))
    mp = call(b, "MapPng nav"); print(" map:", mp)
    verdict("NAV: the main road in its own colour", mp.startswith("main road") and int(mp.split()[2]) > 50, mp)
    for _ in range(3): call(b, "Press zoom-")
    time.sleep(1); print(" shot:", call(b, "Shot navfar")); print(" map far:", call(b, "MapPng far"))
    for _ in range(3): call(b, "Press zoom+")
    call(b, "Press video"); time.sleep(2)
    r = call(b, "Press play"); time.sleep(2)   # pause
    r2 = call(b, "Press play"); time.sleep(3)  # play again: the crash
    st = call(b, "Video"); print(" pause/play:", r, "|", r2, "|", st)
    verdict("pause and play again (no crash)", "PLAYING" in st and game.running(), st)
    r = call(b, "Press next"); print(" next:", r)
    t0 = time.time()
    while time.time() - t0 < 240:
        st = call(b, "Video")
        if ("index 1" in st and "PLAYING" in st) or "ERROR" in st: break
        time.sleep(3)
    verdict("next video plays (no crash)", "index 1" in st and "PLAYING" in st and game.running(), st)
    r = call(b, "Press prev"); time.sleep(1); call(b, "Press prev"); time.sleep(6)
    st = call(b, "Video"); print(" prev:", st)
    verdict("back to the first (cached) video", "index 0" in st and "PLAYING" in st and game.running(), st)
    r = call(b, "Press full"); time.sleep(1.5); print(" full:", r)
    print(" shot:", call(b, "Shot full"))
    call(b, "Press unfull")
    # a Spotify album: its songs found on YouTube Music and queued as they come (Spotify.cs)
    n0 = int(call(b, "Video").split(" of ")[1].split(",")[0]) if " of " in call(b, "Video") else 0
    print(" spotify:", call(b, "Link https://open.spotify.com/album/4m2880jivSbbyEGAKfITCa"))
    t0 = time.time(); n = n0; first = None
    while time.time() - t0 < 120:
        st = call(b, "Video")
        try: n = int(st.split(" of ")[1].split(",")[0])
        except Exception: pass
        if n > n0 and first is None: first = time.time() - t0
        if n >= n0 + 13: break
        time.sleep(1)
    print(f" spotify: {n - n0} songs added, the first after {first if first is not None else -1:.0f} s, all after {time.time() - t0:.0f} s; tools: {call(b, 'Tools')}")
    verdict("Spotify album: songs queued", n - n0 >= 10, f"{n - n0} of 13, first after {first} s")
    verdict("Spotify album: the first within 20 s", first is not None and first < 20, f"{first}")

    # + ADD with words: YouTube's results listed, one tapped into the queue; a link in the same box still adds directly
    def count():
        try: return int(call(b, "Video").split(" of ")[1].split(",")[0])
        except Exception: return -1
    call(b, "Press video"); n0 = count(); t0 = time.time()
    print(" search:", call(b, "Search rick_astley_never_gonna_give_you_up"))
    res = ""
    while time.time() - t0 < 60:
        res = call(b, "SearchResults")
        if not res.startswith("searching"): break
        time.sleep(1)
    print(f" results after {time.time() - t0:.1f} s:", res[:400])
    verdict("search: YouTube's videos listed", "dQw4w9WgXcQ" in res, res[:200])
    time.sleep(1); print(" shot:", call(b, "Shot search"))
    print(" screen text:", call(b, "ScreenText"))
    print(" font lacks:", call(b, "Missing |\"çğıöşüÇĞİÖŞÜéñ€—–’“”&#!?[]{}<>"))
    r = call(b, "Press r0"); time.sleep(2); n1 = count()
    print(" tap the first:", r[:120], "| queue", n0, "->", n1, "|", call(b, "SearchResults")[:120])
    verdict("search: a tapped result is queued", n1 == n0 + 1, f"{n0} -> {n1}")
    print(" shot:", call(b, "Shot searchadded"))
    call(b, "Press done")
    print(" link in the same box:", call(b, "Search https://www.youtube.com/watch?v=jNQXAC9IVRw"))
    until_t = time.time()
    while time.time() - until_t < 30 and count() < n1 + 1: time.sleep(1)
    verdict("a link in the box still adds directly", count() == n1 + 1, f"{n1} -> {count()}")

    # stuck anywhere: a second unit stuck on the dashboard beside the radio, as the glue key does (Glue.cs)
    print(" spawn second unit:", b("devspawn 9101")); time.sleep(3)
    print(" aim:", call(b, "AimBeside 0.35 0.05")); time.sleep(0.5)
    st = call(b, "Stick"); print(" stick:", st)
    try: back = float(st.split("screen back ")[1].split(" ")[0]); faces = float(st.split("faces the player ")[1].split(" ")[0])
    except Exception: back, faces = 9, -9
    verdict("stuck where looked", "stuck True" in st and "car none" not in st, st)
    verdict("stuck: the radio body hidden and passable", "body shown False" in st and "solid colliders 0" in st, st)
    verdict("stuck: screen flush on the spot, facing the player", back < 0.03 and faces > 0.7, f"back {back} m, faces {faces}")
    time.sleep(1); print(" shot:", call(b, "Shot stuck"))
    if "--keep" not in sys.argv: game.quit_game()

print(f"{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed")
