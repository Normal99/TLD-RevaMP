# The composed launch: compositor -> admission broker -> systemd unit -> game

## Why the three layers are in this order

An automated test copy must be hidden and unfocused from its first frame, must run
inside the machine's admission limits, and must be attributable to exactly one
launch.  The three layers each own one of those, and their order is forced by two
facts that were measured on this machine, not assumed.

### Fact 1: the per-launch rule travels in the environment, not in the process tree

Hyprland 0.56.2 (`src/desktop/rule/windowRule/WindowRule.cpp:456-470`) matches a
per-launch rule against the **environment of the window's own process**: it reads
`HL_EXEC_RULE_TOKEN` from `/proc/<window pid>/environ`.  `src/config/supplementary/
executor/Executor.cpp:194` sets that variable in the process it forks, and
`src/desktop/rule/windowRule/WindowRule.cpp:481-520` registers the rule for that
token.  A window matches when its process carries the token, so the rule survives a
re-parent: systemd re-parenting the game does not break the containment.

The rule is not a process-tree match, so the *order* of the layers is free in that
respect -- but the *token* has to reach the game.

### Fact 2: the token is lost at the systemd boundary unless it is carried explicitly

`systemd-run` does not pass the caller's environment into the unit it starts.  On
this machine:

```sh
HL_EXEC_RULE_TOKEN=probe systemd-run --user --quiet --wait --pipe /usr/bin/env | grep -c HL_EXEC_RULE_TOKEN
# 0
```

The systemd user manager *does* carry the compositor's own environment, so a unit
can still reach the display and the compositor:

```sh
systemd-run --user --quiet --wait --pipe /usr/bin/env | grep -E '^(WAYLAND_DISPLAY|HYPRLAND_INSTANCE_SIGNATURE)='
# WAYLAND_DISPLAY=wayland-1
# HYPRLAND_INSTANCE_SIGNATURE=efb50993780079460b0cbed1363e2166a2de1d9f_1792539290_1682221551
```

So the unit can be the outermost layer, but the token has to be handed over
explicitly.  `tools/launchshim.py` is that handover: the outer mode runs inside the
compositor dispatch (where the token is), writes the token to a private file, and
runs the broker; the inner mode runs inside the unit, restores the token, and
execs the game.

### Fact 3: the compositor's dispatch passes no environment on either

`hyprctl dispatch exec` runs the command through `/bin/sh -c` of its own and does
not forward the dispatcher's environment:

```sh
TLD_TEST_LAUNCH_TOKEN=abc hyprctl dispatch exec "/tmp/probe.sh"   # the probe sees no token
TLD_TEST_LAUNCH_TOKEN=abc hyprctl dispatch exec "TLD_TEST_LAUNCH_TOKEN=abc /tmp/probe.sh"  # it does
```

The launch token is therefore set **inside the command line** (`env
TLD_TEST_LAUNCH_TOKEN=...`), not in the environment of the process that calls
`hyprctl`.  This was a real defect found by measurement, not by reading.

### Fact 4: Proton keeps the environment

