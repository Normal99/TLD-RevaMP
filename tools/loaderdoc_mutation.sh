#!/bin/sh
# Mutation check for the loader documentation test (tools/unit/LoaderLifecycleDocTest.cs).
#
# A documentation test that cannot fail is worthless.  This script copies the tree's
# documentation and loader code into a scratch directory, breaks ONE claim at a
# time, and proves that the doc test notices every one of them.  The tree itself is
# never modified.
#
#   sh tools/loaderdoc_mutation.sh
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# ---- POSIX self-check -------------------------------------------------------
# This script is #!/bin/sh and CI runs it with dash on Ubuntu. The local /bin/sh is usually bash,
# so a local `sh -n` CANNOT see a bash-only construct (a process substitution is a syntax error under
# dash and passes silently here). This check looks for the constructs dash rejects, so the failure is
# found before CI runs it.
posix_selfcheck() {
    # The script is scanned for the constructs dash rejects. The patterns are written with a
    # character class so the scan does not have to quote a bare '(' or '>' in a shell context.
    bad=""
    # The scan excludes its own lines (they are marked), so the patterns do not match themselves.
    scan() { grep -v 'POSIX-SCAN' "$0" 2>/dev/null; }   # POSIX-SCAN
    # process substitution
    if scan | grep -qE '[>][(]|[<][(]'; then bad="$bad process-substitution"; fi   # POSIX-SCAN
    # the two-bracket conditional
    if scan | grep -qF '[['; then bad="$bad double-bracket"; fi                      # POSIX-SCAN
    # the function keyword
    if scan | grep -qE 'function[[:space:]]+[A-Za-z_]+[[:space:]]*[(]'; then bad="$bad function-keyword"; fi   # POSIX-SCAN
    # `local -x`
    if scan | grep -qE '[[:space:]]local[[:space:]]+-[a-zA-Z]'; then bad="$bad local-option"; fi                   # POSIX-SCAN
    # here-strings
    if scan | grep -qF "$(printf '\074\074\074')"; then bad="$bad here-string"; fi                               # POSIX-SCAN
    # ANSI-C quoting
    if scan | grep -qF "\$'" ; then bad="$bad ansi-c-quoting"; fi                  # POSIX-SCAN
    if [ -n "$bad" ]; then
        echo "HARNESS BROKE: tools/loaderdoc_mutation.sh uses bash-only construct(s):$bad"
        echo "It runs under #!/bin/sh (dash on Ubuntu CI). Remove them."
        exit 1
    fi
}
posix_selfcheck

WORK="${XDG_STATE_HOME:-$HOME/.local/state}/tld-revamp/loaderdoc"
mkdir -p "$WORK"
WORK="$(mktemp -d "${TMPDIR:-$WORK}/loaderdoc-mut.XXXXXX")"
echo "scratch copy: $WORK"

