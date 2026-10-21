#!/usr/bin/env bash
# The ONE serialized check entrypoint: every unit project, one after another, with each project's
# exit code and its full output kept.
#
#   tools/verify.sh                 # every unit project under tools/unit
#   tools/verify.sh Terrain         # only the projects whose name matches "Terrain"
#
# Why one entrypoint: the unit suite is 63 projects and the count keeps growing. A reader who wants
# to know the state of the project should not have to guess which command runs what, and CI should not
# have to keep a list either. The project list is ENUMERATED, never written down.
#
# The output of every project goes to its own log under the artifact directory, and the exit code of
# every project is recorded. Nothing is written into the repository: the build output and the logs go
# to an artifact directory OUTSIDE the tree, so a failed run can never leave a build tree in
# tools/unit/bin or tools/unit/obj (UnitIsolationTest refuses an output tree that belongs to no
# unit project).
#
# The exit code is the number of projects that failed, capped at 125, and 0 when every project
# passed. A machine-readable manifest is written next to the logs.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

FILTER="${1:-}"

# Progress output is BEST EFFORT. The consumer of this script's stdout can go away at any moment
# (a CI log pipe, a pager, `head -1`, the admission broker's own pipe). A closed consumer must NOT
# end the run: the authoritative result is the exit code of every project, not the fate of a
# progress line. A broken pipe used to kill this shell with SIGPIPE (exit 141) part way through
# the suite, so a run with no failing project still looked like a failure.
#
# Every line is written from a SUBSHELL that ignores SIGPIPE. This shell, and every child it
# starts (dotnet included), therefore keeps the SIGPIPE disposition it was given; only the
# undeliverable line is lost. The subshell's stderr is turned away so a lost line is not a
# complaint, but the exit code of the run is never taken from it.
say() {
    ( trap '' PIPE; printf '%s\n' "$1" ) 2>/dev/null || true
}

# The same for the one line that goes to stderr. The real stderr is kept on fd 3 first, because
# the subshell's own stderr is turned away.
say_err() {
    ( trap '' PIPE; printf '%s\n' "$1" >&3 ) 3>&2 2>/dev/null || true
}

# The CI step summary is a file, but it can be a pipe on some runners, so it is written the same
# tolerant way.
add_summary() {
    ( trap '' PIPE; printf '%s\n' "$1" >> "$2" ) 2>/dev/null || true
}

# The artifact directory is DURABLE by default: a verification run's logs are evidence, and evidence
# that lives in /tmp is lost on a reboot. The default is under the user's state directory, which is
# the same place the worker root lives. An explicit TLD_VERIFY_DIR always wins.
STATE_HOME="${XDG_STATE_HOME:-${HOME:-}/.local/state}"
DEFAULT_BASE="$STATE_HOME/tld-revamp/verification"
if [ -n "${TLD_VERIFY_DIR:-}" ]; then
    ART="$TLD_VERIFY_DIR"
else
    # a UNIQUE subdirectory per run, so two runs never share a directory
    stamp="$(date -u +%Y%m%dT%H%M%SZ)-$$"
    ART="$DEFAULT_BASE/$stamp"
fi
LOGDIR="$ART/logs"
mkdir -p "$LOGDIR"
ART="$(cd "$ART" && pwd)"

# The scratch build tree: absolute, unique, outside the repository.
BUILD="$ART/build"
mkdir -p "$BUILD/obj" "$BUILD/bin"

say "verify: the artifact directory is $ART"
say "verify: the projects are ENUMERATED from tools/unit/*.csproj (the count is never written down)"

failed=0
total=0
manifest="$ART/manifest.tsv"
printf 'project\texit\tlog\n' > "$manifest"

for project in tools/unit/*.csproj; do
    name="$(basename "$project" .csproj)"
    [ -n "$FILTER" ] && case "$name" in *"$FILTER"*) ;; *) continue ;; esac
    total=$((total + 1))
    log="$LOGDIR/$name.log"
    say "::group::$name"
    # Each project gets its OWN intermediate and output path, so one project's launcher can never
    # overwrite another's.
    dotnet run --project "$project" --configuration Release --disable-build-servers \
        --property:BaseIntermediateOutputPath="$BUILD/obj/$name/" \
        --property:OutputPath="$BUILD/bin/$name/" > "$log" 2>&1
    rc=$?
    # The CI step summary is OPTIONAL. With `set -u` an unset GITHUB_STEP_SUMMARY is an error, so it
    # is read with a safe fallback and the summary is written only when there is one.
    summary="${GITHUB_STEP_SUMMARY:-}"
    if [ "$rc" -eq 0 ]; then
        if [ -n "$summary" ]; then add_summary "- PASS $name" "$summary"; fi
        say "PASS $name (exit 0)  $log"
    else
        failed=$((failed + 1))
        if [ -n "$summary" ]; then add_summary "- FAIL $name (exit $rc)" "$summary"; fi
        say "FAIL $name (exit $rc)  $log"
        # The tail of the log is shown through the same tolerant writer, so a closed consumer
        # cannot turn a failed project into a dead script.
        tail_lines="$(tail -5 "$log" 2>/dev/null | sed 's/^/            /' 2>/dev/null)"
        if [ -n "$tail_lines" ]; then
            while IFS= read -r line; do say "$line"; done <<< "$tail_lines"
        fi
    fi
    printf '%s\t%s\t%s\n' "$name" "$rc" "$log" >> "$manifest"
    say "::endgroup::"
done

say ""
say "verify: $((total - failed)) of $total projects passed"
say "verify: the manifest is $manifest"
say "verify: the logs are in $LOGDIR"

[ "$total" -eq 0 ] && { say_err "verify: no project matched"; exit 2; }
[ "$failed" -gt 125 ] && failed=125
exit "$failed"
