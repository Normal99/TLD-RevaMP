# TLD RevaMP

Performance fixes and a from-scratch, seamless multiplayer for **The Long Drive** (Steam, **beta branch**), as a
BepInEx 5 mod. You need your own copy of the game — this repository contains only the mod's code, never game files.

> **Test builds.** Expect bugs. Back up your saves before playing.

## Download and install

Get the latest zip from **[Releases](../../releases)** and follow the `INSTALL.txt` inside
(short version: unzip into the game folder; on Linux/Proton add the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`).
Everyone in a session needs the same version.

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

## Updating

Close the game and copy the new zip's files over the old ones. Saves and settings stay. Everyone in a session needs
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

## Credits

BepInEx and HarmonyX (bundled unmodified in the release zip, LGPL-2.1 / MIT). The Long Drive by Gab and Genesz. Also special thanks to Killenger for his TLDAutopilot mod used in testing.