# the files the doc test reads
copy_tree() {
    mkdir -p "$WORK/src/TLDRevamp" "$WORK/src/TLDRevamp/Net" "$WORK/src/TLDLoaderShim" "$WORK/docs"
    cp "$ROOT/src/TLDRevamp/ModHost.cs" "$ROOT/src/TLDRevamp/Plugin.cs" "$ROOT/src/TLDRevamp/LoaderScan.cs" \
       "$ROOT/src/TLDRevamp/Overlay.cs" "$ROOT/src/TLDRevamp/FeedbackReporter.cs" "$WORK/src/TLDRevamp/"
    cp "$ROOT/src/TLDRevamp/Net/Voice.cs" "$ROOT/src/TLDRevamp/Net/ModCatalog.cs" "$ROOT/src/TLDRevamp/Net/ModActivation.cs" \
       "$ROOT/src/TLDRevamp/Net/JoinLoading.cs" "$ROOT/src/TLDRevamp/Net/JoinLoadingView.cs" "$ROOT/src/TLDRevamp/Net/JoinLoadingState.cs" "$ROOT/src/TLDRevamp/Net/JoinChecks.cs" "$ROOT/src/TLDRevamp/Net/MpScreen.cs" \
       "$WORK/src/TLDRevamp/Net/"
    cp "$ROOT/src/TLDLoaderShim/TLDLoader.cs" "$WORK/src/TLDLoaderShim/"
    cp "$ROOT/docs/MODDING-MULTIPLAYER.md" "$ROOT/docs/NEXUS.md" "$ROOT/docs/CONTRIBUTING.md" "$WORK/docs/"
    cp "$ROOT/README.md" "$WORK/"
    cp "$ROOT/packaging/mods.json" "$ROOT/packaging/approved-mods.txt" "$ROOT/packaging/INSTALL.txt" "$WORK/packaging/" 2>/dev/null || {
        mkdir -p "$WORK/packaging"; cp "$ROOT/packaging/mods.json" "$ROOT/packaging/approved-mods.txt" "$ROOT/packaging/INSTALL.txt" "$WORK/packaging/"; }
    mkdir -p "$WORK/installer" "$WORK/tools" "$WORK/.github/workflows" "$WORK/tools/modscan" "$WORK/src/Mods/HeadUnit" "$WORK/src/TLDRevamp.Preloader"
    # LoaderScanDocTest reads the preloader project file at this exact path.
    cp "$ROOT/src/TLDRevamp.Preloader/TLDRevamp.Preloader.csproj" "$WORK/src/TLDRevamp.Preloader/" 2>/dev/null || true
    # ReleaseDocTest reads the package audit script at this exact path.
    cp "$ROOT/tools/package_audit.py" "$WORK/tools/" 2>/dev/null || true
    # ReleaseDocTest reads the CI workflow, so the scratch tree must carry it at its real path
    cp "$ROOT/.github/workflows/checks.yml" "$WORK/.github/workflows/" 2>/dev/null || true
    # and the files the other baseline projects read
    cp "$ROOT/src/Mods/HeadUnit/Plugin.cs" "$WORK/src/Mods/HeadUnit/" 2>/dev/null || true
    cp "$ROOT/tools/modscan/Program.cs" "$WORK/tools/modscan/" 2>/dev/null || true
    cp "$ROOT/tools/installer-test.sh" "$WORK/tools/" 2>/dev/null || true
    cp "$ROOT/tools/loaderdoc_mutation.sh" "$WORK/tools/" 2>/dev/null || true
    cp "$ROOT/installer/install.sh" "$ROOT/installer/install.ps1" "$WORK/installer/" 2>/dev/null || true
    mkdir -p "$WORK/packaging/licenses"
    cp "$ROOT/packaging/licenses/"*.txt "$WORK/packaging/licenses/" 2>/dev/null || true
    cp "$ROOT/tools/package.sh" "$ROOT/tools/libs.sh" "$ROOT/tools/pin-installer.sh" "$WORK/tools/" 2>/dev/null || true
    cp "$ROOT/COPYING" "$WORK/" 2>/dev/null || true
}

# The scratch tree must be complete before a mutation is scored: a missing file makes the test
# CRASH, and a crash is not a catch. This check turns that into a clear harness error.
require_scratch_files() {
    for f in README.md packaging/INSTALL.txt packaging/mods.json packaging/approved-mods.txt \
             src/TLDRevamp/Overlay.cs src/TLDRevamp/LoaderScan.cs src/TLDRevamp/FeedbackReporter.cs \
             src/TLDRevamp/Plugin.cs src/TLDRevamp/ModHost.cs \
             src/TLDRevamp/Net/Voice.cs src/TLDRevamp/Net/ModCatalog.cs src/TLDRevamp/Net/ModActivation.cs \
             src/TLDRevamp/Net/JoinLoading.cs src/TLDRevamp/Net/JoinLoadingView.cs src/TLDRevamp/Net/JoinLoadingState.cs src/TLDRevamp/Net/JoinChecks.cs src/TLDRevamp/Net/MpScreen.cs \
             src/TLDLoaderShim/TLDLoader.cs docs/MODDING-MULTIPLAYER.md docs/NEXUS.md docs/CONTRIBUTING.md \
             .github/workflows/checks.yml src/Mods/HeadUnit/Plugin.cs tools/modscan/Program.cs \
             tools/installer-test.sh tools/libs.sh tools/package.sh tools/pin-installer.sh tools/package_audit.py \
             src/TLDRevamp.Preloader/TLDRevamp.Preloader.csproj \
             installer/install.sh installer/install.ps1; do
        if [ ! -f "$WORK/$f" ]; then
            echo "HARNESS BROKE: the scratch tree is missing $f (the test would crash, and a crash is not a catch)"
            exit 1
        fi
    done
}

