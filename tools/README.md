# Test tools

Scripts that drive the real game for testing: they build and deploy the mod, start and quit the game, load a seeded
test world, and run scenarios through the mod's **debug bridge**. One machine is enough for most of them; the
multiplayer scenarios use a second machine over SSH.

Everything here is written for **Linux with Steam + Proton** (the game runs through Proton, the tools run natively).
Python 3.9+ and the standard library only.

## Setup

1. Build the mod once as the [main README](../README.md#building-from-source) says (`tools/libs.sh`, then
   `tools/build.sh`). `build.sh` also turns the debug bridge on in *this* game's config (`Bridge = true` in
   `BepInEx/config/tldrevamp.core.cfg`). A player's install has it off.
2. Copy `tools/local.env.example` to `tools/local.env` (gitignored), or to `~/.config/tld-revamp/tools.env` to share it
   between checkouts, and set whatever differs on your machine. Nothing is needed for a default Steam library on one
   machine. Environment variables override the file. All of it is read in one place, [`tldenv.py`](tldenv.py).
3. `tools/doctor.py` checks the setup without changing anything (Steam, the game, BepInEx and its launch option,
   `lib/`, the deployed build, the bridge setting, and the second machine when one is configured).
4. Recommended: give the tests a Proton prefix of their own, so they never run in (or change) the one you play in —
   your game settings and saves. Set `TLD_TEST_COMPAT` (a folder, e.g. `~/tld-revamp-testprefix`) and, if Steam runs
   the game with a Proton other than Proton Experimental, `TLD_PROTON` (that Proton's folder). The tools then start
   the game through Proton directly; Proton makes the prefix on the first launch (allow a few minutes). If a fresh
   prefix never gets the game going, `tools/testprefix.py make` copies the game's own prefix without the game's
   settings, saves and PlayerPrefs. The second machine has the same settings with `TLD_REMOTE_` in front.
   A new prefix runs the game's default graphics; on a slower machine `tools/testprefix.py lowgfx [--remote]` (the
   game running in the test prefix) switches the effects off — the preflight below refuses a game under 30 fps.
5. Run Steam (without a test prefix the tools launch the game with `steam -applaunch`), then check the loop works:

   ```sh
   tools/game.py launch           # starts the game and waits for the bridge
   tools/tld.py ping              # one bridge command (tools/tld.py help lists the basic ones)
   tools/game.py newgame          # a new world with the fixed test seed
   tools/game.py quit
   ```

**Your saves are protected.** Every tool that runs a world backs up the save folder first (`TLD_BACKUPS`, default
`~/tld-revamp-backups/`) and puts the autosave slots back afterwards; the mod also blocks autosaving in test worlds.
Still: keep your own backup of anything you care about before the first run.

**The bridge can run anything in the game.** It listens on 127.0.0.1:27050 only and is off unless the config turns it
on. Never expose it to the network, and leave it off when you play normally. The second machine's bridge is reached
through an SSH tunnel, never directly.

## The loop

```sh
tools/game.py cycle            # build + deploy, restart the game, new seeded world (one iteration)
tools/game.py status           # running? bridge up?
tools/game.py gamecheck        # is the installed game the build the mod was verified on (game_build.json)?
tools/bench.py --scenarios idle,drive mylabel     # frame-time benchmark → bench/<time>-mylabel.json
tools/bench.py --compare bench/A.json bench/B.json
tools/seeded.py Some.Runtime.Switch               # A/B a switch on identical, freshly generated worlds
```

