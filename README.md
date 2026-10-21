# TLD RevaMP

Performance fixes and a from-scratch, seamless multiplayer for **The Long Drive** (Steam, **beta branch**), as a
BepInEx 5 mod. You need your own copy of the game — this repository contains only the mod's code, never game files.

> **Test builds.** Expect bugs. Back up your saves before playing.

## Download and install

The easy way is the installer on the **[Releases](../../releases)** page: a short plain-text script you can read
before you run it.

- **Windows:** download `install.ps1`, right-click it and choose *Run with PowerShell*.
- **Linux / Steam Deck:** download `install.sh` and run `bash install.sh` in a terminal (on the Deck, in Desktop Mode).

It asks what you want to do: **install or update**, **repair** (checks every file against the release and fixes the
missing or changed ones), or **uninstall**. Then it shows what it will download and asks once more before it changes
anything.

To install by hand instead, get the zip from the same page and follow the `INSTALL.txt` inside
(short version: unzip into the game folder; on Linux/Proton add the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`).
Everyone in a session needs the same version.

### Before you run the installer

What it does, so you can check it against the script in [installer/](installer/):

- **Finds the game** through Steam's own list of library folders, or uses the folder you give it.
- **Downloads two files from GitHub:** BepInEx 5.4.23.5 from
  [BepInEx's own release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5), and the RevaMP zip from this
  repository's release. The SHA-256 of each is written at the top of the script. If a download doesn't match, it stops
  before changing anything. (The BepInEx files inside the RevaMP zip are the same bytes as BepInEx's release; the
  release tool `tools/pin-installer.sh` checks that.)
- **Changes only the game folder.** It copies BepInEx and RevaMP's plugin folder in. A file it would replace is first
  saved to `RevaMP-installer/backup` in the game folder, and every file it adds is listed in
  `RevaMP-installer/installed-files.txt`. Saves, settings and other games are not touched, and it needs no
  administrator or root rights.
- **On Linux it also sets one Proton setting**, for this game only: the line `"winhttp"="native,builtin"` in the
  game's own Proton prefix (`steamapps/compatdata/1017180/pfx/user.reg`). That does the same as the launch option, so
  you don't need to add it. If the game has never been started, the prefix doesn't exist yet: it then tells you to add
  the launch option, or to start the game once and run Repair.
- **Uninstall** removes exactly the listed files, puts the backup back and removes the Proton line. Your RevaMP
  settings (`BepInEx/config`), logs and reports stay.
- Each release's installer is pinned to that release. The top of each script lists its options, for example
  installing from zips you downloaded yourself (`--from` / `-From`).

## Playing together

- The game's own **Multiplayer** menu (pause menu or main menu, **F11** in game): *Host* (optionally with a password),
  or pick a Steam friend who is hosting and *Join*. No port forwarding (Steam relay).
- The host's machine owns the world and the save; joining loads the host's world and never touches your own saves.
- Joining shows loading stages and object counts, mutes game audio and voice, and holds you safely until nearby
  terrain, roads, ground and shared objects are ready. **Esc** cancels; after 60 seconds without progress you can
  continue anyway. **Settings > Revamp > Multiplayer > Loading screen when joining** is on by default.
- Players can be anywhere on the map — each machine simulates the cars, items and creatures near its own player.
- **Text chat** (Enter), **proximity voice** (hold the game's Voice chat key, K if unbound — Steam's microphone, heard
  within 60 m), mute, and kick for the host.

What is shared today: players (on foot, seated, driving, passengers, footsteps and other player sounds), cars
(driving, crashes, damage, parts coming off, fuel and fluids), loose items (carry, throw, drop), doors and gates of
buildings, creatures (hunting, attacks, shots, deaths, bodies), guns, bullet holes and explosions, sandstorms,
tumbleweeds, the radio. Players can stand in a driving bus or on a car.

## Approved mods

Open **Settings → Revamp → Mods** from the main menu or pause menu. Select a mod to see its author, description,
source, download size, installed version and approval state. **Install** or **Update** downloads one mod at a time;
**Cancel** (or Back) stops the download. **Disable / Enable** keeps a managed mod installed and changes whether it
loads at the next start. **Remove** also works while disabled and preserves the DLL as `.removed`. Restart after
changes; updates keep disabled mods disabled and preserve the old file as `.replaced`.

The catalog is fetched only when opening Mods or pressing **Refresh**. Offline, the screen says the catalog is
unavailable; installed mods keep working. The default is this repository's `packaging/mods.json` on `dev`; override
`[Mods] CatalogUrl` in `BepInEx/config/tldrevamp.core.cfg` if needed.

The catalog cannot approve code. A downloaded DLL must match both its catalog SHA-256 and the **local**
`approved-mods.txt` shipped with RevaMP, including the mod's id and version. Only GitHub release assets are accepted,
with bounded downloads and single-DLL ZIP validation. The newest locally approved version is offered. A grey entry
explains why it cannot be installed with this RevaMP release. Head Unit 0.2.0 and Car Trailer 0.1.1 are approved
by their original release DLL hashes.

Only single-file mods are supported. Browser installs use `BepInEx/plugins/TLDRevampMods`, like host transfers.
Mods installed by hand are marked **not managed** and must be updated or removed by hand.
They must also be enabled or disabled by hand; RevaMP's own DLLs and BepInEx core are protected.
The browser shows Enabled, Disabled (takes effect at next start), Disabled, or Enabled at next start.
Disabled files live in `BepInEx/disabled-mods/<id>/`, entirely outside the recursively scanned plugins tree.
Persistent intent lives separately in `BepInEx/disabled-mods/<id>.state`; a missing disabled DLL folder is reported
in the browser. Keep this folder when updating RevaMP, together with its `BepInEx/patchers/TLDRevamp` preloader.

## Example mods

The source of the example mods is in [src/Mods](src/Mods/README.md): **Head Unit** (a digital head unit with a
YouTube queue everyone in the car shares, built on the multiplayer mod API), **Car Trailer** (a car trailer RevaMP
shares with no multiplayer code of its own), **Trailer Hitches** (receiver hitches for compatible cars)
and **API Probe** (a small test mod for the API). Each has a README on
what it shows and how to build it. Head Unit, Car Trailer and Trailer Hitches are attached to each release as their own zips.

## Updating

Run the new release's installer and choose *Install or update*, or close the game and copy the new zip's files over
the old ones. Saves and settings stay. Everyone in a session needs
the same version.

## Reporting problems

Press **Page Down** when something goes wrong. It writes one zip to `BepInEx/tldrevamp-feedback/` with recent
game state and performance/network telemetry, numeric settings, hardware models and warning/error counts.
Screenshots, raw logs/configs, player identities, paths and local clock details are excluded; your report note is
privacy-cleaned. Keep report zips, marks and play logs on your PC: **do not attach them to a [GitHub issue](../../issues)
(issues are public), or upload them anywhere.** Describe the bug and when it happened; the developers will ask for the
files privately if needed. *Settings → Revamp → Debugging → Share my marks and reports with the players in this
session* (off by default) exchanges new captures with the other sharing players through the host; received files
identify senders by session player index only. **Scroll Lock** opens marks: Enter saves an optional note (up to
500 cleaned characters), Escape cancels. Browse notes and rename your own notes in the panel; sharing also relays edits.
Each dated session folder under `marks/` contains local/received captures and a compact automatic comparison page;
`marks/index.json` and `index.txt` retain full notes. Protocol **46** is required.

Keys: **F11** multiplayer menu, **Page Up** overlay, **Page Down** report, **Insert** item traffic, **Scroll Lock** marks —
none of the game's own keys; change them in `BepInEx/config/tldrevamp.core.cfg`.

**Debug tools:** Settings > Revamp > Debugging starts with **Debug view**: Off (default), Focus (one target
card), Problems (Focus plus faulty-object tags), Everything (all nearby diagnostics). The card follows the
crosshair, seated car, then held item; empty histories disappear. Partner comparison is optional and shows
differences by default. Bind `DebugView` in `tldrevamp.core.cfg` to cycle modes; it starts unbound. Existing keys
and bridge commands still work; `net debugview off|focus|problems|everything|state` selects a mode and
`net compare all|differences` changes comparison detail. See [Telemetry](docs/TELEMETRY.md#world-overlays) for
thresholds, costs and [partner comparison](docs/TELEMETRY.md#live-object-panels).
The same Debugging section includes local network-condition presets and latency/jitter/loss/duplication/reorder
controls, with a persistent on-screen warning while active. The repro recorder (Ctrl+Insert, configurable) saves
numeric multiplayer actions and poses locally for two-copy bridge replay. Both start off every game restart;
recordings exclude player identities, chat text and command arguments. See the [repro format and runner](docs/REPRO-FORMAT.md).

## Building from source

Requirements: .NET SDK 8+, the game (beta) with BepInEx 5.4.23 installed.

```sh
tools/libs.sh          # copies the game's and BepInEx's reference assemblies into lib/ (never committed)
tools/package.sh       # builds and writes dist/TLD-RevaMP-<version>.zip
```

## Contributing

See [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md). Before changing how the mod patches the game, decompile your own copy
of the game to read its code (ILSpy; the steps are there). The repository never contains game code. The test tools that
build, launch and drive the game (single machine, or two machines for multiplayer) are in [tools/](tools/README.md).

## Credits

Bundled unmodified in the release zip, each under its own licence (texts in [packaging/licenses](packaging/licenses), shipped as `BepInEx/licenses`): BepInEx 5 (MIT), HarmonyX (MIT) and UnityDoorstop (`winhttp.dll`, LGPL-2.1). The Long Drive by Gab and Genesz. Also special thanks to Killenger for his TLDAutopilot mod used in testing.

The parked-trailer creep fix, trailer brakes and the optional driving assists (trailer sway reduction, roll correction, traction control) follow ideas from Long Haul Overhaul by QuantumRage & Wisher (LGPL-3.0); they are written anew for TLD Revamp, no code from it is included.

## License

Copyright (c) 2026 Normal99.

TLD RevaMP is free software under the **GNU General Public License, version 3** (GPL-3.0): see [COPYING](COPYING).
Anyone who shares TLD RevaMP, changed or not, shares its source under the same licence. That includes other mods:
a mod that uses TLD RevaMP's code, such as the multiplayer mod API in `TLDRevamp.Multiplayer.dll`, must be released
under GPL-3.0 or a GPL-3.0-compatible licence (see [docs/MODDING-MULTIPLAYER.md](docs/MODDING-MULTIPLAYER.md)).
The Long Drive itself, BepInEx, HarmonyX and UnityDoorstop keep their own licences; none of their code is in this repository.