# The build output goes to an ABSOLUTE path OUTSIDE the repository. A relative "obj/LLDTM/" lands
# in tools/unit/obj and tools/unit/bin, and UnitIsolationTest correctly refuses an output tree that
# belongs to no unit project. A failed harness run must not be able to contaminate the repository.
OUTDIR=$(mktemp -d "${TMPDIR:-/tmp}/loaderdoc-mut-build.XXXXXX")
cleanup() { rm -rf "$OUTDIR"; }
trap cleanup EXIT INT TERM

# The harness's OWN output is kept: a failed run's evidence must survive the terminal.
#
# POSIX ONLY. This script is #!/bin/sh and CI runs it with dash on Ubuntu, where a bash process
# substitution is a SYNTAX ERROR. The local `sh` is bash, so a local `sh -n` cannot see that. Each
# captured output is therefore written to the run log with printf, and the CALLER captures the whole
# transcript.
RUNLOG="$(dirname "$WORK")/$(basename "$WORK").log"
: > "$RUNLOG"
echo "the run log is $RUNLOG"

# log <text> : append a line to the run log and to the terminal.
log() {
    printf '%s\n' "$1"
    printf '%s\n' "$1" >> "$RUNLOG"
}

run_test() {
    dotnet run --project tools/unit/LoaderLifecycleDocTest.csproj --configuration Release \
        --disable-build-servers --property:BaseIntermediateOutputPath="$OUTDIR/obj/LLDTM/" \
        --property:OutputPath="$OUTDIR/bin/LLDTM/" -- "$WORK" 2>&1
}

failures=0

# A mutation is CAUGHT only when the test reports a real FAIL line AND did not crash.
#
# The order matters: a CRASH is checked FIRST. A test that fails one assertion and THEN crashes (a
# file missing from the scratch copy) still prints a FAIL line, and scoring that as a catch hides a
# broken harness behind a green result. The real exit code is retained: a mutation is caught when the
# test exited non-zero AND reported an assertion failure, and it is an ERROR when the test could not
# run at all.
verdict() {
    # verdict <what> <output> <exit-code>
    #
    # Everything this function prints is also appended to the run log, so the evidence of a failed
    # run survives even when the terminal is lost.
    #
    # The three outcomes are kept apart:
    #   ERROR      the test could not run (a crash, a signal, a build failure). rc >= 128 means the
    #              process died on a signal; a FAIL line printed before the crash does NOT make it a
    #              catch. The crash is checked FIRST, and it wins over any earlier FAIL line.
    #   caught     the test ran and reported an assertion failure: rc is EXACTLY 1 and a FAIL line is
    #              present. The loader/doc tests exit 1 on an assertion failure; anything else is a
    #              different kind of exit and is not a catch.
    #   NOT CAUGHT the test passed (rc 0), or failed in a way that is not an assertion failure.
    what="$1"; out="$2"; rc="$3"
    # Preserve the complete captured test output before evaluating the verdict.
    printf '%s\n' "$out" >> "$RUNLOG"
    if echo "$out" | grep -qE "Unhandled exception|FileNotFoundException|DirectoryNotFoundException|Traceback|error CS|Build FAILED"; then
        log "ERROR  $what  (the test could not run: a crash or a build failure, exit $rc)"
        echo "$out" | grep -E "Unhandled exception|FileNotFoundException|DirectoryNotFoundException|error CS|Build FAILED" | head -2 | sed 's/^/            /' | tee -a "$RUNLOG" || true
        failures=$((failures + 1))
        return 1
    fi
    if [ "$rc" -ge 128 ]; then
        log "ERROR  $what  (the test died on a signal (exit $rc); a crash is not a catch)"
        echo "$out" | tail -3 | sed 's/^/            /' | tee -a "$RUNLOG" || true
        failures=$((failures + 1))
        return 1
    fi
    if [ "$rc" -eq 0 ]; then
        log "NOT CAUGHT  $what  (the test still passed: exit 0)"
        failures=$((failures + 1))
        return 1
    fi
    if [ "$rc" -ne 1 ]; then
        log "NOT CAUGHT  $what  (the test exited $rc, which is not the assertion-failure exit 1)"
        failures=$((failures + 1))
        return 1
    fi
    if echo "$out" | grep -q "^FAIL"; then
        log "caught      $what  (exit 1, assertion failure)"
        echo "$out" | grep "^FAIL" | head -3 | sed 's/^/            /' | tee -a "$RUNLOG" || true
        return 0
    fi
    log "NOT CAUGHT  $what  (the test exited 1 but reported no assertion failure)"
    failures=$((failures + 1))
    return 1
}

