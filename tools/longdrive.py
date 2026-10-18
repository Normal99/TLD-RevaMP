#!/usr/bin/env python3
"""Hitch hunt: a long scripted drive along the main road through new terrain (fresh seeded world), every slow frame
collected with what it was doing.

  tools/longdrive.py [--minutes 10] [--speed 25] [--hitch-ms 25] [--autosave] [--set Path=Value] [--profile T.M,T.M] [label]

The car (drivebench's Car01Full, the mod's scripted driver DriveLab) drives west on the main road for --minutes.
Collected:
  - the frame-phase profiler's hitch log (every frame over --hitch-ms: wall, loop, GC, the 3 costliest phases)
  - the play log's `hitch` records (docs/TELEMETRY.md: frames over 50 ms with phases, gc, rebase / saving / loading /
    stream flags, generation backlog and what generation finished that frame) and its 5 s `tick` records (frame
    percentiles, memory: managedMB, unityAllocMB, unityReservedMB)
  - distance, world rebases, autosaves (blocked unless --autosave: the test world's autosaves go to the test prefix's
    slots and are put back afterwards, like every tool)
  - with --profile: the script profiler on those methods (calls, total, worst call)
Summary: hitches grouped by their costliest phase and by flag, the worst ones in full, memory at the start and the end.
Result: bench/<time>-<label>.json.
"""
import json, sys, time, pathlib, datetime, statistics as st
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import bench, game, seeded, drivebench, tldenv
from bench import Bridge

ROOT = pathlib.Path(__file__).resolve().parent.parent
SESSIONS = tldenv.GAME_DIR / "BepInEx/tldrevamp-feedback/sessions"


def newest_play_log(after):
    files = sorted(SESSIONS.glob("*.jsonl"), key=lambda p: p.stat().st_mtime) if SESSIONS.exists() else []
    files = [f for f in files if f.stat().st_mtime >= after]
    return files[-1] if files else None


def read_play_log(path, st0, st1):
    hitches, ticks, saves = [], [], []
    if path is None: return hitches, ticks, saves
    for line in path.read_text(errors="replace").splitlines():
        try: d = json.loads(line)
        except ValueError: continue
        s = d.get("st", 0)
        if not (st0 <= s <= st1): continue
        if d.get("k") == "hitch": hitches.append(d)
        elif d.get("k") == "tick": ticks.append(d)
        elif d.get("k") == "save": saves.append(d)
    return hitches, ticks, saves


def main():
    minutes, speed, hitch_ms, autosave, sets, rest, prof = 10.0, 25.0, 25.0, False, [], [], None
    it = iter(sys.argv[1:])
    for a in it:
        if a == "--minutes": minutes = float(next(it))
        elif a == "--speed": speed = float(next(it))
        elif a == "--hitch-ms": hitch_ms = float(next(it))
        elif a == "--autosave": autosave = True
        elif a == "--set": sets.append(tuple(next(it).split("=", 1)))
        elif a == "--profile": prof = next(it).split(",")
        else: rest.append(a)
    label = rest[0] if rest else "longdrive"
    with game.ProtectedSession():
        if not game.bridge_up(): game.launch()
        seeded.set_now(sets)
        t_open = time.time()
        seeded.restart_same_seed()
        b = Bridge(); b("bg on")
        restore = bench.apply_changes(b, [("UnityEngine.QualitySettings.vSyncCount", "0"), ("settingsscript.s.S.BEnableFpsLimit", "False"),
                                          (bench.GOD, "True"), (bench.SURVIVAL, "1"),
                                          ("TLDRevamp.TestSafety.BlockAutoSave", "False" if autosave else "True"),
                                          ("TLDRevamp.PlayerLoopProfiler.HitchMs", str(hitch_ms)), *sets])
        result = {"label": label, "time": datetime.datetime.now().isoformat(timespec="seconds"), "version": b("ping")["version"],
                  "minutes": minutes, "speed": speed, "hitchMs": hitch_ms, "autosave": autosave, "sets": dict(sets)}
        try:
            drivebench.sit_in_car(b)
            b(f"autodrive on {speed}")
            time.sleep(10)   # up to speed
            rb0 = int(b("get mainscript.s.visszarakvaCount")["value"])
            st0 = b("stats").get("time", 0)
            play_st0 = None
            b("reset"); b("alloc reset"); b("loopprof start")
            if prof: b("profile start " + " ".join(prof))
            samples, mem = [], []
            t_end = time.time() + minutes * 60
            stopped_since = None
            while time.time() < t_end:
                time.sleep(2)
                s = b("autodrive")
                samples.append({"t": round(time.time() - (t_end - minutes * 60), 1), "v": s["speed"], "pos": s["pos"], "dist": s["distance"]})
                if len(samples) % 15 == 1:
                    x = b("stats"); mem.append({"t": samples[-1]["t"], "managedMB": x.get("managedMB"),
                                                "farItems": b("get savedatascript.s.data.itemData.items.Count")["value"],
                                                "liveItems": b("get savedatascript.s.items.Count")["value"]})
                if s["speed"] < 2.0:
                    stopped_since = stopped_since or time.time()
                    if time.time() - stopped_since > 30: result["endedEarly"] = f"stopped 30 s at {s['pos']} ({s['lastObstacle']})"; break
                else: stopped_since = None
            stats = b("stats"); lp = b("loopprof stop 25"); stats["alloc"] = b("alloc")
            if prof: result["profile"] = b("profile stop 40")
            stats["rebases"] = int(b("get mainscript.s.visszarakvaCount")["value"]) - rb0
            drv = b("autodrive off")
        finally:
            try: b("set TLDRevamp.TestSafety.BlockAutoSave True")
            except Exception as e: print("!!", e)
            bench.restore_settings(restore)
        log = newest_play_log(t_open)
        hitches, ticks, saves = read_play_log(log, 0, 1e18)
        # the play log's clock is the game's realtime: keep the records of the measured window (by count of frames since
        # the drive started: ticks after the first 10 s of driving)
        result.update({"stats": stats, "phases": lp["phases"], "hitches": lp["hitches"], "drive": {k: v for k, v in drv.items() if k != "wheels"},
                       "samples": samples, "mem": mem, "playLog": str(log.name) if log else None,
                       "playHitches": hitches, "playTicks": ticks, "playSaves": saves})
    out = ROOT / "bench" / f"{datetime.datetime.now():%Y%m%d-%H%M%S}-{label}.json"
    out.write_text(json.dumps(result, indent=1))
    show(result)
    print("saved", out.relative_to(ROOT))


