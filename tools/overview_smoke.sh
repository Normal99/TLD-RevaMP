#!/bin/sh
# A smoke test for tools/overview: build it, run the self-test, take a snapshot and
# check the panels are all there. Never launches a game, broker or daemon.
#
#   sh tools/overview_smoke.sh
set -e
cd "$(dirname "$0")/.."
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT HUP INT TERM

say() { printf '%s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*"; exit 1; }

say "== building =="
make -C tools overview >/dev/null
[ -x tools/overview ] || fail "tools/overview was not built"
say "ok   tools/overview built"

say "== self-test =="
tools/overview --self-test > "$tmp/selftest.txt" 2>&1 || fail "the self-test failed"
grep -q "self-test passed" "$tmp/selftest.txt" || fail "the self-test did not report success"
say "ok   the self-test passes ($(grep -c '^  ok' "$tmp/selftest.txt") checks)"

say "== --once =="
tools/overview --once --no-remote > "$tmp/once.txt" 2>&1 || fail "--once failed"
for panel in "AGENTS / WORKSPACES" "TLD / PROTON PROCESSES" "EFFECTIVE BRIDGE STATE" \
             "STEAM HOST / CLIENT / RELAY" "REPORTS / FINDINGS" "GIT" "SYSTEM" \
             "ADMISSION / MEMORY GUARD" "GLOBAL / SESSION MEMORY PRESSURE" \
             "TOP MEMORY CONSUMERS" "STEAM / WIVRN / ADB / OPENXR" \
             "TLD WINDOWS / SPECIAL WORKSPACE"; do
    grep -q "$panel" "$tmp/once.txt" || fail "the panel '$panel' is missing"
done
say "ok   every panel is in the snapshot"

say "== --once --no-remote =="
tools/overview --once --no-remote > "$tmp/once_nr.txt" 2>&1 || fail "--once --no-remote failed"
grep -q "remote probes disabled" "$tmp/once_nr.txt" || fail "--no-remote did not take effect"
say "ok   --no-remote turns the remote probe off"

say "== the help text =="
tools/overview --help > "$tmp/help.txt" 2>&1 || fail "--help failed"
grep -q -- "--no-remote" "$tmp/help.txt" || fail "the help does not mention --no-remote"
grep -q -- "--once" "$tmp/help.txt" || fail "the help does not mention --once"
say "ok   the help text lists the options"

say "== an unknown option is refused =="
if tools/overview --nonsense >/dev/null 2>&1; then fail "an unknown option was accepted"; fi
say "ok   an unknown option is refused"

say "== no terminal =="
if tools/overview >/dev/null 2>&1; then fail "the TUI ran without a terminal"; fi
say "ok   the TUI refuses to run without a terminal"

say "== nothing secret on the screen =="
# the snapshot must not carry an environment dump or a password
if grep -qiE "PASS=|PASSWORD=|SECRET=|API_KEY=|AUTHORIZATION=|BEARER " "$tmp/once.txt"; then
    fail "the snapshot carries something that looks like a credential"
fi
if grep -qE "^[A-Z_]+=" "$tmp/once.txt"; then
    fail "the snapshot carries an environment-style assignment"
fi
say "ok   the snapshot carries no credential"

say "== guard fields =="
for field in "reason:" "profile:" "published exit:" "OOM killed:" "limits:" \
             "global:" "watched session: anon" "memory PSI avg10:" \
             "external OOM events since broker baseline:" "special:tld-tests exposed:" \
             "Steam:" "WiVRn:" "ADB:" "OpenXR:"; do
    grep -Fq "$field" "$tmp/once.txt" || fail "the guard field '$field' is missing"
done
say "ok   guard fields survive unavailable probes"

say "== synthetic Hyprland and read-only helpers =="
mkdir -p "$tmp/bin"
cat > "$tmp/bin/hyprctl" <<'HELPER'
#!/bin/sh
case "$*" in
    '-j clients') printf '%s\n' '[{"class":"TheLongDrive.exe","title":"The Long Drive fixture","workspace":{"name":"special:tld-tests"},"floating":true,"fullscreen":0,"focusHistoryID":0}]' ;;
    '-j monitors') printf '%s\n' '[{"specialWorkspace":{"name":"special:tld-tests"}}]' ;;
    *) exit 77 ;;