mutate() {
    # mutate <what> <python file> ; the python edits the scratch copy
    copy_tree
    require_scratch_files
    python3 "$2" "$WORK"
    rc=0
    out=$(run_test) || rc=$?
    verdict "$1" "$out" "$rc"
}

# ---- the mutations -------------------------------------------------------
cat > "$WORK/m1.py" <<'PY'
import sys
work = sys.argv[1]
p = work + "/src/TLDRevamp/ModHost.cs"
s = open(p).read()
old = 'if (wasUp && !worldUp) { _unloadCalled = true; foreach (var l in Mods) Call(l, l.OnUnload, "OnUnload"); }'
assert old in s, "anchor missing"
open(p, "w").write(s.replace(old, 'if (wasUp && !worldUp) { _unloadCalled = true; }'))
PY

cat > "$WORK/m2.py" <<'PY'
import sys
work = sys.argv[1]
# the documentation claims the host calls OnUnload, but the code does not
p = work + "/src/TLDRevamp/ModHost.cs"
s = open(p).read()
old = 'if (wasUp && !worldUp) { _unloadCalled = true; foreach (var l in Mods) Call(l, l.OnUnload, "OnUnload"); }'
assert old in s, "anchor missing"
open(p, "w").write(s.replace(old, 'if (wasUp && !worldUp) { _unloadCalled = true; }'))
d = work + "/docs/MODDING-MULTIPLAYER.md"
t = open(d).read()
anchor = "config folder)."
assert anchor in t, "doc anchor missing"
open(d, "w").write(t.replace(anchor, anchor + "\nThe host calls `OnUnload` when the world goes away.", 1))
PY

cat > "$WORK/m3.py" <<'PY'
import sys
work = sys.argv[1]
# the documentation claims OnLoad happens as soon as the scene is the game,
# but the code waits for the player to exist
d = work + "/docs/MODDING-MULTIPLAYER.md"
t = open(d).read()
anchor = "config folder)."
assert anchor in t, "doc anchor missing"
open(d, "w").write(t.replace(anchor, anchor + "\n`OnLoad` runs as soon as the game scene is active.", 1))
p = work + "/src/TLDRevamp/ModHost.cs"
s = open(p).read()
old = "mainscript.s != null && mainscript.s.player != null"
assert old in s, "anchor missing"
open(p, "w").write(s.replace(old, "true"))
PY

cat > "$WORK/m4.py" <<'PY'
import sys
work = sys.argv[1]
# the shim loses a lifecycle method the host calls
p = work + "/src/TLDLoaderShim/TLDLoader.cs"
s = open(p).read()
old = "public virtual void OnUnload() { }"
assert old in s, "anchor missing"
open(p, "w").write(s.replace(old, "public virtual void OnUnloadX() { }"))
PY

cat > "$WORK/m5.py" <<'PY'
import sys
work = sys.argv[1]
# the docs promise the config folder, but the host stops setting the shim's root
p = work + "/src/TLDRevamp/ModHost.cs"
s = open(p).read()
old = 'GetField("ConfigRoot").SetValue(null, Path.Combine(BepInEx.Paths.ConfigPath, "TLDLoaderMods"))'
assert old in s, "anchor missing"
open(p, "w").write(s.replace(old, 'GetField("ConfigRoot").SetValue(null, "")'))
PY

# The BASELINE for EVERY test project the mutations use. A project whose baseline does not pass is
# not a valid judge: it would "catch" every mutation by crashing, and its verdict would mean nothing.
# Every project is run against the UNTOUCHED scratch tree first, so a missing file or a build error
# is found here, before any mutation is scored.
BASELINE_PROJECTS="LoaderLifecycleDocTest LoaderScanDocTest NexusDocTest ReadmeCommandsTest ReleaseDocTest"

