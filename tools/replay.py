#!/usr/bin/env python3
"""Replay check for issue #36 (record gameplay, play it back with the filming camera).

  tools/replay.py check      on the leased hidden test game that is already in a world
  tools/replay.py run        launch the test copy (TLD_DIR), start a new world, check, quit

What `check` proves, through the mod's own bridge commands:

  * recording: a car is driven for about 20 s while recording; the recording holds the player at 20 samples a
    second, objects with keys, and is saved to a file that lists;
  * playback from memory: every recorded object gets a copy, the game's own renderers are hidden, the tiny-part
    culler is paused, the player's controls are blocked, the recorded player is where the player really was at
    the same moment (1.5 m plus 0.4 s at the car's speed, at the start, the middle and the end), and the clock
    runs at the chosen speed;
  * each camera view (follow, eyes, orbit, free) opens, with a screenshot of each; nothing sits on the eyes;
  * the player's own character did not move while the replay played;
  * leaving gives everything back: no renderer left hidden, the culler running again, the camera closed;
  * playback from the saved file (as a later session would) finds the same objects live and shows the same path;
  * no new errors or exceptions in the game's log.

Every step prints PASS or FAIL; the exit code is 1 if anything failed.
"""
import math
import pathlib
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

FAILS = []


def check(ok, what, detail=""):
    print(("PASS " if ok else "FAIL ") + what + (f" ({detail})" if detail else ""), flush=True)
    if not ok:
        FAILS.append(what)
    return ok


def gpos(b):
    g = b("gpos")
    return float(g["gx"]), float(g["gy"]), float(g["gz"])