Most other scripts are single scenarios (a bug's reproduction, a feature check). Each one's docstring at the top says
what it sets up, what it checks and its options. They print `PASS`/`FAIL` lines and a summary at the end; read the
summary, since not every script sets its exit code. `--no-build` reuses the last build. The bridge commands they use
are implemented in `src/TLDRevamp/DebugBridge.cs` (and `Net/Mp.cs` for the `mp …` ones).

## Two machines

The multiplayer tests (`playround.py`, `copylock.py`, `anomalywatch.py`, `buglist2m.py`, `twocars.py`, `releasecheck.py`
and most scripts importing `mplib`) need a second Linux machine with the game. It needs no checkout of this repo: everything runs from here over SSH.

- `TLD_SECOND=user@host` in your config, and key-based SSH to it (`ssh user@host true` must work without a prompt;
  set `TLD_SSH_KEY` if it needs a specific key). The tools open one multiplexed SSH connection and reuse it.
- rsync and Steam on it, the game installed with BepInEx and its Proton launch option (see the release's
  `INSTALL.txt`), and run once.
- Both machines on the same game build, and Steam signed in on each with a **different account** (the join goes
  through Steam, by the host's Steam ID).

In test output "the desktop" is the machine you run the script on (it hosts) and "the laptop" is `TLD_SECOND` (it joins).
The tools build here, rsync the DLLs to the second machine, restart both games on the same build, and drive the
second one through `mpagent.py` (which runs here and reaches that game's bridge through an SSH tunnel). Its saves are
protected the same way. Many tests take `--relay <datacenter>` (often `iad` by default) to route the session through a
Valve relay for real latency, or `--relay off`.

### A second Steam account on one machine

Without a second machine, a second Linux user on this one can be "the laptop": its own home, its own native Steam
signed in to the second account (a different Steam ID, its own saves) and its own systemd user manager, so its own
admission broker admits its one game (the broker admits one game job per Linux user, so a second game of the same
user is always refused). Its game runs in headless gamescope only; nothing of it reaches the desktop.

- **Its own network namespace.** A game reaches its Steam client on 127.0.0.1:57343 (fixed in Steam's client library),
  and only one Steam per network namespace can hold that port: without one of its own, the second user's games talk
  to the first Steam and come out on the host's account. A root service (`ip netns add`, then pasta for user-mode
  networking, `--runas 0`, forwarding only the bridge port with `-t 127.0.0.1/27050 --host-lo-to-ns-lo`) and a
  `NetworkNamespacePath=` drop-in for the user's `user@<uid>.service` put its whole user manager (its Steam, and the
  broker's game units) inside; a bind-mounted resolv.conf points DNS at pasta's forward (`--dns-forward`).
- The user (root, once): no password, `loginctl enable-linger` (its games outlive the SSH login), `video`/`render`
  groups, key-only SSH from 127.0.0.1 with port forwarding allowed (the bridge tunnel). Its game copy, test prefix,
  Proton and SteamLinuxRuntime as reflinks in its home.
- Its Steam is started once with `DISPLAY` on the desktop's Xwayland (`xhost +SI:localuser:<user>`) to sign in, then
  stays running (a user unit, so it is in the namespace).
- The tools see it as a second machine, plus `TLD_REMOTE_DESKTOP=none` (that user has no desktop session: the guard
  starts its game headless itself instead of through the compositor, refuses if the user has a Hyprland session, and
  reads the bridge listener in the game's own namespace). On one machine the tunnel's local end needs its own port
  (`TLD_BRIDGE_FORWARD_PORT`), and the host is the namespace's gateway (pasta's `--map-host-loopback`):

  ```sh
  TLD_SECOND=<user>@localhost TLD_SECOND_IP=127.0.0.1 TLD_HOST_IP=<the namespace gateway> TLD_SSH_KEY=~/.ssh/<key>
  TLD_REMOTE_DESKTOP=none TLD_REMOTE_BRIDGE_PORT=27050 TLD_BRIDGE_FORWARD_PORT=27451
  TLD_REMOTE_DIR=$HOME/tld-installs/second/game TLD_REMOTE_TEST_COMPAT=$HOME/tld-installs/second/compat
  TLD_REMOTE_PROTON=$HOME/tld-tools/GE-Proton11-3 TLD_REMOTE_SLR=$HOME/tld-tools/SteamLinuxRuntime_4
  ```

  Check `net me` on both games: two different Steam IDs, or the namespace is missing.
- Memory: a test game peaks at about 5 GB and a Steam client takes about 1 GB, and each launch still needs the broker's
  12 GiB MemAvailable, so two games fit on a 32 GB machine only while the desktop's own apps leave room.

## Running many tests

```sh
tools/suite.py smoke                  # ~5 short two-machine tests, one summary table at the end
tools/suite.py full                   # the pre-release set, every test on freshly started games
tools/suite.py hitcheck.py 'playround.py --rounds 1'    # any tests, with their own options
tools/suite.py --list                 # the tiers (tools/suite.json)
```

The table shows each test's PASS count, its time, and how much of it was setup against the test itself. Logs go to
`bench/suite-<time>-<name>/`. Every two-machine test (`mplib`) also prints its own split at the end:
`[time] agent 3s, build 0s, restart 52s, world 31s, check 4s, relay 2s, join 9s, test 48s`.

What keeps the setup short:
- Both games restart at the same time. The build is skipped when no file it reads changed since the last one, and the
  copy to a machine when its game already has these files (`--no-build` still deploys the last build).
- **Games kept between tests.** A test that follows a clean one (no FAIL, no error exit, no exception, the join
  worked) on the same build keeps both games: their session is stopped and the test makes its own fresh same-seed
  world. After anything else, a rebuild, or an hour without tests, the games restart. `TLD_REUSE=0` (or
  `suite.py --fresh`) always restarts; `TLD_REUSE=1` keeps any game already on this build. The `full` tier always
  restarts. Kept games carry what an earlier test switched at runtime and didn't put back.
- A preflight right after the fresh world stops a test in seconds when it couldn't mean anything: a game outside the
  test prefix, vsync on or paused in the background (1 fps unfocused), under `TLD_PREFLIGHT_FPS` (30) frames a
  second, auto shift off, or the second machine on the host's Steam account. `TLD_PREFLIGHT=0` skips it. God mode a
  player had before a join is on again after it.
- One test run at a time: the tests hold a lock (`$TLD_CACHE/machines.lock`, a suite for its whole run) and a second
  run waits for it, saying who holds it.

## Dev builds

```sh
tools/devrelease.py --dry-run         # build the checked-out commit and zip it to dist/, nothing uploaded
tools/devrelease.py                   # the same, then upload it as a draft GitHub release (gh signed in)
tools/devrelease.py --public          # ... or publish it as a public pre-release instead
```

A dev build lets testers try `dev` without building it. By default it is a **draft** release: only people with write
access to the repository see and download it (release page while signed in, or `gh release download <tag>`), and its
tag `v<version>-dev.<commit>` only appears if someone publishes it. `--public` makes it a pre-release anyone can
download, never marked latest. The installers stay pinned to their release; a dev build is installed from its zip. In game it shows as e.g. `0.69.0-dev.c8d9c55`, and multiplayer only lets that exact build
join (BepInEx itself sees the plain `0.69.0`: it skips plugins whose version isn't plain numbers). The zip has the
release layout and private-string check of `package.sh`, in Python so it also runs on Windows (Git Bash has no
`zip`/`strings`). It needs a clean checkout whose commit is pushed, and the game with BepInEx 5 in `TLD_DIR`.

## How a test copy is launched (the launch policy)

A test copy is **never** started by hand and **never** through a raw Proton script. `tools/game.py` is the only
way, and it goes through `tools/testdisplay.py`, which is the mechanism ported from the private proven
implementation (master commit `09f7c55`, "Test copies in parallel on the desktop: own prefix/port per copy,
muted and hidden; round reviewed").

| requirement | how it is met |
|---|---|
| its own Proton prefix | `TLD_TEST_COMPAT` (and `TLD_REMOTE_TEST_COMPAT` on the second machine). A copy that would run in the Steam owner prefix is refused. |
| hidden | the launch is wrapped in Hyprland's rules: `[workspace special:tld-tests silent; float; size 1280 720; no_initial_focus; suppress_event fullscreen maximize]`. |
| no focus | `no_initial_focus`, and the window is checked for `focusHistoryID == 0`. |
| `bg on` | `bg on` through the bridge, applied again after the bridge answers (the first-run defaults can override Unity's flags). |
| vsync off | `UnityEngine.QualitySettings.vSyncCount 0` and `settingsscript.s.S.BVsync False`. |
| the lowest quality | `UnityEngine.QualitySettings.SetQualityLevel(0, true)`, and the costly effects are off in the copy's own settings file. |
| the 30 FPS cap | the game's own limiter: `settingsscript.s.S.BEnableFpsLimit True` and `FFPSLimit 30`. A deliberate 30 FPS cap is the policy, not a defect. |
| the game-only mute | `settingsscript.s.S.FSound 0` and `UnityEngine.AudioListener.volume 0`. The desktop's own audio is never touched. |
| the resolution | the desktop copy is 1280x720 windowed, the second machine's copy is 800x450 windowed. Never larger, never fullscreen. |

**Without a compositor socket the launch fails closed.** There is no way to place the window in the hidden
workspace, so `testdisplay.background_command()` returns nothing and `game.py` refuses to launch: an ordinary
visible launch of a test copy is exactly what the launch-policy violation was, and it is not an acceptable
fallback. On the second machine the rules are applied **there** (the window belongs to that machine's
compositor).

The process handling is scoped the same way: `running()` and the kill look only at the processes whose
`STEAM_COMPAT_DATA_PATH` / `WINEPREFIX` is this run's prefix, so one worker's copy is never mistaken for
another's and a machine-wide `pkill` is never used for a test copy.

`tools/test_launch_policy.py` pins all of this (16 checks, offline, no game) and fails if any of it is
weakened - including if the launch ever falls back to a visible one.

## Copies of the game (TLD Copies)

`tools/installs/` keeps copies of The Long Drive next to Steam's, each with its own saves: the game exactly as Steam
has it, no BepInEx or mods (**clean**), without gameplay mods (**vanilla**), a TLD RevaMP **release**, a **dev build** of
the checkout, the **newest dev built here** from an app-owned clone, or the **test** copy the test tools run. Steam's own install is only read; it is never changed. The window
(TLDCopies) shows a card per copy (what's in it, branch and game build, size, last launch) with Launch/Stop, Update,
Open folder and Remove, and a New copy panel. A copy with RevaMP also has a **Report a bug** box: the text is sent as an
issue in the releases' repository (`TLD_REPO`) with the copy's RevaMP version, BepInEx and game build in its body, then
put on the bug board (`TLD_BUG_PROJECT`, default `Normal99/1`) in To triage. It uses the GitHub sign-in (`gh auth
login`; the board also needs `gh auth refresh -s project`). Choose the related marks and report zips to keep in one
local zip under `<copy>/bug-reports/` (100 MB cap); the newest mark from the last 30 minutes is ticked, the optional
newest play log is off. The folder opens, and nothing is uploaded. **Do not attach report zips, marks or play logs to
GitHub issues (public), or upload them anywhere.** Describe the bug and when it happened without telemetry or file
summaries; the developers will ask for the files privately if needed. The issue gets only a file count and the first
12 hex characters of the local zip's SHA-256 as a reference. The command line (`tld-installs`) does the same for scripts:

```sh
dotnet run --project tools/installs/Installs.App -c Release          # the window
dotnet run --project tools/installs/Installs.Cli -c Release -- list  # tld-installs list | make | update | sync | run | stop | check | remove | releases | where
tools/installs/publish.py                                            # single files that need no .NET: dist/installs/<linux-x64|win-x64>/
dotnet test tools/installs/Installs.Tests
tld-installs report NAME "what's broken"                             # choose private files, then send the public description
```

- Copies go to `~/tld-installs/<name>/` (`%USERPROFILE%\tld-installs` on Windows; `TLD_INSTALLS` or the window's
  Change… button to move it). `make NAME --clean | --vanilla | --release [VERSION] | --source [REF] | --dev | --test`.
- **Newest dev, built here** (`--source`) needs Git and a .NET SDK. It clones
  `https://github.com/Normal99/TLD-RevaMP` into `<cache>/source/TLD-RevaMP`, fetches `origin`, and checks out the chosen
  branch or full commit detached (default `origin/dev`). It never uses or changes your checkout. References come from
  Steam's Managed DLLs and the pinned, checked BepInEx download, with the same list as `libs.sh`. The player layout
  has the bridge off and shows `<version>-dev.<short commit>`. Windows also gets CopyIsolation.
  `update NAME` fetches and rebuilds only when the commit changed; `update NAME --ref BRANCH_OR_SHA` switches refs.
  The card shows the installed commit, title, date and ref, and checks how many newer commits the ref has every two
  minutes. **Build** (or `tld-installs build NAME`) fetches and rebuilds immediately. **Update to the newest commit
  before launching** is on by default: launch fetches and builds a changed commit before starting the game. A failed
  fetch or build prevents that launch; its output is available behind Show log.
  Source builds isolate build servers, recover failed cache checkouts, and verify the built and installed DLL identity before saving; cards and `list` flag when the installed commit's label differs.
  Launch refuses a second game for the same copy unless `--force` is passed.
- Bug reports carry the installed build's full and short commit, title, date, ref, dirty flag and commit link when
  known. Release tags and locally built dev/test copies record their commits too; public issues contain no telemetry,
  paths or logs. Use `--files default`, `--files none`, or `--files 1,2` to select local files without a prompt;
  `bug` remains an alias for `report`.
- RevaMP embeds the full source commit and whether `src/` has uncommitted changes in assembly metadata, including
  plain `dotnet build`, `build.sh` and `devrelease.py`. Protocol 36 compares clean builds by commit across SDKs and
  platforms. A dirty build requires an identical full DLL SHA-1; an unknown commit falls back to the DLL hash.
- **Clean** copies contain only the game's files, without BepInEx, doorstop, CopyIsolation or gameplay mods.
  `update` and `sync` bring their game files up to Steam's without installing any mods.
- Each card and `list` show the branch and build recorded when the copy was made or synced, for example
  `beta branch, game build 16539689`. Steam's appmanifest `BetaKey` supplies the branch; missing or empty means
  `public`. The New copy panel shows Steam's current branch and build: new copies take those files. Changing Steam's
  branch leaves an existing copy's label alone until Update or Sync. Older copies show `unknown branch` until synced.
- Release copies get BepInEx as `installer/install.sh` pins it and the release zip from GitHub, each checked against
  its SHA-256. Dev copies come from `tools/devrelease.py --dry-run`, test copies from `dotnet build` as `build.sh` does
  (debug bridge on). Both need the checkout's `lib/` (`tools/libs.sh`).
- Every copy with RevaMP (release, dev, test) has **all telemetry on** (`docs/TELEMETRY.md`): its config's
  `[Telemetry]` gets `ScriptTimers = true` (script timers and engine phases), `ScriptTimerAlloc = true` and
  `ScriptTimerColumns = 32`, on top of the play log, black box and frame counters that are always on. Copies made
  before this get it at their next Update or launch, once: a value changed by hand afterwards stays.
- `tld-installs releases` and the window's Release list include draft dev builds when signed in with repository write
  access. Credentials come from `GH_TOKEN`, then `GITHUB_TOKEN`, then `gh auth token` (install GitHub CLI and use
  `gh auth login`); tokens are never saved or logged. Without a token only public releases appear, with a sign-in
  hint. Drafts and pre-releases are labelled, newest first; for example,
  `tld-installs make try-dev --release 0.69.1-dev.fea22b0`. Draft downloads use GitHub's asset API and check SHA-256
  against the asset digest, or the checksum in the release notes when there is no digest. Update keeps the installed
  version, including a draft, unless `--release VERSION` (or Change to in the window) selects another.
- **Linux:** the game files are a reflink copy on btrfs/XFS (they share disk blocks with Steam's: a copy takes a few
  MB until Steam updates the game; elsewhere a full ~0.6 GB copy). Each copy runs in a Proton prefix of its own
  (`<copy>/compat`), copied from the tests' prefix or the game's own without the game's settings, saves and
  PlayerPrefs; the test copy uses the tests' prefix (`TLD_TEST_COMPAT`). It launches through Proton as the test tools
  do (Steam running). To run the tests in the test copy instead of Steam's install, set
  `TLD_DIR=~/tld-installs/test/game`: `game.py` then brings the copy's game files up to Steam's whenever Steam has
  updated the game.
- **Windows 10/11:** install the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build it (or use the files from
  `publish.py`, which need nothing), then `dotnet run --project tools\installs\Installs.App -c Release` in the checkout
  (`py tools\installs\publish.py win-x64` makes `TLDCopies.exe`). Copies are full copies (NTFS has no reflinks: ~0.6 GB
  each, shown before making one). A copy starts as its own `game/TheLongDrive.exe` (Steam running; `steam_appid.txt`
  next to it). A **clean** copy sets `USERPROFILE`, `HOMEDRIVE` and `HOMEPATH` for the game process only, pointing at
  `<copy>/profile`, and creates its `Documents` folder. Saves and game data go to
  `<copy>/profile/Documents/TheLongDrive`; Unity's LocalLow log may follow the profile too. Steam still connects
  through IPC. The one thing a Windows clean copy shares with Steam's game (and other clean copies) is **PlayerPrefs**
  in `HKCU\Software\Genesz\TheLongDrive`: last save, language, resolution and a few options. Only a mod can separate
  those. All other Windows copies get the tiny **CopyIsolation** BepInEx plugin: saves, settings, screenshots and
  other game data go straight into `<copy>/UserData`, and PlayerPrefs registry keys have a `copy[<copy name>]:`
  prefix. **Vanilla on Windows includes pinned, SHA-256-checked BepInEx and only this plugin, with no gameplay mods.**
  Different copies and Steam's game can run side by side; only starting the same copy twice is refused. The window
  can close while games keep running. `tld-installs run` waits for its game, but no save-folder swapping is needed.
  On an old non-clean copy's next launch or update, the tool installs the plugin and moves `saves/TheLongDrive` into
  `UserData` once. If both already contain files, it refuses to overwrite either: move one aside before retrying. For this
  release it also recovers any old swap left in Documents while no game runs, restoring Steam's folder and moving
  the stranded copy's data into its `UserData`. Non-clean copies keep Steam's saves and registry keys separate.

The window and CLI carry the isolation DLL as an embedded resource, including in single-file publishes. Building
them needs this checkout's `lib/` game/BepInEx references; the plugin targets .NET Framework 4.7.2 for BepInEx 5, while
the tools keep their .NET 10 target. To build a relocated copy of `tools/installs`, pass
`-p:CopyIsolationLibDir=/path/to/checkout/lib`. Set `TLD_REPO_DIR` to the checkout when running relocated tests, so
`BepInExPinMatchesTheInstaller` can read `installer/install.sh`.

## Offline telemetry comparison

`telemetry_compare.py` runs on Windows or Linux with Python 3.9+ and no dependencies or game installation. Give it
report zips, mark JSON snapshots, play logs, or folders (searched recursively):

```sh
py -3 tools/telemetry_compare.py reports/ players/ marks/ sessions/ -o bench/telemetry
py -3 tools/telemetry_compare.py tools/fixtures/telemetry/ -o bench/telemetry-fixtures
py -3 -m unittest tools/test_telemetry_compare.py
```

On Linux use `python3` instead of `py -3`. Open the resulting HTML files locally: each session/moment has one page
with differences and inline charts, requiring no internet. Stdout gives a short summary; malformed inputs produce
warnings and usable inputs are retained. Files are grouped by `sid`, with moment groups spanning at most two seconds.
Matching role/start/hardware headers associate a machine's logs and snapshots; indistinguishable headers cannot
identify separate machines. Keep the original session headers. Local-clock captures use seconds within that machine/session, including `sid:null` solo files. Missing session
time uses file order with a **session time unavailable** notice; **timeline not synced** applies to comparing
machines without a common host clock. One complete snapshot supplies the frames for each machine/moment; duplicate
marks and report/log copies are deduplicated, while distinct rows with equal rounded timestamps remain frames. Without marks/reports, the page uses the end of the log.
Play-log hitch records are rate limited; sampled interval counts and cumulative network counters are labelled
separately from per-frame evidence. The synthetic fixtures have a host and two clients, one GC-stutter moment and
one slow-network moment. `fixtures/telemetry-solo/` trims the first real capture structure to a few rows with
synthetic headers; tests also create report zips and corrupt inputs in temporary folders.

## Other notes

- `TLD_TEST_MODS=CarTrailer,HeadUnit` also builds and deploys the example mods from `src/Mods`; tests otherwise remove
  them from the game.
- `TLD_PLUGIN_DLL=/path/to/TLDRevamp.dll` deploys another build (an A/B against an older version built elsewhere).
- A few driving tests (`twocars.py`, `shiftdrive.py --insane`, `autopilotprobe.py`) use Killenger's TLDAutopilot, a
  third-party mod that is not in this repository: put its DLL in `thirdparty/TLDLoaderMods/` (gitignored).
- `gamediff.py` tells you what changed after a game update. It decompiles the game's DLL into `TLD_CACHE` (never the
  repo) and lists the changed methods the mod patches or calls. See [docs/CONTRIBUTING.md](../docs/CONTRIBUTING.md)
  for decompiling the game to read its code.
- Many scripts' comments mention dates and builds: they were written while chasing a specific bug, and the notes say
  what that bug looked like.
- Results and screenshots go to `bench/` (gitignored).
- `tools/modscan` audits an existing TLDLoader DLL without running it or starting the game. Its Markdown report lists
  lifecycle overrides, dependencies, type-load failures, resolved/unresolved member references and the multiplayer
  changes the mod needs (docs/MODDING-MULTIPLAYER.md §7).
- `tools/terrainhorizon.py` writes a read-only bridge snapshot of near/distant terrain, far meshes, camera clipping and
  road coverage around a global point (or the player). `--compare before.json after.json` summarises coverage changes.
- `tools/cinematic.py check` drives the cinematic camera (issue #36) through the bridge and proves, from the game's
  own values, that the gameplay cameras and the player did not move while the camera was on, that the speed, the
  field of view and the orbit radius are clamped, and that the cursor is given back. `cinematic.py shot <file>` loads
  a shot list; the canonical form is written by the mod itself (`cinema shot write`), so the file and the take
  cannot drift apart.
- `tools/cinematictwomachine.py` is the two-machine check for the same camera: the host turns its camera on, moves it
  and plays a shot list while the test watches the host's own traffic, the position the joiner sees the host at, and
  the anomaly counters on both machines. It also checks that a joiner under the default host-only policy may read the
  camera's status but may not move a camera.
- `tools/quest3vr.py probe` checks ADB, a Quest 3 and local WiVRn/OpenXR registration without changing device settings.
  `run` exercises a timed OpenXR session through the running game's bridge and saves health, controller, frame and error
  samples plus an ADB screenshot; a short human comfort/visual check still follows. The game must be running with the
  bridge on (`Bridge = true` in `BepInEx/config/tldrevamp.core.cfg`): the tool drives the `openxr` bridge command, and
  the bridge is off in a player's install. The session is always stopped when the run ends, whatever happened.
- The bridge's XR commands (issue #41): `openxr` reads the XR loader's state (a read, so it works even when the
  session denies developer access); `openxr start`, `openxr retry`, `openxr stop` and `openxr recenter` change the
  session and need developer access. `openxr recenter origin` moves the runtime's tracking origin as well, which the
  game does not do on its own. Nothing starts a session behind the player's back: a start is always explicit, and a
  failed start is retried only when it is asked for. See [docs/VR-REWRITE.md](../docs/VR-REWRITE.md) §6.
- `tools/overview` is a **native** terminal overview (C11, the standard library only, no Python at run time). It shows
  the free-worker fleet: the agent processes and their workspaces, the TLD/Proton copies running here and on the second
  machine (with their Proton prefixes and whether they run in the background), the effective bridge state against the
  low-load graphics policy (quality, frame cap, vsync, resolution), the Steam host/client/relay/session phase read out
  of the worker logs, the result reports, the git status and the machine's load, memory and disk. It only reads: `/proc`
  (directly, no `ps`/`pgrep`), the worker logs, the result files and the read-only debug bridge. It never launches,
  stops or configures a game, and it never prints a credential or an environment variable. Build and run it:

  ```sh
  make -C tools                 # builds tools/overview and tools/tld-admit
  tools/overview                # the TUI: alternate screen, 2 s poll, q or Ctrl-C to quit
  tools/overview --once         # one text snapshot (no terminal needed)
  tools/overview --no-remote    # do not probe the second machine over SSH
  tools/overview --measure      # the idle CPU and RSS over 30 s
  make -C tools test            # the built-in self-test
  sh tools/overview_smoke.sh    # the end-to-end smoke test
  ```

  The admission/memory guard reads `/run/user/1000/tld-admission/state.json`, falling back to
  `status.txt`, `state.txt` or `status`. It shows the broker's state/reason, active job, profile, limits,
  elapsed time and published exit/OOM result, including publication age and whether limits were verified. Broker memory readings are labelled as a snapshot;
  live readings come from `/proc/meminfo` and the broker's watched Hyprland session cgroup under
  `/sys/fs/cgroup/user.slice/user-1000.slice/user@1000.service/session.slice/wayland-wm@hyprland.desktop.service`.
  Anonymous memory, file memory and swap are separate; cgroup v2 swap comes from `memory.swap.current`.
  Global file cache uses `Cached` (including shared memory), and PSI shows memory stall percentages
  averaged over ten seconds. The top eight processes are ranked by RSS, which can count shared pages repeatedly.
  New external OOM events are the live watched `memory.events` `oom_kill` counter minus the broker's
  `oom-baseline` (the count at the last healthy recovery); a missing reading or reset counter shows `?`.
  Observed counts are deduplicated separately in `oom-last-seen`, so a repeated status poll cannot
  restart the OOM cooldown or erase the visible event delta.

  Steam and WiVRn server presence come from process names. ADB reports device state counts through the
  existing local server on port 5037; it never invokes `adb` or starts its daemon, and omits serial numbers.
  When a ready Quest 3 is attached, it also reports the model, battery percentage, charging source and
  status, weak-charger flag, rounded battery temperature, and whether reverse forwarding for the WiVRn
  bridge port (`tcp:9757`) is present. These reads use ADB's read-only device transport and `dumpsys battery`;
  they do not change device or forwarding state. Malformed or out-of-range readings remain unavailable.
  OpenXR shows the selected manifest's runtime name and, when the local game bridge answers, its read-only
  `openxr` session state. No environment values or manifest paths are displayed. Bounded `hyprctl -j clients`
  and `hyprctl -j monitors` queries show TLD windows and whether any monitor exposes `special:tld-tests`.
  SSH uses no persistent control socket, forwarding, host-key update or local command.
  Failed helpers show unavailable readings without printing diagnostics. External labels are filtered for
  credential assignments and terminal controls. The overview never calls the admission broker or systemd.

  The **HEADSET** section reports the Quest / WiVRn state. It is read over the ADB server that is already
  running on the loopback interface: `tools/overview_headset.c` speaks the ADB server protocol itself, so
  `adb` is never executed and the daemon can never be started from here. The requests are fixed and
  whitelisted — the device listing, `getprop ro.product.model`, `dumpsys battery`, `dumpsys power`,
  `pm list packages org.meumeu.wivrn` and the `reverse:list-forward` device service. Exactly one authorized device
  is selected; with none, with several, with an unauthorized or an offline device, the section says so and
  asks the device nothing. WiVRn ships under two package ids — the Play Store build `org.meumeu.wivrn` and
  the GitHub build `org.meumeu.wivrn.github` — and the package manager filters by substring, so the one
  fixed query `pm list packages org.meumeu.wivrn` finds either of them. The answer is then matched against
  those two exact ids, so another app that happens to match the filter is never reported as the WiVRn
  client. An empty answer is a successful query with no match and is reported as genuinely absent; a
  query that failed leaves the state unknown rather than claiming an absence. It shows the model, the battery percentage, the charging and USB state, the weak
  charger warning, the temperature, the wake state, whether the `tcp:9757` reverse mapping is there, the
  WiVRn client app and the installed against the selected OpenXR runtime. The device serial is used on the
  wire and dropped there: it is never printed, never stored in the frame and never part of an error message,
  and no device path, environment value or manifest path is shown. Every probe is bounded in time and in
  bytes and cached; a failed probe leaves an explicit `unknown` instead of a guess.

  The headset checks are hermetic. `tools/overview_headset_test.c` answers from fixture text and a fixture
  transport, so the self-test opens no socket and contacts no ADB server or systemd unit; it covers the
  absent, two-device, unauthorized, offline, malformed-battery, several-reverse-mappings, no-runtime and
  privacy cases. `make -C tools test` runs it next to the overview's own self-test.

  The admission broker is built as `tools/tld-admit`. Its commands are:

  ```sh
  tools/tld-admit run --profile agent|test|game|vr --name NAME -- COMMAND...
  tools/tld-admit status [--json]
  tools/tld-admit pause [reason]
  tools/tld-admit resume
  tools/tld-admit limits
  ```

  Every admitted command runs in one transient `tld-job-*` user unit and the broker rejects a second
  controller or unit, including a unit still deactivating. A single user-wide kernel `flock` is held by
  the calling controller and a separate waiter through launch, execution and final state publication.
  The waiter survives controller death; the active-unit check remains a second barrier if wrappers die.
  Stale files cannot own a lock, and recovery never unlinks the lock inode. Private fixture state
  directories share this same admission lock. Agent and test jobs use `MemoryHigh=1536M`, `MemoryMax=2G`,
  `MemorySwapMax=1G`, `TasksMax=256`, `CPUQuota=200%`, `MemoryOOMGroup=yes`, `CPUWeight=50`,
  `IOWeight=50` and `Nice=10`. Game jobs use `MemoryHigh=8G`, `MemoryMax=10G`, `MemorySwapMax=2G`,
  `TasksMax=512` and `CPUQuota=300%`; explicit VR jobs use `MemoryHigh=10G`, `MemoryMax=12G`,
  `MemorySwapMax=4G`, `TasksMax=768` and `CPUQuota=300%`. Game/VR also use `MemoryOOMGroup=yes`.
  Launch reserves include the entire RAM and swap caps (zram can compress poorly): agent/test need
  9 GiB available, game 12 GiB, VR 26 GiB; swap free must exceed each cap by at least 4 GiB. Admission exits 75 when paused or
  unavailable, 64 for invalid arguments, and otherwise returns the child's exit code. Paused admission
  does not stop an already-running job. A manual pause remains until `resume`; pressure pauses clear
  after 60 continuous healthy seconds. A limited, transient `tld-admission-watch-*.service` samples
  pressure once a second while pending PSI or an automatic pause exists, including when no job or CLI is running,
  and exits after recovery. It is separate from the admitted `tld-job-*` unit and uses 128 MiB maximum
  memory, no swap, 32 tasks, 10% CPU and reduced CPU/I/O priority. Sampling gaps over three seconds,
  unknown gauges, another threshold crossing or another OOM event restart the interval. Manual pause
  takes precedence and never expires; explicit resume still obeys automatic pressure/cooldown.
  Immediate gates use `MemAvailable < 6 GiB`, remaining swap below max(2 GiB, 5%), severe memory PSI,
  compound low-memory/low-swap/session-working-set pressure, and global or session OOM-kill counter
  changes. Moderate global/session some/full PSI holds entry during a five-second debounce; sustained
  stalls latch a cooldown. Recovery requires 60 continuous seconds at >=8 GiB available, remaining
  swap >=max(4 GiB, 10%), some PSI <5% and full PSI <1%. Raw swap used or session anon alone cannot pause.
  Session anon/file/shmem and swap, both PSI sources, and both OOM counters are mandatory and fail closed.
  See [ADMISSION-POLICY.md](ADMISSION-POLICY.md) for exact boundaries, profile rationale and containment
  gates, and [BTRFS-SWAP-PROCEDURE.md](BTRFS-SWAP-PROCEDURE.md) for the unexecuted disk tier procedure.
  State is atomically published in
  `/run/user/1000/tld-admission/state.json` and `status.txt` (each file uses an atomic rename; JSON is
  authoritative). Publication failures prevent launch. Unknown readings are `-1`/`null`, not healthy zero;
  zero available memory is a known pressure reading. Profile values are verified against systemd and
  cgroup controls before executing the child. `OOMPolicy=kill` sets `memory.oom.group=1`, which is read
  back directly. A watched-session OOM pauses admission; only job-cgroup counters or systemd's
  `Result=oom-kill` label the job as OOM-killed. Exit 137 or a core dump alone is insufficient evidence.
  Commands use argv with `--expand-environment=no`; no shell, `--fdname`, child argv or environment dump
  enters state. The controller preserves the working directory and the user manager supplies the
  command environment. Failed/completed units are cleaned by exact name only.

  `make -C tools test` runs the overview's synthetic parser/rendering checks. `make -C tools smoke` also
  checks the panels, fixture Hyprland collection, helper failures and read-only behavior. Broker tests use
  the separate `make -C tools test_admit` target because they exercise live systemd units. The boundary
  suite needs Python 3 and a working user manager, uses private pressure/OOM fixtures, allocates at most
  16 MiB in its sample child, and spends about 60 seconds checking real automatic recovery. It tests
  manual/automatic precedence, repeated crossings, unknown gauges, failed publication, concurrent
  admission, stale recovery, controller/waiter death, literal argv and exact exit/signal propagation.
  `TLD_ADMIT_RUNTIME_DIR`, `TLD_ADMIT_MEMINFO`, `TLD_ADMIT_WATCHED_CGROUP`, `TLD_ADMIT_PSI` and
  `TLD_ADMIT_VMSTAT` select private test fixtures;
  the test-only `TLD_ADMIT_TEST_NO_WATCH` suppresses background sampling only when both pressure fixture
  overrides are set. Do not use pressure overrides for ordinary jobs. The emergency
  Hyprland barrier is `tools/tld-admission-hyprland.conf`; it is deliberately prepared but not installed.

  The expensive probes (the bridge, SSH, Steam, Hyprland and ADB) are cached for 30 s by default; the other panels refresh every poll.
  Each panel degrades on its own: a probe that fails shows its own error and leaves the other panels alone.
- Without the game: `python3 -m unittest tools/test_tools.py` tests the tools' own helpers, and
  `tools/privacy_scan.py` checks the repository for personal paths, LAN addresses, Steam IDs, keys, binaries and
  decompiled game code. CI runs both on every pull request, with the network reader's unit test
  (`dotnet run --project tools/unit/NetReaderTest.csproj`).
  The mod browser's parser, URL allowlist, trust/ZIP/size checks and install/update/remove operations also run without
  the game: `dotnet run --project tools/unit/ModCatalogTest.csproj`. It links the production plain C# functions,
  uses synthetic bytes and temporary folders, and never downloads or loads a mod.

## Headless mode is retired

`devserver.py`, `leasepersist.py`, `servermode.py`, `serveradmin.py` and `headless.py` used to start the game
with `-batchmode -nographics`. The guarded launcher owns the display arguments and refuses them, so those scripts
could only quit the running game first and then fail at the launch: they could kill the current session and still
not work. They are retired. Each one now **refuses with a non-zero exit and does nothing else** -- no import of
`game`/`tldenv`/`bench`, no process, no network, no file and no game action -- and its message names the supported
workflow below. Nothing is started automatically.

Use the guarded GUI workflow instead: `tools/game.py launch` (hidden and unfocused), `tools/game.py newgame`,
`tools/game.py quit`, and two windowed copies for multiplayer (never `-batchmode`; see
[docs/REPRO-FORMAT.md](../docs/REPRO-FORMAT.md)). The mod's own dedicated-server mode has no supported launch path
yet: it would need a separately proven route, so it is not started from here.
