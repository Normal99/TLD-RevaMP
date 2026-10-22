# TLD RevaMP for developers

The technical reference for TLD RevaMP: how the mod is installed, what it does to the game, how it is built and
released, and how the multiplayer protocol works. For a plain-language introduction and player instructions, see
the [README](../README.md). For how to contribute a change, see [CONTRIBUTING.md](CONTRIBUTING.md). For the test
tools that build, launch and drive the real game, see [tools/README.md](../tools/README.md).

**What is built, what is tested and what is still missing** is in
[PROJECT-STATUS.md](PROJECT-STATUS.md). The evidence behind every claim there lives in the durable
worker root, `${XDG_STATE_HOME:-$HOME/.local/state}/tld-revamp/workers/` (the reports in `workers/results/`, the
integration log in `workers/results/integrate.md`, and the closeout status in
`workers/context/closeout-20261021/STATUS.md`). The check entrypoint is `tools/verify.sh`.

## What RevaMP is

TLD RevaMP is a BepInEx 5 mod for **The Long Drive** (Steam, **beta branch**). It does two things:

- **Performance fixes.** Mirror refresh throttling, tiny distant part culling, grass camera throttling, sun shadow
  cascade reduction, and other trade-offs that are measured in game. Each has a default that was checked in game and
  chosen so the game looks the same as the original during normal play. The player-facing list of these, with the
  measured effect of each, is the **Settings → Revamp** tab (`src/TLDRevamp/RevampSettings.cs`), which is built by
  cloning the game's own *Misc* settings tab so it looks and behaves like a vanilla tab.
- **Multiplayer.** A from-scratch, seamless multiplayer implementation (`src/TLDRevamp/Net/`). The host's machine owns
  the world and the save; every machine simulates the objects near its own player.

The repository contains only the mod's code, never game files.

## Installation, in detail

### The two installer scripts

`installer/install.sh` (Linux, Steam Deck) and `installer/install.ps1` (Windows) are the player-facing installers. They
are plain text and are meant to be read before they are run. Both support the same operations:

| Operation | `install.sh` | `install.ps1` |
| --- | --- | --- |
| Menu (install / update, repair, uninstall, quit) | default | default |
| Install or update without the menu | `--install` | `-Install` |
| Repair without the menu | `--repair` | `-Repair` |
| Uninstall without the menu | `--uninstall` | `-Uninstall` |
| Game folder, if Steam's own list does not find it | `--game-dir DIR` | `-GameDir DIR` |
| Proton prefix, if it is not next to the game | `--prefix DIR` | — |
| Use zips already downloaded instead of downloading | `--from DIR` | `-From DIR` |
| Don't ask "Go ahead?" | `--yes` (needs one of the three actions) | `-Yes` (needs one of the three switches) |
| Print the usage and exit | `--help` | — |

What an install does, in order (each step is printed as it happens):