echo "== the baselines on the untouched copy =="
copy_tree
require_scratch_files
baseline_failed=0
for proj in $BASELINE_PROJECTS; do
    rc=0
    out=$(dotnet run --project "tools/unit/$proj.csproj" --configuration Release \
        --disable-build-servers --property:BaseIntermediateOutputPath="$OUTDIR/obj/$proj/" \
        --property:OutputPath="$OUTDIR/bin/$proj/" -- "$WORK" 2>&1) || rc=$?
    # Preserve the complete baseline output in the persistent run log before scoring it.
    printf '%s\n' "$out" >> "$RUNLOG"
    last=$(echo "$out" | tail -1 || true)
    if [ "$rc" -ne 0 ] || echo "$out" | grep -qE "Unhandled exception|FileNotFoundException|error CS|Build FAILED"; then
        log "ERROR  the baseline of $proj does not pass (exit $rc): $last"
        echo "$out" | grep -E "Unhandled exception|FileNotFoundException|error CS" | head -2 | sed 's/^/            /' | tee -a "$RUNLOG" || true
        baseline_failed=1
    elif ! echo "$out" | grep -q "ALL PASS"; then
        log "ERROR  the baseline of $proj did not report ALL PASS (exit $rc): $last"
        baseline_failed=1
    else
        log "ok   $proj passes on the untouched tree ($last)"
    fi
done
if [ "$baseline_failed" -ne 0 ]; then
    echo "a baseline does not pass: the mutations would be judged by a broken test. Stop here."
    exit 1
fi
echo

cat > "$WORK/m8.py" <<'PY'
import sys
work = sys.argv[1]
# the host keys the world on the scene name again: a scene change inside a world
# would then unload the mod
p = work + "/src/TLDRevamp/ModHost.cs"
s = open(p).read()
old = "bool worldUp = mainscript.s != null && mainscript.s.player != null;"
assert old in s, "anchor missing"
new = "bool worldUp = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == menuhandler.sceneName;"
open(p, "w").write(s.replace(old, new))
PY

cat > "$WORK/m9.py" <<'PY'
import sys
work = sys.argv[1]
# the host calls Config() on every IMGUI pass again
p = work + "/src/TLDRevamp/ModHost.cs"
s = open(p).read()
old = "            if (!ConfigScreenOpen) return;"
assert old in s, "anchor missing"
open(p, "w").write(s.replace(old, ""))
PY

cat > "$WORK/m10.py" <<'PY'
import sys
work = sys.argv[1]
# the game quitting no longer unloads the mods
p = work + "/src/TLDRevamp/Plugin.cs"
s = open(p).read()
old = "            ModHost.Shutdown();   // the world ends here: the Loader mods get their OnUnload"
assert old in s, "anchor missing"
open(p, "w").write(s.replace(old, ""))
PY

cat > "$WORK/m11.py" <<'PY'
import sys
work = sys.argv[1]
# the README's troubleshooting names an installer option that does not exist
p = work + "/README.md"
s = open(p).read()
anchor = "--game-dir"
assert anchor in s, "anchor missing"
open(p, "w").write(s.replace(anchor, "--game-folder"))
PY

cat > "$WORK/m12.py" <<'PY'
import sys
work = sys.argv[1]
# the README claims the overlay is off during a loading screen, which it is not
p = work + "/README.md"
s = open(p).read()
# this tree's README words the Page Up key in a table row; the CLAIM is the same
anchor = "**Page Up** shows and hides the overlay with the frame graph."
assert anchor in s, "anchor missing"
open(p, "w").write(s.replace(anchor, "**Page Up** shows and hides the overlay with the frame graph. The overlay is off during a loading screen."))
PY

cat > "$WORK/m13.py" <<'PY'
import sys
work = sys.argv[1]
# the README's first-session walkthrough claims the version is in the top-left
# overlay, but the overlay no longer takes the left column of the debug layout
p = work + "/src/TLDRevamp/Overlay.cs"
s = open(p).read()
old = "float width = DebugLayout.LeftWidth(Screen.width, Screen.height, MarkPanel.Opened);"
assert old in s, "anchor missing"
open(p, "w").write(s.replace(old, "float width = 300f;"))
PY

cat > "$WORK/m14.py" <<'PY'
import sys
work = sys.argv[1]
# the scanner stops reporting a section the documentation still promises
p = work + "/src/TLDRevamp/LoaderScan.cs"
s = open(p).read()
old = '[Kind.DrawOnly] = ("Draws on screen", "Works as is: each player sees their own."),'
assert old in s, "anchor missing"
open(p, "w").write(s.replace(old, ""))
PY