`Proton - Experimental/proton:1532` copies the whole environment
(`self.env = dict(os.environ)`) and removes only `LC_ALL`, `WINEARCH` and
`WINEFSYNC` (lines 1605, 1618, 1819).  The token therefore reaches the game
process under Wine, which is the process Hyprland reads for an X11 window
(`src/xwayland/XSurface.cpp:39`, the X client's own PID).

## The composition

```
hyprctl dispatch exec "[workspace special:tld-tests silent; float; size W H;
                        no_initial_focus; suppress_event fullscreen maximize]
                       env TLD_TEST_LAUNCH_TOKEN=<token>
                       python3 tools/launchshim.py --outer <tokenfile> --exitfile <exitfile> --
                         tools/tld-admit run --profile game --name tld-test-<port>-<token> --
                           python3 tools/launchshim.py --inner <tokenfile> --cwd <game> --log <log> --
                             env STEAM_COMPAT_*=... <slr>/_v2-entry-point --verb=waitforexitandrun
                                 -- <proton>/proton waitforexitandrun <game>/TheLongDrive.exe
                                    -screen-fullscreen 0 -screen-width W -screen-height H -tldbridge <port>"
```

* The dispatch is the only process-launch boundary; its rules apply at map time,
  so the window is hidden and unfocused from its first frame.  Moving a window
  afterwards is recovery, not acceptance.
* The broker owns the machine's admission limits: it refuses a second job, it
  pauses under memory pressure, and it starts the game in one transient
  `tld-job-*.service` unit with the `game` profile (MemoryMax 10G, CPUQuota 300%,
  MemoryOOMGroup).  The broker's exit code 75 (paused or unavailable) is a
  refusal, and the controller reads it.
* The shim carries the two tokens across the systemd boundary and refuses to
  start anything without both of them.

## The synthetic containment proof

`tools/synthproof.py` runs the *shipped* composition -- `target.compose()` builds the same argv the
guarded launch uses -- with a harmless synthetic GUI process instead of the game: `tools/synthwin.py`
maps one GTK window, reports its own pid, cgroup, tokens and size, and exits by itself.  It is not the
game and it touches nothing but the compositor.

The proof asserts, against the real broker, the real systemd user manager and the real compositor:

* a refused admission returns 75, creates no unit and creates no window;
* exactly one `tld-job-game-*` unit exists, and it belongs to this launch;
* the first map is on `special:tld-tests`, floating, not fullscreen, at exactly 1280x720, and the
  window never took the focus (the active window is unchanged) -- every observation during the run
  agreed, not only the last one;
* the synthetic process and its descendants share the unit's cgroup;
* the unit carries the game profile's limits (`MemoryHigh` 8 GiB, `MemoryMax` 10 GiB, `MemorySwapMax`
  2 GiB, `TasksMax` 512, `CPUQuotaPerSecUSec` 3s, `CPUWeight`/`IOWeight` 100, `Nice` 0, `OOMPolicy`
  kill, `Type` exec) and the kernel's `memory.oom.group` is 1, `pids.max` 512, `memory.max` 10 GiB;
* the cleanup is scoped: the owned process is gone and an unrelated process survives.

The broker's *gauges* (meminfo, the watched cgroup, PSI, vmstat) come from the broker's own
sanctioned test hooks (`TLD_ADMIT_MEMINFO`, `TLD_ADMIT_WATCHED_CGROUP`, `TLD_ADMIT_PSI`,
`TLD_ADMIT_VMSTAT`), so the proof does not depend on this machine's momentary memory pressure.  The
unit, its cgroup, its limits and the profile verification are all real.  The `game` profile's
capacity gate (12 GiB MemAvailable) is *not* bypassed: the refusal case proves the gate refuses
before anything is dispatched.

Result on this machine: **39/39 checks pass**, and the run leaves no unit and no process behind.

## What is proven offline, and what is not

Proven by the offline suite (`tools/test_background_launch.py`): the composition's
argv order, the game profile and job name, exactly one `-tldbridge PORT`, the
launch token and the two Proton identities reaching the unit, the refusal when
the broker or the shim is missing, the refusal without a compositor token or a
launch token, the token file's privacy, the refusal of an incomplete or missing
token file, that no token is ever printed, and that an admission refusal is
treated as a refusal rather than a slow start.

Proven by measurement on this machine (no game was started): the dispatched
payload ran inside the broker's `tld-job-*.service` cgroup, both tokens were
present in it, `hyprctl` answered from inside the unit, and the broker's exit code
reached the controller.  The same measurement found the environment loss in fact 3
and the fix above.

Not proven here: a live game launch.  A live run still needs the acceptance
evidence listed in `/tmp/tld-free-workers/context/BACKGROUND-LAUNCH-MANDATORY.md`
(Hyprland events from before the first map through shutdown, on both machines).
