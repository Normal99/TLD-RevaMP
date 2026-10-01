# TLD RevaMP

Performance fixes and a from-scratch, seamless multiplayer for **The Long Drive** (Steam, **beta branch**), as a
BepInEx 5 mod. You need your own copy of the game — this repository contains only the mod's code, never game files.

> **Test builds.** Expect bugs. Back up your saves before playing.

## Download and install

Get the latest zip from **[Releases](../../releases)** and follow the `INSTALL.txt` inside
(short version: unzip into the game folder; on Linux/Proton add the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`).
Everyone in a session needs the same version.

## Playing together

- **F7** opens the multiplayer panel: *Host*, or *Join* a Steam friend who is hosting. No port forwarding (Steam relay).
- The host's machine owns the world and the save; joining loads the host's world and never touches your own saves.
- Players can be anywhere on the map — each machine simulates the cars, items and creatures near its own player.

What is shared today: players (on foot, seated, driving, passengers), cars (driving, crashes, damage, parts coming
off, fuel and fluids), loose items (carry, throw, drop), doors and gates of buildings, creatures (hunting, attacks,
shots, deaths), guns, bullet holes and explosions. Not yet: voice (use Discord), weather/sandstorm sync is untested,
ragdolls after a kill are local to each machine.

## Reporting problems

Press **F9** when something goes wrong (or *Save report* in the F7 panel). It writes one zip to
`BepInEx/tldrevamp-feedback/` with a screenshot, the logs of this run and the previous one, your hardware, and the
multiplayer telemetry of your recent sessions. Attach it to a [GitHub issue](../../issues) or send it to the developer.

## Building from source

Requirements: .NET SDK 8+, the game (beta) with BepInEx 5.4.23 installed.

```sh
tools/libs.sh          # copies the game's and BepInEx's reference assemblies into lib/ (never committed)
tools/package.sh       # builds and writes dist/TLD-RevaMP-<version>.zip
```

## Credits

BepInEx and HarmonyX (bundled unmodified in the release zip, LGPL-2.1 / MIT). The Long Drive by Gábor Pintér.