cat > "$WORK/m15.py" <<'PY'
import sys
work = sys.argv[1]
# the documentation promises a section the scanner never writes
p = work + "/docs/MODDING-MULTIPLAYER.md"
s = open(p).read()
old = "- what only draws on screen (works as is)."
assert old in s, "anchor missing"
open(p, "w").write(s.replace(old, "- what only draws on screen (works as is);\n- the mod's network traffic;"))
PY

cat > "$WORK/m16.py" <<'PY'
import sys
work = sys.argv[1]
# the default catalog URL no longer points at the dev branch's manifest
p = work + "/src/TLDRevamp/Net/ModCatalog.cs"
s = open(p).read()
old = "refs/heads/dev/packaging/mods.json"
assert old in s, "anchor missing"
open(p, "w").write(s.replace(old, "refs/heads/main/packaging/mods.json"))
PY

cat > "$WORK/m17.py" <<'PY'
import sys
work = sys.argv[1]
# the browser no longer protects BepInEx core from being toggled
p = work + "/src/TLDRevamp/Net/ModActivation.cs"
s = open(p).read()
old = 'id.StartsWith("bepinex", StringComparison.OrdinalIgnoreCase)'
assert old in s, "anchor missing"
open(p, "w").write(s.replace(old, "false"))
PY

cat > "$WORK/m18.py" <<'PY'
import sys
work = sys.argv[1]
# the scanner stops counting the member sites it cannot resolve
p = work + "/src/TLDRevamp/LoaderScan.cs"
s = open(p).read()
old = "catch { rep.UnresolvedMembers++; }"
assert old in s, "anchor missing"
open(p, "w").write(s.replace(old, "catch { }"))
PY

cat > "$WORK/m19.py" <<'PY'
import sys
work = sys.argv[1]
# the report stops saying which dependencies could not be loaded
p = work + "/src/TLDRevamp/LoaderScan.cs"
s = open(p).read()
old = "UNRESOLVED"
assert old in s, "anchor missing"
open(p, "w").write(s.replace(old, "not loaded"))
PY

cat > "$WORK/m20.py" <<'PY'
import sys
work = sys.argv[1]
# the CONTRIBUTING release flow names a command that does not exist: the file no test read
p = work + "/docs/CONTRIBUTING.md"
s = open(p).read()
old = "tools/package.sh"
assert old in s, "anchor missing"
# only the COMMAND BLOCK is renamed, so a mention in the prose does not mask the break
head, sep, tail = s.partition("## Making a release")
assert sep, "the release section is missing"
# the COMMAND BLOCK is the first fenced block of the section
fence = tail.find("```")
assert fence >= 0, "the release section has no command block"
block_end = tail.find("```", fence + 3)
assert block_end > fence, "the command block is not closed"
block = tail[fence:block_end]
assert old in block, "the command block does not name it"
# only the block is renamed, so a mention in the prose does not mask the break
tail = tail[:fence] + tail[fence:].replace(old, "tools/package-NOPE.sh", 1)
open(p, "w").write(head + sep + tail)
PY

cat > "$WORK/mnoop.py" <<'PY'
import sys
work = sys.argv[1]
# a HARMLESS change: an HTML comment in the README breaks nothing. The runner must report this as
# NOT CAUGHT. If it reports "caught", the runner kills every change and proves nothing.
p = work + "/README.md"
s = open(p).read()
open(p, "w").write(s + "\n<!-- a harmless comment, added by the harness's own no-op check -->\n")
PY

cat > "$WORK/m6.py" <<'PY'
import sys
work = sys.argv[1]
# the beginner README names a command that does not exist
p = work + "/README.md"
s = open(p).read()
anchor = "tools/package.sh"
assert anchor in s, "anchor missing"
open(p, "w").write(s.replace(anchor, "tools/package-NOPE.sh"))
PY

cat > "$WORK/m7.py" <<'PY'
import sys
work = sys.argv[1]
# the Nexus listing copy claims a voice range the code does not have
p = work + "/docs/NEXUS.md"
s = open(p).read()
# this tree's copy says "within about 60 m"; the claim is the number, not the wording
anchor = "60 m"
assert anchor in s, "anchor missing"
open(p, "w").write(s.replace(anchor, "120 m", 1))
PY

