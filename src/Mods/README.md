# Example mods

Mods that show how other BepInEx mods work with TLD RevaMP's multiplayer. They are separate mods, not part of
RevaMP: each builds to its own DLL, and the playable ones ship as their own zips.

| Mod | What it shows | Uses the API |
| --- | --- | --- |
| [HeadUnit](HeadUnit/README.md) | A digital head unit for the radio slot with a YouTube queue everyone in the car shares | yes |
| [TvBrowser](TvBrowser/README.md) | A web browser on the game's TV, used with the keyboard; everyone sees the same page, and replays show it | yes |
| [CarTrailer](CarTrailer/README.md) | A car trailer that RevaMP shares like the game's own trailer, with no multiplayer code | no |
| [TrailerHitches](TrailerHitches/README.md) | Adds receiver hitches so cars such as car06p can accept the game's trailer drawbar | yes |
| [ApiProbe](ApiProbe/README.md) | A small test mod for state records, the far store and session settings (not shipped) | yes |

How the multiplayer mod API works, its limits, and what a mod gets without it: [docs/MODDING-MULTIPLAYER.md](../../docs/MODDING-MULTIPLAYER.md).

## Building

Same requirements as RevaMP itself (.NET SDK 8+, the game with BepInEx 5.4.23, `tools/libs.sh` run once so `lib/`
has the reference assemblies). Then, from the repository root:

```sh
dotnet build src/Mods/HeadUnit/HeadUnit.csproj -c Release      # -> src/Mods/HeadUnit/bin/Release/netstandard2.1/HeadUnit.dll
dotnet build src/Mods/TrailerHitches/TrailerHitches.csproj -c Release
dotnet build src/Mods/TvBrowser/TvBrowser.csproj -c Release
tools/package-mods.sh                                          # example mod zips in dist/
```

Put the DLL in `BepInEx/plugins/<Mod>/`. The mods that use the API reference `TLDRevamp.Multiplayer` as a project,
so building one builds that too; at run time they load without RevaMP as well (single player only).

The test tools deploy the example mods when a test names them: `TLD_TEST_MODS=CarTrailer,HeadUnit,TrailerHitches,TvBrowser`
(see [tools/README.md](../../tools/README.md)). The matching scripts under `tools/` exercise the mods in the game.

## Using them as a starting point

Copy a mod's folder, rename the project, the plugin GUID and the namespace, and change the mod id passed to
`MpApi.RegisterMod`. A mod that uses RevaMP's code, such as the multiplayer API, is released under GPL-3.0 or a
GPL-3.0-compatible licence, as these examples are.