1. **Finds the game.** `install.sh` reads Steam's own list of library folders
   (`steamapps/libraryfolders.vdf`, the normal, Flatpak and Snap locations), then the library holding
   `steamapps/appmanifest_1017180.acf` (The Long Drive's Steam app id is `1017180`), and takes
   `steamapps/common/<installdir>` if it holds `TheLongDrive.exe`. `install.ps1` does the same through the registry
   keys Steam uses (`HKCU:\Software\Valve\Steam` and the two 64-bit variants). A folder given with `--game-dir` /
   `-GameDir` is used instead. Both refuse to continue if that folder has no `TheLongDrive.exe`, and the PowerShell
   script refuses to run while the game is still open.
2. **Downloads two files and checks them.** BepInEx 5.4.23.5 from
   [BepInEx's own release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) and the RevaMP zip from this
   repository's release. Each SHA-256 is written at the top of the script; a download that does not match stops the
   script before anything changes. `--from DIR` / `-From DIR` uses zips the user already downloaded, and they are
   still checked.
3. **Copies the files in.** All of BepInEx (`winhttp.dll`, `doorstop_config.ini`, `BepInEx/core`), plus from the
   RevaMP zip only `BepInEx/plugins/TLDRevamp` and the mod-action preloader `BepInEx/patchers/TLDRevamp`. A file it
   would replace that it did not put there itself is first copied to `RevaMP-installer/backup` in the game folder,
   and every file it adds is listed in `RevaMP-installer/installed-files.txt`. Nothing outside the game folder is
   touched except the game's own Proton prefix. No administrator or root rights are needed.
4. **On Linux, sets one Proton setting for this game only:** the line `"winhttp"="native,builtin"` in the game's own
   Proton prefix (`steamapps/compatdata/1017180/pfx/user.reg`, under `[Software\\Wine\\DllOverrides]`). This is the
   same thing as the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`, so the launch option is not needed.
   Proton prefers its built-in `winhttp`, and the mod needs the game folder's own copy to load. If the prefix does not
   exist yet (the game has never been started), the script says so and asks for the launch option instead. The
   override that was there before is remembered in `RevaMP-installer/proton-override` and put back on uninstall.

**Repair** compares every installed file against the release and replaces the ones that are missing or different,
clears `BepInEx/cache` (a stale cache can stop BepInEx loading plugins), and moves any stray second copy of
`TLDRevamp*.dll` found under `BepInEx/plugins` into the backup folder rather than deleting it. **Uninstall** removes
exactly the files in `installed-files.txt`, restores everything from the backup, restores the earlier Proton override
and removes the `RevaMP-installer` folder. RevaMP's own settings (`BepInEx/config`), logs and reports stay.

Each release's installer is pinned to that release. `tools/pin-installer.sh` rewrites the pinned block in both
scripts from a release zip, and checks that the BepInEx files inside the zip are byte-identical to the official
BepInEx release the scripts download. Update `BEPINEX_URL` and `BEPINEX_SHA256` in both scripts when BepInEx
changes.

### Manual installation

Unzip the release zip into the game folder so that `winhttp.dll` sits next to `TheLongDrive.exe`. On Linux and the
Steam Deck, also add the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%` unless the installer already set
the Proton override. `packaging/INSTALL.txt` ships in the zip with the same steps.

Remove any other mod loader (TLDLoader, ModLoader) from the game folder first. The installer is the recommended
route.

### Updating

Run the new release's installer and choose *Install or update*, or close the game and copy the new zip's files over
the old ones. Saves and settings stay. **Everyone in a session must run the same version.**

## The multiplayer, in detail

### Starting a session

The game's own **Multiplayer** button (pause menu and main menu) opens RevaMP's multiplayer screen instead of the
Steam lobby, and **F11** opens it too (`Plugin.KeyMpScreen`, `src/TLDRevamp/Net/MpScreen.cs`). The host's card offers
**Host** (a password is optional, and the host must have a world loaded or started); a friend's card offers **Join**.
**Join by address** connects by IP and port for LAN play and dedicated servers, with `27070` as the default port.

The transport is Steam Networking Sockets (`src/TLDRevamp/Net/SteamTransport.cs`): Steam P2P for friends, with NAT
traversal and relay, so no port forwarding is needed. The mod also listens on a UDP port (default `27070`) so a
join can be made by address.

A session holds at most 16 players, the host included (`Session.MaxPlayers`). The host refuses to start if the port
is already held by another program, because Steam would otherwise host on it anyway and joiners would reach the
other program (`JoinChecks.PortTaken`).

### Joining

Joining loads the host's world and never touches the joiner's own saves. The host's machine owns the save.

While the join loads, a muted loading screen covers the game and shows each stage and the object counts, until the
host's world and the nearby terrain, roads, ground and shared objects are really there
(`src/TLDRevamp/Net/JoinLoading.cs`). **Escape** cancels the join. After 60 seconds without progress the screen offers
*Continue anyway* as well as *Cancel*. The screen is on by default and can be turned off under
**Settings → Revamp → Multiplayer → Loading screen when joining**.

### The join checks

A join is refused, with the reason shown in the multiplayer screen, when:

| Reason | Message |
| --- | --- |
| Different mod versions | "The host plays TLD Revamp X, you Y. Both need the same version." |
| Same version, different code | "… (network protocol N), you … (protocol M): different builds of the same version. Both need the same build." |
| Unreadable request | "The host couldn't read your join request. … both need the same build." |
| Different multiplayer-API mods | the mod list is described and compared |
| Wrong password | "This game has a password." / "Wrong password." |
| Kicked before | "You were kicked from this game." |
| Full | "The game is full (16 players)." |

The current wire protocol is **54** (`src/TLDRevamp/Net/Protocol.cs`). A different *game* build is not a refusal: the
host announces it in chat instead, because the worlds can differ (buildings) but the session still works.

### What is shared

Players (on foot, seated, driving, as passengers, footsteps and other player sounds), cars (driving, crashes,
damage, parts coming off, fuel and fluids), loose items (carry, throw, drop), doors and gates of buildings, creatures
(hunting, attacks, shots, deaths, bodies), guns, bullet holes and explosions, sandstorms, tumbleweeds and the radio.
Players can stand in a driving bus or on a car.

Players can be anywhere on the map: each machine simulates the cars, items and creatures near its own player. The
protocol, the interest rules and the byte projections are in [MULTIPLAYER-ARCHITECTURE.md](MULTIPLAYER-ARCHITECTURE.md),
[SYNC-CONTROLS.md](SYNC-CONTROLS.md) and [INTEREST.md](INTEREST.md).

### Chat, voice and the host's tools

- **Text chat**: Enter (or the game's own multiplayer chat key).
- **Proximity voice**: hold the game's *Voice chat* key (K if it was never bound; the microphone is the one chosen in
  Steam). Players hear you within 60 m. The mode and volume are under **Settings → Revamp → Multiplayer**.
- The host can **mute** and **kick** players.
- **Teleport requests** (`/tpa <name>`, answered with `/y` or `/n`) are on by default with a 5-minute wait per
  player, and are set under **Settings → Revamp → Multiplayer → Teleport requests (when you host)**.
- **Session settings** (join placement, teleport requests, and the developer policy) are set by the host and shown to
  joiners as read-only.

## The approved-mod browser

**Settings → Revamp → Mods** opens the browser (`src/TLDRevamp/Net/ModsScreen.cs`, built from
`src/TLDRevamp/RevampMenu.cs`). Selecting a mod shows its author, description, source, download size, installed
version and approval state. **Install** or **Update** downloads one mod at a time; **Cancel** (or Back) stops the
download. **Disable / Enable** keeps a managed mod installed and changes whether it loads at the next start.
**Remove** also works while disabled and preserves the DLL as `.removed`. Restart after changes. Updates keep
disabled mods disabled and preserve the old file as `.replaced`.

The catalog is fetched only when the Mods screen is opened or **Refresh** is pressed. Offline, the screen says the
catalog is unavailable and installed mods keep working. The default catalog is this repository's `packaging/mods.json`
on the `dev` branch (`ModCatalog.DefaultUrl`); override it with `[Mods] CatalogUrl` in
`BepInEx/config/tldrevamp.core.cfg`.

**The catalog cannot approve code.** A downloaded DLL must match both its catalog SHA-256 and the **local**
`packaging/approved-mods.txt` shipped with RevaMP, including the mod's id and version. Only GitHub release assets are
accepted (`ModCatalog.ReleaseUrl`), with bounded downloads and single-DLL ZIP validation. The newest locally approved
version is offered. A grey entry explains why it cannot be installed with this RevaMP release. Head Unit 0.2.0 and
Car Trailer 0.1.1 are approved by their original release DLL hashes.

Only single-file mods are supported. Browser installs use `BepInEx/plugins/TLDRevampMods`, like host transfers. Mods
installed by hand are marked **not managed** and must be updated or removed by hand; they must also be enabled or
disabled by hand. RevaMP's own DLLs and BepInEx core are protected.

Disabled files live in `BepInEx/disabled-mods/<id>/`, entirely outside the recursively scanned plugins tree. The
persistent intent lives separately in `BepInEx/disabled-mods/<id>.state`; a missing disabled DLL folder is reported
in the browser. Keep this folder when updating RevaMP, together with `BepInEx/patchers/TLDRevamp` (the preloader that
applies the queued moves before the plugins load).

The catalog format is described in [CONTRIBUTING.md](CONTRIBUTING.md).

## Reports, marks and the debug tools

**Page Down** writes one zip to `BepInEx/tldrevamp-feedback/`, named
`TLDRevamp-report-<date>-<time>.zip`. It contains `stats.json`, `blackbox.json`, `breakdown.json`, `errors.json`,
`fixes.json`, the numeric settings and the play log. Screenshots, raw logs and configs, player identities, paths and
local clock details are excluded, and the report note is privacy-cleaned (`src/TLDRevamp/TelemetryPrivacy.cs`).

**Scroll Lock** opens marks: Enter saves an optional note (up to 500 cleaned characters), Escape cancels. Notes can be
browsed and your own notes renamed in the panel. Each dated session folder under `marks/` contains local and received
captures and a compact automatic comparison page; `marks/index.json` and `index.txt` keep the full notes.

*Settings → Revamp → Debugging → Share my marks and reports with the players in this session* (off by default) exchanges
new captures with the other sharing players through the host. Received files identify senders by session player index
only. **Do not attach report zips, marks or play logs to a GitHub issue** (issues are public) or upload them anywhere.
Describe the bug and when it happened; the developers will ask for the files privately if needed.

Keys: **F11** multiplayer menu, **Page Up** overlay, **Page Down** report, **Insert** item traffic, **Scroll Lock**
marks, **Ctrl+Insert** repro recorder. None of them is a key the game uses (its developer keys F1–F10, Home and End
stay the game's). Change them in `BepInEx/config/tldrevamp.core.cfg`, section `[Keys]`. The ownership, ghost, link,
comparison and event-feed shortcuts are unbound by default.

**Debug tools** (Settings → Revamp → Debugging) include ownership/sync labels, last-received owner ghost boxes and
attachment/lock/rider link lines, all off by default. The same section has live partner comparison and a focused object
event feed, both off by default, plus local network-condition presets and latency/jitter/loss/duplication/reorder
controls with a persistent on-screen warning. The repro recorder saves numeric multiplayer actions and poses locally
for two-copy bridge replay. See [TELEMETRY.md](TELEMETRY.md) for the limits, the interpretation, the measured costs and
the bridge controls (`net ownership|ghost|ghostall|links on|off|state`), and [REPRO-FORMAT.md](REPRO-FORMAT.md) for the
recording format and the runner.

## Building from source

Requirements: .NET SDK 8+, the game (beta) with BepInEx 5.4.23 installed.

```sh
tools/libs.sh          # copies the game's and BepInEx's reference assemblies into lib/ (never committed)
tools/package.sh       # builds and writes dist/TLD-RevaMP-<version>.zip
```

`tools/libs.sh` takes the game from `$TLD_DIR`, or Steam's default path. `tools/package.sh` builds the mod, checks
that the DLL's version string matches `src/TLDRevamp/Plugin.cs`, copies BepInEx and the mod's plugin folder into the
zip, writes `INSTALL.txt` with the version, ships the third-party licence texts, and refuses to build a zip that
contains configs, logs, third-party mods, or personal strings in the DLLs.

Other build and release steps:

| Command | What it does |
| --- | --- |
| `tools/build.sh` | builds and copies the DLLs into the game's `BepInEx/plugins/TLDRevamp/`, turning the debug bridge on |
| `tools/package-mods.sh` | builds the example mod zips into `dist/` and prints their SHA-256 approval lines |
| `tools/pin-installer.sh [zip]` | pins both installer scripts to a release zip and checks the BepInEx bytes |
| `tools/devrelease.py --dry-run` | builds the checked-out commit and zips it into `dist/`, nothing uploaded |
| `tools/devrelease.py` | the same, uploaded as a draft GitHub release |
| `tools/devrelease.py --public` | the same, uploaded as a public pre-release |

A dev build is a **draft** release by default: only people with write access see and download it, and its tag
`v<version>-dev.<commit>` only appears if someone publishes it. The installers stay pinned to their release; a dev
build is installed from its zip. In game it shows as e.g. `0.69.1-dev.c8d9c55`, and multiplayer only lets that exact
build join.

## Release model

- **Releases** are published from `main` as a GitHub release with the version tag `v<version>`. The assets are the
  mod zip (`TLD-RevaMP-<version>.zip`), the two installer scripts, a `SHA256SUMS` file, and the example mod zips.
  The installers pinned to that release are committed in the same change.
- **Pre-releases** come from `dev`. The version in the tag says so (e.g. `v0.69.1 (dev build)`), and a pre-release
  is never marked as the latest release.
- **Dev builds** are drafts or public pre-releases from `tools/devrelease.py`, tagged `v<version>-dev.<commit>`.
- The wire protocol number changes when the protocol changes, and it is part of the join check: two builds of the
  same version with different protocol numbers do not join.
- Everyone in a session must run the same version and the same build.

## Credits and licensing

Bundled unmodified in the release zip, each under its own licence (texts in [packaging/licenses](../packaging/licenses),
shipped as `BepInEx/licenses`): BepInEx 5 (MIT), HarmonyX (MIT) and UnityDoorstop (`winhttp.dll`, LGPL-2.1). The Long
Drive by Gab and Genesz. Also special thanks to Killenger for his TLDAutopilot mod used in testing.

The parked-trailer creep fix, trailer brakes and the optional driving assists (trailer sway reduction, roll
correction, traction control) follow ideas from Long Haul Overhaul by QuantumRage & Wisher (LGPL-3.0); they are
written anew for TLD RevaMP, no code from it is included.

Copyright (c) 2026 Normal99. TLD RevaMP is free software under the **GNU General Public License, version 3**
(GPL-3.0): see [COPYING](../COPYING). Anyone who shares TLD RevaMP, changed or not, shares its source under the same
licence. That includes other mods: a mod that uses TLD RevaMP's code, such as the multiplayer mod API in
`TLDRevamp.Multiplayer.dll`, must be released under GPL-3.0 or a GPL-3.0-compatible licence (see
[MODDING-MULTIPLAYER.md](MODDING-MULTIPLAYER.md)). The Long Drive itself, BepInEx, HarmonyX and UnityDoorstop keep
their own licences; none of their code is in this repository.