echo "== the mutations =="
mutate "the host stops calling OnUnload"                    "$WORK/m1.py"
mutate "the docs claim OnUnload but the code does not call it" "$WORK/m2.py"
mutate "the docs claim OnLoad runs at scene change, not when the player exists" "$WORK/m3.py"
mutate "the shim loses a lifecycle method the host calls"    "$WORK/m4.py"
mutate "the host stops giving the mods their config folder"  "$WORK/m5.py"
mutate "the host keys the world on the scene name again"      "$WORK/m8.py"
mutate "the host calls Config() on every IMGUI pass again"   "$WORK/m9.py"
mutate "the game quitting no longer unloads the mods"         "$WORK/m10.py"

# the README and the Nexus copy have their own tests
# The no-op goes through the SAME runner as the real mutations, with the verdict inverted: it must
# NOT be scored as caught. A runner that reports "caught" for a harmless comment kills every change
# it is given, and proves nothing about the ones it should catch.
mutate_noop() {
    # mutate_noop <what> <python file> <test project>
    copy_tree
    cp "$ROOT/README.md" "$WORK/"
    cp "$ROOT/docs/NEXUS.md" "$WORK/docs/"
    cp "$ROOT/src/TLDRevamp/Overlay.cs" "$ROOT/src/TLDRevamp/LoaderScan.cs" "$ROOT/src/TLDRevamp/FeedbackReporter.cs" \
       "$ROOT/src/TLDRevamp/Plugin.cs" "$WORK/src/TLDRevamp/"
    mkdir -p "$WORK/src/TLDRevamp/Net"
    cp "$ROOT/src/TLDRevamp/Net/Voice.cs" "$ROOT/src/TLDRevamp/Net/ModCatalog.cs" "$ROOT/src/TLDRevamp/Net/ModActivation.cs" \
       "$ROOT/src/TLDRevamp/Net/JoinLoading.cs" "$ROOT/src/TLDRevamp/Net/JoinLoadingView.cs" "$ROOT/src/TLDRevamp/Net/JoinLoadingState.cs" "$ROOT/src/TLDRevamp/Net/JoinChecks.cs" "$ROOT/src/TLDRevamp/Net/MpScreen.cs" \
       "$WORK/src/TLDRevamp/Net/"
    mkdir -p "$WORK/src/Mods/HeadUnit" "$WORK/packaging" "$WORK/tools/modscan" "$WORK/src/TLDRevamp.Preloader"
    cp "$ROOT/src/Mods/HeadUnit/Plugin.cs" "$WORK/src/Mods/HeadUnit/" 2>/dev/null || true
    cp "$ROOT/packaging/mods.json" "$WORK/packaging/" 2>/dev/null || true
    cp "$ROOT/src/TLDRevamp.Preloader/TLDRevamp.Preloader.csproj" "$WORK/src/TLDRevamp.Preloader/" 2>/dev/null || true
    cp "$ROOT/tools/modscan/Program.cs" "$WORK/tools/modscan/" 2>/dev/null || true
    require_scratch_files
    python3 "$2" "$WORK"
    rc=0
    out=$(dotnet run --project "tools/unit/$3.csproj" --configuration Release \
        --disable-build-servers --property:BaseIntermediateOutputPath="$OUTDIR/obj/$3/" \
        --property:OutputPath="$OUTDIR/bin/$3/" -- "$WORK" 2>&1) || rc=$?
    # Preserve the complete no-op output before evaluating the no-op verdict.
    printf '%s\n' "$out" >> "$RUNLOG"
    if [ "$rc" -eq 0 ] && echo "$out" | grep -q "ALL PASS"; then
        log "ok   the no-op SURVIVED: $1 (the runner does not kill a harmless change)"
    else
        log "ERROR  the no-op was killed: $1 (exit $rc). The runner would report every change as caught."
        echo "$out" | tail -3 | sed 's/^/            /' | tee -a "$RUNLOG" || true
        failures=$((failures + 1))
    fi
}