esac
HELPER
cat > "$tmp/bin/adb" <<'HELPER'
#!/bin/sh
printf '%s\n' forbidden > "$OV_FORBIDDEN_MARKER"
exit 77
HELPER
cp "$tmp/bin/adb" "$tmp/bin/systemctl"
cp "$tmp/bin/adb" "$tmp/bin/systemd-run"
chmod +x "$tmp/bin/"*
PATH="$tmp/bin:$PATH" HYPRLAND_INSTANCE_SIGNATURE=fixture \
    OV_FORBIDDEN_MARKER="$tmp/forbidden" tools/overview --once --no-remote > "$tmp/hypr.txt" 2>&1
[ ! -e "$tmp/forbidden" ] || fail "overview invoked a state-changing helper"
grep -Fq 'special:tld-tests exposed: yes  TLD windows: 1' "$tmp/hypr.txt" || fail "fixture workspace exposure is missing"
grep -Fq 'The Long Drive fixture' "$tmp/hypr.txt" || fail "fixture window is missing"
say "ok   collector renders fixture windows without invoking ADB or systemd"

# A failed helper must leave exposure unknown and never print its diagnostics.
cat > "$tmp/bin/hyprctl" <<'HELPER'
#!/bin/sh
printf '%s\n' 'PASSWORD=fixture-sensitive-diagnostic'
exit 1
HELPER
PATH="$tmp/bin:$PATH" HYPRLAND_INSTANCE_SIGNATURE=fixture \
    tools/overview --once --no-remote > "$tmp/hypr_failed.txt" 2>&1
grep -Fq 'special:tld-tests exposed: ?' "$tmp/hypr_failed.txt" || fail "failed Hyprland probe claims known exposure"
if grep -Fq 'fixture-sensitive-diagnostic' "$tmp/hypr_failed.txt"; then fail "helper diagnostics leaked"; fi
say "ok   failed helper diagnostics stay private and exposure stays unknown"

say "== bounded helper descendants =="
cat > "$tmp/bin/hyprctl" <<'HELPER'
#!/bin/sh
sleep 10 &
printf '%s\n' "$!" >> "$OV_HELPER_PID_MARKER"
wait
HELPER
PATH="$tmp/bin:$PATH" HYPRLAND_INSTANCE_SIGNATURE=fixture OV_HELPER_PID_MARKER="$tmp/helper-pids" \
    tools/overview --once --no-remote > "$tmp/hypr_timeout.txt" 2>&1
python3 - "$tmp/helper-pids" <<'CHECK'
import os,sys
from pathlib import Path
pids=Path(sys.argv[1]).read_text().split()
assert len(pids)==2, 'both bounded Hyprland helpers must run'
for pid in pids:
    stat=Path('/proc')/pid/'stat'
    if stat.exists():
        assert stat.read_text().rsplit(')',1)[1].split()[0]=='Z', 'timed-out helper left a running descendant'
CHECK
say "ok   timed-out helpers leave no running descendants"

say "== the headset panel =="
tools/overview --once --no-remote > "$tmp/headset.txt" 2>&1
grep -q "HEADSET" "$tmp/headset.txt" || fail "the HEADSET panel is missing"
grep -q "reverse tcp:9757" "$tmp/headset.txt" || fail "the HEADSET panel does not name the reverse mapping"
for field in "battery:" "charging:" "USB:" "awake:" "OpenXR:"; do
    grep -Fq "$field" "$tmp/headset.txt" || fail "the HEADSET field '$field' is missing"