def dist(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def shot(b, label):
    try:
        r = b("screenshot")
        print(f"     screenshot ({label}): {r.get('path')}")
    except Exception as e:  # a missing screenshot is evidence lost, not a replay failure
        print(f"     screenshot ({label}) failed: {e}")


def setup_drive(b):
    b("set TLDRevamp.TestSafety.BlockAutoSave True")
    b("set kaposztaleves.s.settings.god True")
    rp = b("roadpoint")
    yaw = rp["yaw"] + 180.0
    fx, fz = math.sin(math.radians(yaw)), math.cos(math.radians(yaw))
    cx, cz = rp["x"] + fx * 8, rp["z"] + fz * 8
    g = b(f"ground {cx} {cz}")
    b(f"spawnpile 223 1 {cx} {g['y']} {cz} 1 1 1.0 4242"); time.sleep(3)
    b(f"carface {cx} {g['y']} {cz} {yaw}"); time.sleep(1)
    b(f"drivein {cx} {g['y']} {cz}"); time.sleep(2)
    b(f"drivein {cx} {g['y']} {cz}"); time.sleep(3)
    b("fuelup")


def run_check(b):
    b("errors reset")
    setup_drive(b)

    # ---- record
    r = b("replay record")
    check(r.get("recording") is True, "recording starts", str(r))
    path = []   # (recording time, global position) of the real player
    t0 = time.time()
    b("autodrive on 20")
    while time.time() - t0 < 20:
        st = b("replay status")["recorder"]
        path.append((float(st["seconds"]), gpos(b)))
        if b("autodrive").get("stuckSeconds", 0) > 6:
            b("nudge 12")
        time.sleep(1.0)
    b("autodrive off"); b("brakecar"); time.sleep(2)
    st = b("replay status")["recorder"]
    path.append((float(st["seconds"]), gpos(b)))
    stopped = b("replay stop")
    for _ in range(40):
        stopped = b("replay status")["recorder"]
        if not stopped["saving"]:
            break
        time.sleep(0.25)
    seconds = float(stopped["seconds"])
    moved = dist(path[0][1], path[-1][1])
    print(f"     recorded {seconds:.1f} s, {stopped['objects']} objects, {stopped['objectKeys']} object keys, "
          f"{stopped['pieceKeys']} piece keys, {stopped['playerSamples']} player samples, {stopped['approxMB']} MB, "
          f"{stopped['selfMs']} ms per tick (max {stopped['maxMs']} at {stopped.get('maxAt')} s); the car moved {moved:.0f} m")
    check(not stopped["recording"] and not stopped["saving"] and not stopped["failed"], "recording stops and saves", str(stopped))
    check(moved > 20, "the player really drove during the recording", f"{moved:.1f} m")
    check(stopped["objects"] > 5 and stopped["objectKeys"] > stopped["objects"], "objects and their moves are recorded")
    check(abs(stopped["playerSamples"] - seconds * 20) < seconds * 2 + 5, "the player is sampled 20 times a second",
          f"{stopped['playerSamples']} for {seconds:.1f} s")
    check(stopped["selfMs"] < 2.0, "recording costs under 2 ms per sample on average", f"{stopped['selfMs']} ms")
    name = stopped["lastFile"]
    listed = [x["name"] for x in b("replay list")["replays"]]
    check(name in listed, "the recording is saved and listed", name)

    # ---- playback from memory
    v = playback(b, "replay play", path, seconds, from_disk=False)
    # ---- playback from the file
    if name in listed:
        playback(b, f"replay play {name} disk", path, seconds, from_disk=True)

    errs = b("errors")   # counted since `errors reset` at the start of the check
    print(f"     game log since the check started: {errs.get('errors')} errors, {errs.get('exceptions')} exceptions; {errs.get('kinds')}"[:600])
    check(errs.get("exceptions", 0) == 0, "no exceptions in the game's log during the check")
    return v


def playback(b, command, path, seconds, from_disk):
    tag = "file" if from_disk else "memory"
    before = gpos(b)
    v = b(command)
    check(v.get("active") is True, f"[{tag}] playback starts", str(v)[:300])
    if not v.get("active"):
        return v
    check(v["copies"] > 5 and v["copiesShown"] > 0, f"[{tag}] recorded objects are drawn as copies", f"{v['copies']} copies, {v['copiesShown']} shown")
    check(v["hidden"] > 0 and v["stillHidden"] == v["hidden"], f"[{tag}] the game's own renderers are hidden", f"{v['hidden']}")
    check(v["cullPaused"], f"[{tag}] the tiny-part culler is paused")
    check(v["bodiesShown"] >= 1, f"[{tag}] the recorded player's body is drawn")
    if from_disk:
        check(v["live"] > 0 and v["notFound"] == 0, f"[{tag}] the file's objects are found in the world", f"live {v['live']}, from item {v['fromItem']}, missing {v['notFound']}")
    cin = b("cinema")
    check(cin.get("active") is True, f"[{tag}] the filming camera is on")

    # the recorded player is where the real player was, at three moments
    # The real position was read one bridge call after the recording's clock, so a moving car is a little further on:
    # the tolerance is 1.5 m plus 0.4 s of the car's speed there.
    for frac in (0.0, 0.5, 1.0):
        i = min(range(len(path)), key=lambda k: abs(path[k][0] - frac * seconds))
        want_t, want_pos = path[i]
        j = i + 1 if i + 1 < len(path) else i - 1
        speed = dist(path[i][1], path[j][1]) / max(0.1, abs(path[j][0] - path[i][0])) if j >= 0 else 0.0
        tolerance = 1.5 + 0.4 * speed
        b("replay pause")
        s = b(f"replay seek {want_t}")
        got = tuple(float(x) for x in s.get("subjectAt", (0, 0, 0)))
        d = dist(got, want_pos)
        check("subjectAt" in s and d < tolerance, f"[{tag}] the recorded player at {want_t:.1f} s is where the player was",
              f"{d:.2f} m off, {speed:.1f} m/s, tolerance {tolerance:.1f} m")

    # the clock: 2x for ~3 s of wall time
    b("replay seek 0"); b("replay speed 2"); b("replay resume")
    t0 = time.time(); time.sleep(3.0)
    s = b("replay status")["viewer"]
    elapsed = time.time() - t0
    check(abs(float(s["time"]) - 2 * elapsed) < 1.0 or float(s["time"]) >= seconds - 0.01, f"[{tag}] 2x speed plays twice as fast",
          f"{s['time']} s of replay in {elapsed:.1f} s")
    check(s["inputBlocked"] > 0, f"[{tag}] the player's controls are blocked while it plays", str(s["inputBlocked"]))
    b("replay speed 1"); b(f"replay seek {seconds * 0.5}")

    for view in ("follow", "eyes", "orbit", "free"):
        s = b(f"replay camera {view}")
        check(s.get("camera") == view, f"[{tag}] {view} view")
        time.sleep(1.0)
        if view == "eyes":
            near = b("replay near 3")
            print(f"     copied pieces within 3 m of the eyes ({near['count']}), "
                  f"{b('replay status')['viewer'].get('atEyes')} small copies left out at the eyes:")
            for n in near["near"][:8]:
                print(f"       {n}")
            # what the player wore on the head sat right on the eyes and filled the view
            close = [n for n in near["near"] if n["d"] < 0.1]
            check(not close, f"[{tag}] nothing worn on the head fills the eyes view", str(close[:2]))
        if not from_disk:
            shot(b, view)
    s = b("replay status")["viewer"]
    check(abs(float(s["playerMoved"])) < 0.05, f"[{tag}] the player's own character did not move", f"{s['playerMoved']} m")

    e = b("replay end")
    check(e["active"] is False and e["hidden"] == 0 and e["stillHidden"] == 0 and e.get("darkLights", 0) == 0,
          f"[{tag}] leaving gives every renderer and light back", str(e))
    check(not e["cullPaused"], f"[{tag}] the tiny-part culler runs again")
    check(b("cinema").get("active") is False, f"[{tag}] the filming camera closes with the replay")
    after = gpos(b)
    check(dist(before, after) < 0.1, f"[{tag}] the player is where it was before playback", f"{dist(before, after):.3f} m")
    return v


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "check"
    import game
    from bench import Bridge
    if cmd == "check":
        run_check(Bridge())
    elif cmd == "run":
        with game.ProtectedSession():
            game.launch()
            game.newgame(20260924)
            b = Bridge()
            try:
                run_check(b)
            finally:
                try:
                    b("replay end")
                except Exception:
                    pass
                game.quit_game()
    else:
        print(__doc__)
        return 2
    print(("FAILED: " + "; ".join(FAILS)) if FAILS else "ALL PASS")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
