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
tools/devrelease.py --dry-run         # build the checked-out commit and zip it to dist/, nothing published
tools/devrelease.py                   # the same, then publish it as a GitHub pre-release (gh signed in)
```

A dev build lets testers try `dev` without building it. Each one is its own pre-release, tagged
`v<version>-dev.<commit>` and never marked latest. The installers stay pinned to the official release; a dev build is
installed from its zip. In game it shows as e.g. `0.69.0-dev.c8d9c55`, and multiplayer only lets that exact build
join (BepInEx itself sees the plain `0.69.0`: it skips plugins whose version isn't plain numbers). The zip has the
release layout and private-string check of `package.sh`, in Python so it also runs on Windows (Git Bash has no
`zip`/`strings`). It needs a clean checkout whose commit is pushed, and the game with BepInEx 5 in `TLD_DIR`.

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
- Without the game: `python3 -m unittest tools/test_tools.py` tests the tools' own helpers, and
  `tools/privacy_scan.py` checks the repository for personal paths, LAN addresses, Steam IDs, keys, binaries and
  decompiled game code. CI runs both on every pull request, with the network reader's unit test
  (`dotnet run --project tools/unit/NetReaderTest.csproj`).
  The mod browser's parser, URL allowlist, trust/ZIP/size checks and install/update/remove operations also run without
  the game: `dotnet run --project tools/unit/ModCatalogTest.csproj`. It links the production plain C# functions,
  uses synthetic bytes and temporary folders, and never downloads or loads a mod.