done
say "ok   the HEADSET panel carries its readings"

say "== the headset panel leaks nothing =="
# The device serial must never reach the screen, in any form.
serial=$(tools/overview --once --no-remote 2>/dev/null | grep -oE "[A-Z0-9]{8,16}" | head -20 || true)
if tools/overview --once --no-remote 2>/dev/null | grep -qE "^[[:space:]]*[A-Z0-9]{8,16}[[:space:]]+device"; then
    fail "the snapshot carries a device identifier"
fi
if grep -qiE "PASSWORD=|SECRET=|TOKEN=|API_KEY=|AUTHORIZATION=|BEARER " "$tmp/headset.txt"; then
    fail "the HEADSET panel carries something that looks like a credential"
fi
say "ok   the HEADSET panel carries no identifier and no credential"

say "== the collector never runs adb or systemd =="
# adb, systemctl and systemd-run must never be executed: the overview speaks the
# ADB server protocol itself so it can never start the daemon.
mkdir -p "$tmp/nohelpers"
cat > "$tmp/nohelpers/adb" <<'HELPER'
#!/bin/sh
printf '%s\n' forbidden > "$OV_FORBIDDEN_MARKER"
exit 77
HELPER
cp "$tmp/nohelpers/adb" "$tmp/nohelpers/systemctl"
cp "$tmp/nohelpers/adb" "$tmp/nohelpers/systemd-run"
chmod +x "$tmp/nohelpers/"*
PATH="$tmp/nohelpers:$PATH" OV_FORBIDDEN_MARKER="$tmp/forbidden-helpers" \
    tools/overview --once --no-remote > "$tmp/nohelpers.txt" 2>&1
[ ! -e "$tmp/forbidden-helpers" ] || fail "overview invoked adb, systemctl or systemd-run"
say "ok   the overview never runs adb, systemctl or systemd-run"

say "== the headset self-test is hermetic =="
# The headset self-test must answer from its fixtures: no socket, no adb, no
# systemd.  A helper that would be contacted must never be reached.
[ -x tools/test_overview_headset ] || make -C tools test_overview_headset >/dev/null
PATH="$tmp/nohelpers:$PATH" OV_FORBIDDEN_MARKER="$tmp/forbidden-selftest" \
    tools/test_overview_headset > "$tmp/headset_selftest.txt" 2>&1 || fail "the headset self-test failed"
[ ! -e "$tmp/forbidden-selftest" ] || fail "the headset self-test contacted a helper"
grep -q "headset self-test passed" "$tmp/headset_selftest.txt" || fail "the headset self-test did not report success"
say "ok   the headset self-test passes without touching adb or systemd"

say "== the read-deadline cases =="
# The timing cases open a loopback socket against a mock server, so they are a
# separate binary from the hermetic self-test.
[ -x tools/test_overview_headset_timing ] || make -C tools test_overview_headset_timing >/dev/null
timeout 60 tools/test_overview_headset_timing > "$tmp/headset_timing.txt" 2>&1 || fail "the read-deadline cases failed"
grep -q "headset timing self-test passed" "$tmp/headset_timing.txt" || fail "the read-deadline cases did not report success"
say "ok   a reply that arrives late is read, and a silent peer stops at the deadline"

say "== the tool does not write anywhere =="
# the other workers write into the results directory all the time, so the check
# uses its own directory: the overview must not create a thing in it.
mkdir -p "$tmp/workers/logs" "$tmp/workers/results"
tools/overview --once --no-remote --workers "$tmp/workers" >/dev/null 2>&1
# the two directories are the test's own; anything else is a file the tool wrote
left=$(find "$tmp/workers" -mindepth 1 -not -name logs -not -name results | wc -l)
[ "$left" -eq 0 ] || { find "$tmp/workers" -mindepth 1; fail "the tool wrote into the worker directory"; }
say "ok   the tool writes nothing into the worker directories"

say ""
say "smoke test passed"
