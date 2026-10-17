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
**Cancel** (or Back) stops the download. **Remove** renames a managed DLL to `.removed`. After a change, restart the
game to load it or finish removing it. Updates keep the old file as `.replaced`.

The catalog is fetched only when opening Mods or pressing **Refresh**. Offline, the screen says the catalog is
unavailable; installed mods keep working. The default is this repository's `packaging/mods.json` on `main`; override
`[Mods] CatalogUrl` in `BepInEx/config/tldrevamp.core.cfg` if needed.

The catalog cannot approve code. A downloaded DLL must match both its catalog SHA-256 and the **local**
`approved-mods.txt` shipped with RevaMP, including the mod's id and version. Only GitHub release assets are accepted,
with bounded downloads and single-DLL ZIP validation. The newest locally approved version is offered. A grey entry
explains why it cannot be installed with this RevaMP release. CarTrailer 0.1.1 is listed but is not approved yet.

Only single-file mods are supported. Browser installs use `BepInEx/plugins/TLDRevampMods`, like host transfers.
Mods installed by hand are marked **not managed** and must be updated or removed by hand.

## Example mods

The source of the example mods is in [src/Mods](src/Mods/README.md): **Head Unit** (a digital head unit with a
YouTube queue everyone in the car shares, built on the multiplayer mod API), **Car Trailer** (a car trailer RevaMP
shares with no multiplayer code of its own) and **API Probe** (a small test mod for the API). Each has a README on
what it shows and how to build it. Head Unit and Car Trailer are attached to each release as their own zips.

## Updating

Run the new release's installer and choose *Install or update*, or close the game and copy the new zip's files over
the old ones. Saves and settings stay. Everyone in a session needs
the same version.

## Reporting problems

Press **Page Down** when something goes wrong. It writes one zip to
`BepInEx/tldrevamp-feedback/` with a screenshot, the logs of this run and the previous one, your hardware, and the
multiplayer telemetry of your recent sessions. Attach it to a [GitHub issue](../../issues) or send it to the developer.
In someone else's game, *Settings → Revamp → Debugging → Send my reports to the host* (off by default) sends the host a
copy, and a host's Page Down then collects everyone's report from the same moment.

Keys: **F11** multiplayer menu, **Page Up** overlay, **Page Down** report — none of the game's own keys; change them in
`BepInEx/config/tldrevamp.core.cfg`.

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

BepInEx and HarmonyX (bundled unmodified in the release zip, LGPL-2.1 / MIT). The Long Drive by Gab and Genesz. Also special thanks to Killenger for his TLDAutopilot mod used in testing.

The parked-trailer creep fix, trailer brakes and the optional driving assists (trailer sway reduction, roll correction, traction control) follow ideas from Long Haul Overhaul by QuantumRage & Wisher (LGPL-3.0); they are written anew for TLD Revamp, no code from it is included.

## License

Copyright (c) 2026 Normal99.

TLD RevaMP is free software under the **GNU General Public License, version 3** (GPL-3.0): see [COPYING](COPYING).
Anyone who shares TLD RevaMP, changed or not, shares its source under the same licence. That includes other mods:
a mod that uses TLD RevaMP's code, such as the multiplayer mod API in `TLDRevamp.Multiplayer.dll`, must be released
under GPL-3.0 or a GPL-3.0-compatible licence (see [docs/MODDING-MULTIPLAYER.md](docs/MODDING-MULTIPLAYER.md)).
The Long Drive itself, BepInEx and HarmonyX keep their own licences; none of their code is in this repository.
