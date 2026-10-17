#!/usr/bin/env python3
"""Run a list of tests back to back and print one table: each test's PASS count, how long it took, and how much of
that was setup (restarts, the fresh world, the join) against the test itself.

  tools/suite.py smoke                 a tier from tools/suite.json (smoke: ~5 short tests; full: the pre-release set)
  tools/suite.py hitcheck.py crates.py the tests named (with their own options: 'playround.py --rounds 1')
  tools/suite.py --list                the tiers
  --no-build        no test builds (the last build is deployed)
  --fresh           fresh games for every test. Without it both games are kept between tests that passed (the owner's
                    rule, 2026-10-17; mplib.ensure_both): a test after a FAIL, an error, a failed join or a rebuild gets
                    fresh games. A tier with "reuse": false (full) always restarts.
  --stop-on-fail    stop at the first test that doesn't pass

The suite holds the machine lock (mplib.lock_machines) for its whole run, so no other session's test restarts the games
between two of its tests. Each test's output goes to bench/suite-<time>-<name>/ as well as here."""
import sys, os, re, json, time, shlex, pathlib, subprocess, threading
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import tldenv

TOOLS = pathlib.Path(__file__).resolve().parent
CONF = TOOLS / "suite.json"
VERDICT = re.compile(r"^\s*(PASS|FAIL)\b")


def load_tiers():
    return json.loads(CONF.read_text())


def entries(names, tiers):
    """[(command line, timeout s)] and the tier (or None) for the names given"""
    if len(names) == 1 and names[0] in tiers:
        tier = tiers[names[0]]
        out = [(e, 900) if isinstance(e, str) else (e["test"], e.get("timeout", 900)) for e in tier["tests"]]
        return out, tier
    return [(n, 900) for n in names], None


def count(lines):
    """(passes, fails): verdict lines at the start of a line; a test's indented SUMMARY repeats them, so indented ones
    count only when a test has no others"""
    top = [l for l in lines if VERDICT.match(l) and not l[:1].isspace()]
    got = [VERDICT.match(l).group(1) for l in (top or [l for l in lines if VERDICT.match(l)])]
    return got.count("PASS"), got.count("FAIL")


def mmss(s):
    if s is None: return "-"
    s = int(round(s)); return f"{s // 60}m{s % 60:02d}s" if s >= 60 else f"{s}s"


def table(rows):
    head = ("test", "result", "rc", "time", "setup", "test body", "games kept")
    body = [(r["test"], r["result"], str(r["rc"]), mmss(r["time"]), mmss(r.get("setup")), mmss(r.get("body")), "yes" if r["reuse"] else "")
            for r in rows]
    w = [max(len(x[i]) for x in [head] + body) for i in range(len(head))]
    line = lambda x: "  ".join(c.ljust(w[i]) for i, c in enumerate(x)).rstrip()
    total = sum(r["time"] for r in rows); setup = sum(r.get("setup") or 0 for r in rows)
    return "\n".join([line(head), line(["-" * n for n in w])] + [line(x) for x in body] +
                     [f"{sum(r['ok'] for r in rows)}/{len(rows)} tests passed in {mmss(total)} (setup {mmss(setup)})"])


def run_one(cmd, timeout, env, log):
    args = shlex.split(cmd)
    p = subprocess.Popen([sys.executable, str(TOOLS / args[0])] + args[1:], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                         text=True, bufsize=1, env=env, cwd=str(TOOLS.parent))
    timer = threading.Timer(timeout, p.kill); timer.start()
    lines = []
    with open(log, "w") as f:
        for line in p.stdout:
            sys.stdout.write("  " + line); sys.stdout.flush(); f.write(line); lines.append(line.rstrip("\n"))
    p.wait(); timed_out = not timer.is_alive() and p.returncode != 0; timer.cancel()
    if timed_out: lines.append(f"FAIL  SUITE: killed after {timeout}s")
    return p.returncode, lines


def main():
    args = sys.argv[1:]
    tiers = load_tiers()
    if "--list" in args:
        for k, t in tiers.items():
            names = [e if isinstance(e, str) else e["test"] for e in t["tests"]]
            print(f"{k}: {t.get('about', '')}\n  " + ", ".join(names))
        return
    flags = {a for a in args if a.startswith("--")}
    names = [a for a in args if not a.startswith("--")]
    if not names: sys.exit(__doc__)
    todo, tier = entries(names, tiers)
    reuse = "--fresh" not in flags and (tier is None or tier.get("reuse", True))
    import mplib
    mplib.lock_machines()
    mplib.PHASES.clear()   # the suite's own wait isn't a test's
    label = names[0] if tier is not None else "tests"
    out = tldenv.BENCH / f"suite-{time.strftime('%Y%m%d-%H%M%S')}-{label}"
    out.mkdir(parents=True, exist_ok=True)
    rows, clean = [], False
    for i, (cmd, timeout) in enumerate(todo):
        name = shlex.split(cmd)[0]
        if "--no-build" in flags and "--no-build" not in cmd: cmd += " --no-build"
        timing = out / f"{i + 1:02d}-{name}.timing.json"
        use = reuse and clean   # the first test: fresh games
        env = dict(os.environ, TLD_TIMING_FILE=str(timing))
        if not use: env["TLD_REUSE"] = "0"   # else ensure_both decides (the build, the last test's outcome)
        print(f"\n=== [{i + 1}/{len(todo)}] {cmd}{'  (games kept)' if use else ''}  {time.strftime('%H:%M:%S')}", flush=True)
        t0 = time.time()
        rc, lines = run_one(cmd, timeout, env, out / f"{i + 1:02d}-{name}.log")
        p, f = count(lines)
        try: tm = json.loads(timing.read_text())
        except (OSError, ValueError): tm = {}
        ok = rc == 0 and f == 0 and p > 0
        rows.append({"test": cmd.replace(" --no-build", ""), "result": f"PASS {p}/{p + f}" if p + f else "no verdicts", "rc": rc,
                     "ok": ok, "time": time.time() - t0, "setup": tm.get("setup"), "body": tm.get("test"),
                     "phases": tm.get("phases"), "reuse": bool(tm.get("kept"))})
        print(f"=== {name}: {'OK' if ok else 'NOT OK'} {rows[-1]['result']} rc {rc} in {mmss(rows[-1]['time'])}"
              + (f" — [time] {tm.get('phases')}" if tm else ""), flush=True)
        clean = ok   # a FAIL or an error: the next test gets fresh games (a rebuild restarts in ensure_both itself)
        if not ok and "--stop-on-fail" in flags: break
    summary = table(rows)
    print("\n" + summary)
    (out / "summary.txt").write_text(summary + "\n")
    (out / "summary.json").write_text(json.dumps(rows, indent=1))
    print("logs:", out)
    sys.exit(0 if rows and all(r["ok"] for r in rows) and len(rows) == len(todo) else 1)


if __name__ == "__main__":
    main()