def show(r):
    s = r["stats"]; d = r["drive"]
    print(f"{r['label']}: {d.get('distance')} m in {r['minutes']} min (mean {st.mean(x['v'] for x in r['samples']):.1f} m/s), "
          f"rebases {s['rebases']}, recoveries {d.get('recoveries')}" + (f", ENDED EARLY: {r['endedEarly']}" if r.get("endedEarly") else ""))
    print(f"  fps {s['fps']} p50 {s['p50ms']} p99 {s['p99ms']} p99.9 {s['p999ms']} max {s['maxms']} >50ms {s['hitches50ms']} gc {s['gc0']}")
    hs = r["hitches"]
    print(f"  frames over {r['hitchMs']} ms: {len(hs)}")
    by = {}
    for h in hs:
        k = h["top"][0][0] if h["top"] else "?"
        g = by.setdefault(k, [0, 0.0, 0.0]); g[0] += 1; g[1] += h["wall"]; g[2] = max(g[2], h["wall"])
    for k, (n, tot, mx) in sorted(by.items(), key=lambda kv: -kv[1][1]):
        print(f"    {k[:52]:52} {n:4}  worst {mx:6.1f} ms")
    gcn = sum(1 for h in hs if h["gc"]); print(f"    with a GC that frame: {gcn}")
    print("  worst 12:")
    for h in sorted(hs, key=lambda h: -h["wall"])[:12]:
        print(f"    {h['wall']:6.1f} ms (loop {h['loop']}, gc {h['gc']}) " + ", ".join(f"{p} {ms}" for p, ms in h["top"]))
    ph = r["playHitches"]
    if ph:
        flags = {}
        for h in ph:
            for f in ("rebase", "saving", "loading", "stream"):
                if h.get(f): flags[f] = flags.get(f, 0) + 1
        print(f"  play log hitches (>50 ms): {len(ph)}, flags {flags}")
        for h in sorted(ph, key=lambda h: -h.get("ms", 0))[:8]:
            print(f"    {h.get('ms')} ms gc {h.get('gc')} genDone {h.get('genDone')} gen {h.get('gen')} phases " +
                  json.dumps(dict(sorted((h.get('phases') or {}).items(), key=lambda kv: -kv[1])[:4])))
    tk = r["playTicks"]
    if tk:
        print(f"  memory (play log ticks): managed {tk[0].get('managedMB')} → {tk[-1].get('managedMB')} MB, unity reserved "
              f"{tk[0].get('unityReservedMB')} → {tk[-1].get('unityReservedMB')} MB over {len(tk)} ticks")
    if r.get("mem"): print("  far/live items:", [(m["t"], m.get("farItems"), m.get("liveItems")) for m in r["mem"]])
    if r.get("profile"):
        print("  profile:")
        for m in (r["profile"].get("byTime") or [])[:15]: print("    ", json.dumps(m))
    if r["playSaves"]: print("  saves:", [(x.get("ms"), x.get("auto")) for x in r["playSaves"]])


if __name__ == "__main__":
    main()
