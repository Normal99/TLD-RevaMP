#!/usr/bin/env python3
"""Release safety + player feedback tooling, in the real game (two machines, relay iad).
  1 HTTP GUARD : a browser-style POST to the bridge is refused before its body runs; the bridge still answers
  2 SESSION LOG: a relay session writes a telemetry line every 5 s on both machines (valid JSON, ping/bandwidth)
  3 REPORT ZIP : `report` on both → one zip each with screenshot, logs, stats, sessions; the session file has its end
  4 BRIDGE OFF : the desktop game started with Bridge = false → nothing listens on 27050, the log says "bridge off"
  tools/releasecheck.py [--no-build]"""
import sys, time, json, socket, zipfile, subprocess, pathlib, re
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, tldenv
from mplib import Agent, ensure_both, seeded_both, REMOTE_HOST
from roadkit import until

GAME = tldenv.GAME_DIR
CFG = GAME / "BepInEx/config/tldrevamp.core.cfg"
FB = GAME / "BepInEx/tldrevamp-feedback"
RFB = tldenv.remote_quote(tldenv.REMOTE_GAME + "/BepInEx/tldrevamp-feedback")
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def rsh(cmd): return subprocess.run(tldenv.ssh_args(REMOTE_HOST) + [cmd], capture_output=True, text=True).stdout

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        b = seeded_both(L)
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")

        # ---- 1 HTTP GUARD
        print("1 HTTP GUARD", flush=True)
        marker = "TLDRevamp.SessionLog.Interval"
        before = b(f"get {marker}")
        body = f"set {marker} 99\n"
        req = ("POST / HTTP/1.1\r\nHost: 127.0.0.1:27050\r\nContent-Type: text/plain\r\nContent-Length: " + str(len(body)) + "\r\n\r\n" + body).encode()
        s = socket.create_connection(("127.0.0.1", 27050), 5); s.sendall(req); s.settimeout(5)
        got = b""
        try:
            while True:
                c = s.recv(4096)
                if not c: break
                got += c
        except socket.timeout: pass
        s.close()
        after = b(f"get {marker}")
        print("  reply to the POST:", got[:200], "| interval before/after:", before, after, flush=True)
        verdict("1 HTTP GUARD: THE POST'S BODY NEVER RAN", after == before and got == b"", f"{before} -> {after}, reply {len(got)} bytes")
        verdict("1 HTTP GUARD: THE BRIDGE STILL ANSWERS", b("ping").get("ok") is True)

        # ---- 2 SESSION LOG
        print("\n2 SESSION LOG (relay session, 30 s)", flush=True)
        b("net relay iad"); L("net relay iad")
        n0 = len(list((FB / "sessions").glob("*.jsonl"))) if (FB / "sessions").exists() else 0
        b("mp host 27070"); L(f"mp joinid {b('net me')['steamId']}")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        time.sleep(30)
        files = sorted((FB / "sessions").glob("*.jsonl"))
        lines = files[-1].read_text().splitlines() if files else []
        recs = []
        for ln in lines:
            try: recs.append(json.loads(ln))
            except Exception as e: print("  bad line:", ln[:200], e)
        ticks = [r for r in recs if "t" in r]
        print(f"  host file {files[-1].name if files else '-'}: {len(lines)} lines, {len(ticks)} ticks, longest {max(map(len, lines), default=0)} chars", flush=True)
        if ticks: print("  last:", lines[-1][:600], flush=True)
        ping = ticks[-1]["net"]["conns"][0]["ping"] if ticks and ticks[-1].get("net", {}).get("conns") else -1
        verdict("2 SESSION LOG: HOST WRITES A LINE EVERY 5 S", len(ticks) >= 5 and len(recs) == len(lines) and recs[0].get("role") == "host",
                f"{len(ticks)} ticks in ~30 s, all valid JSON {len(recs) == len(lines)}, ping {ping}")
        rl = rsh(f'f="$(ls -t {RFB}/sessions/*.jsonl | head -1)"; echo "$f"; wc -l < "$f"; tail -1 "$f"')
        print("  laptop:", rl[:500], flush=True)
        rlines = rl.splitlines()
        verdict("2 SESSION LOG: THE CLIENT WRITES ONE TOO", len(rlines) >= 3 and "client" in rlines[0] and int(rlines[1]) >= 5, rlines[0] if rlines else "")

        # ---- 3 REPORT ZIP
        print("\n3 REPORT ZIP", flush=True)
        for name, s in (("host", b), ("laptop", L)): print(f"  {name}:", s("report releasecheck"), flush=True)
        time.sleep(8)
        z = sorted(FB.glob("TLDRevamp-report-*.zip"))
        names = zipfile.ZipFile(z[-1]).namelist() if z else []
        print("  host zip:", z[-1].name if z else "-", names, flush=True)
        need = {"screenshot.png", "stats.json", "LogOutput.log", "Player.log"}
        st = json.loads(zipfile.ZipFile(z[-1]).read("stats.json")) if "stats.json" in names else {}
        verdict("3 REPORT ZIP: HOST'S HAS EVERYTHING", need <= set(names) and any(n.startswith("sessions/") for n in names)
                and st.get("mod") and isinstance(st.get("net stats"), dict) and st.get("system", {}).get("gpu"),
                f"{len(names)} files, stats keys {list(st)[:14]}")
        rz = rsh(f"z=$(ls -t {RFB}/TLDRevamp-report-*.zip | head -1); echo $z; unzip -l \"$z\" | awk 'NR>3{{print $4}}'")
        print("  laptop zip:", rz.replace("\n", " ")[:600], flush=True)
        verdict("3 REPORT ZIP: LAPTOP'S HAS EVERYTHING", all(n in rz for n in need) and "sessions/" in rz)
        L("mp stop"); b("mp stop"); b("net relay off"); L("net relay off")
        time.sleep(2)
        verdict("3 SESSION LOG: CLOSED WITH ITS END", '"end"' in files[-1].read_text().splitlines()[-1] if files else False)
finally:
    L.close()

# ---- 4 BRIDGE OFF (desktop): the player's default
print("\n4 BRIDGE OFF", flush=True)
game.quit_game()
cfg0 = CFG.read_text()
try:
    CFG.write_text(re.sub(r"(?m)^Bridge = .*$", "Bridge = false", cfg0))
    subprocess.Popen(["steam", "-applaunch", game.APP_ID], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
    log = GAME / "BepInEx/LogOutput.log"
    t0 = time.time(); line = ""
    while time.time() - t0 < 180:
        txt = log.read_text(errors="replace") if log.exists() else ""
        m = re.search(r"TLD Revamp \S+ loaded\..*", txt)
        if m and log.stat().st_mtime > t0: line = m.group(0); break
        time.sleep(2)
    time.sleep(10)
    try: socket.create_connection(("127.0.0.1", 27050), 3).close(); listening = True
    except OSError: listening = False
    print("  log:", line, "| port 27050 listening:", listening, flush=True)
    verdict("4 BRIDGE OFF: NOTHING LISTENS, THE LOG SAYS SO", line.endswith("bridge off") and not listening)
finally:
    subprocess.run(["pkill", "-f", "TheLongDrive.exe"]); time.sleep(6)   # main menu: nothing to save
    CFG.write_text(cfg0)
    print("  config restored:", re.search(r"(?m)^Bridge = .*$", CFG.read_text()).group(0), flush=True)

print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")