mutate_other() {
    # mutate_other <what> <python file> <test project>
    copy_tree
    cp "$ROOT/README.md" "$WORK/"
    cp "$ROOT/docs/NEXUS.md" "$WORK/docs/"
    # EVERY file the doc tests read, at the path they read it from. A test that starts reading a new
    # file must fail loudly here (a missing file), never "pass" on a crash.
    cp "$ROOT/src/TLDRevamp/Overlay.cs" "$ROOT/src/TLDRevamp/LoaderScan.cs" "$ROOT/src/TLDRevamp/FeedbackReporter.cs" \
       "$ROOT/src/TLDRevamp/Plugin.cs" "$WORK/src/TLDRevamp/"
    mkdir -p "$WORK/src/TLDRevamp/Net"
    cp "$ROOT/src/TLDRevamp/Net/Voice.cs" "$ROOT/src/TLDRevamp/Net/ModCatalog.cs" "$ROOT/src/TLDRevamp/Net/ModActivation.cs" \
       "$ROOT/src/TLDRevamp/Net/JoinLoading.cs" "$ROOT/src/TLDRevamp/Net/JoinLoadingView.cs" "$ROOT/src/TLDRevamp/Net/JoinLoadingState.cs" "$ROOT/src/TLDRevamp/Net/JoinChecks.cs" "$ROOT/src/TLDRevamp/Net/MpScreen.cs" \
       "$WORK/src/TLDRevamp/Net/"
    mkdir -p "$WORK/src/Mods/HeadUnit" "$WORK/packaging"
    cp "$ROOT/src/Mods/HeadUnit/Plugin.cs" "$WORK/src/Mods/HeadUnit/" 2>/dev/null || true
    cp "$ROOT/packaging/mods.json" "$WORK/packaging/" 2>/dev/null || true
    mkdir -p "$WORK/src/TLDRevamp.Preloader"
    cp "$ROOT/src/TLDRevamp.Preloader/TLDRevamp.Preloader.csproj" "$WORK/src/TLDRevamp.Preloader/" 2>/dev/null || true
    mkdir -p "$WORK/tools/modscan"
    cp "$ROOT/tools/modscan/Program.cs" "$WORK/tools/modscan/" 2>/dev/null || true
    require_scratch_files
    python3 "$2" "$WORK"
    rc=0
    out=$(dotnet run --project "tools/unit/$3.csproj" --configuration Release \
        --disable-build-servers --property:BaseIntermediateOutputPath="$OUTDIR/obj/$3/" \
        --property:OutputPath="$OUTDIR/bin/$3/" -- "$WORK" 2>&1) || rc=$?
    verdict "$1" "$out" "$rc"
}
# The no-op comes first: it proves the runner does not kill everything, before any real mutation is
# judged by it.
mutate_noop "a harmless HTML comment in the README" "$WORK/mnoop.py" ReadmeCommandsTest
mutate_other "the release flow names a command that does not exist" "$WORK/m20.py" ReleaseDocTest
mutate_other "the README names a command that does not exist" "$WORK/m6.py" ReadmeCommandsTest
mutate_other "the Nexus copy claims a voice range the code does not have" "$WORK/m7.py" NexusDocTest
mutate_other "the README's troubleshooting names an installer option that does not exist" "$WORK/m11.py" ReadmeCommandsTest
mutate_other "the README claims the overlay hides during a loading screen" "$WORK/m12.py" ReadmeCommandsTest
mutate_other "the overlay is not drawn in the top-left corner" "$WORK/m13.py" ReadmeCommandsTest
mutate_other "the scanner stops reporting a section the docs promise" "$WORK/m14.py" LoaderScanDocTest
mutate_other "the docs promise a section the scanner never writes" "$WORK/m15.py" LoaderScanDocTest
mutate_other "the default catalog URL no longer points at dev" "$WORK/m16.py" LoaderScanDocTest
mutate_other "BepInEx core is no longer protected from being toggled" "$WORK/m17.py" LoaderScanDocTest
mutate_other "the scanner stops counting unresolved member sites" "$WORK/m18.py" LoaderScanDocTest
mutate_other "the report stops marking an unresolved dependency" "$WORK/m19.py" LoaderScanDocTest

# The build output went to an absolute directory outside the repository (OUTDIR, removed by the
# trap), so nothing was written into tools/unit/bin or tools/unit/obj. The check below proves it.
for p in "$ROOT"/tools/unit/bin/LLDTM "$ROOT"/tools/unit/obj/LLDTM; do
    if [ -e "$p" ]; then
        echo "ERROR: the harness left a build tree in the repository ($p)"
        failures=$((failures + 1))
    fi
done

echo
if [ "$failures" -eq 0 ]; then
    log "mutation check passed: every deliberate break was caught"
    exit 0
fi
echo "mutation check FAILED: $failures break(s) went unnoticed"
exit 1
