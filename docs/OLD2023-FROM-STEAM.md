# The old 2023 world from the player's own Steam copy

The "Old terrain (2023)" mode ([TERRAIN-MODES.md](TERRAIN-MODES.md)) runs **the 2023 build's own code**: the player
downloads the public build v2023.05.02d with their own Steam client, and RevaMP loads its `Assembly-CSharp.dll` beside
the beta's. The repository keeps only the old map's scene constants, the glue that puts the old world into the beta's,
and a SHA-256 of each file it needs. It never ships, commits, uploads or downloads the old code.

## What Steam has

From the Steam client's own app info for The Long Drive (app 1017180), read on the test desktop on 2026-10-23
(`appcache/appinfo.vdf`, no network):

| | |
| --- | --- |
| Default branch (`public`) | build 11140890, last updated 2023-05-02 21:30 UTC: the v2023.05.02d build |
| Content depot | **1017181** (Windows, 64-bit), shared by every branch |
| Its `public` manifest | **7731485613881054580** (529 MB installed, 338 MB to download for the whole depot) |
| `beta` manifest (what RevaMP runs on) | 2955431061162845146, build 16539689, Unity 2022.3.12f1 |
| Others | `old_multiplayer_test` (20230502d non-VR multiplayer), `v20230411c`, older builds, a 32-bit depot 1017182 |

## Getting it (the player, once)

Open Steam's console (start Steam with `steam -console`, or open `steam://open/console`) and run

```
download_depot 1017180 1017181 7731485613881054580
```

Steam downloads the whole depot (202 files, 321 MB) with the player's own login into
`<Steam>/steamapps/content/app_1017180/depot_1017181/` (the Linux client: `<Steam>/ubuntu12_32/steamapps/content/…`)
and prints `Depot download complete`. Then restart the game. Without the files the new-game screen greys the 2023
entry out and says the same in one line.

## What RevaMP does with it

`WorldGen/Old2023/OldBuild.cs`, `OldBuildFiles.cs`:

1. **At startup** it looks in `BepInEx/cache/TLDRevamp/old2023/`, then in the depot folder under every usual Steam
   folder: Proton's `STEAM_COMPAT_CLIENT_INSTALL_PATH`, the Steam folder the game is installed in, `~/.local/share/Steam`,
   `~/.steam/steam`, Flatpak and Snap Steam (through drive `Z:` under Proton, also for each `/home/*` when `HOME` doesn't
   reach the game), and `Program Files (x86)\Steam` on Windows. It needs three files of `TheLongDrive_Data/Managed`
   and checks each against the SHA-256 pinned in `OldBuildFiles.cs`:

   | File | SHA-256 | Why |
   | --- | --- | --- |
   | `Assembly-CSharp.dll` | `9919a9eeddcce23bb4ff86e890437f32b4a3ef3c10af2efe3b8d889d894dd62e` | the 2023 game code |
   | `Unity.Collections.dll` | `fedbd58da08818027c7a3ca4f735a412f54637689158d354dfb9728870ece5f7` | `TerrainGenerationJob`'s `NativeList` fields (the beta ships no Unity.Collections) |
   | `Unity.Mathematics.dll` | `4af4869ab6e59a7afd8c4e185c3ffe3c602fa7df7134e63672f75e6eef0721a0` | referenced by the job's other methods |

   Verified files are copied into the cache, so the player may delete the 321 MB download afterwards. Nothing is
   loaded yet.
2. **At the first old-terrain world** it rewrites a copy of the 2023 `Assembly-CSharp.dll` in memory with Mono.Cecil
   (already in `BepInEx/core`): the assembly and module become `Assembly-CSharp-2023` and every namespace gets an
   `Old2023` prefix (the global namespace becomes `Old2023`), so neither the game nor another mod's type lookup finds
   an old type by accident. It is loaded with `Assembly.Load(byte[])`. References to Unity's own assemblies bind to the
   beta's; Unity.Collections/Unity.Mathematics come from the cache. RevaMP is never compiled against the old build: it
   calls it through members resolved by name, the hot ones (`randomize`, `VertexColor`) through compiled delegates.
3. If anything fails (files changed, a member missing), the world is the game's own and the log says why: never a
   half-old world. The bridge's `terrain oldcode` shows where the files came from and whether the code is loaded.

## What runs from it

| Old code | Where RevaMP calls it | Stand-ins it reads |
| --- | --- | --- |
| `TerrainGenerationJob.randomize`, `VertexColor` (height and biome colour) | `OldTerrain.cs`: the beta's `terrainGenSettings.Height`/`Biome` and RevaMP's worker-thread copies | a job struct per thread, `seed` set |
| `roadGenScript.FStart`, `CalcOneRoad` (the road, 100 m at a time) | `OldRoad.cs`: the main road's dots and bones | the map's road generator, `TerrainGenerator.TG`, `mainscript.M` |
| `roadBuildingScript.FStart` → `GenAll` → `GenerateOne` (the roadside building plan) | `OldBuildings.cs`: the beta's POI chunks | the map's building generator |
| `TerrainGenerator.GetTerrainHeightWorld`, `mainscript.TurnAngle` | called by the old code itself | |

An old MonoBehaviour can't be a live Unity object (it isn't in any scene, and its assembly isn't a Unity script
assembly). It doesn't need to be: `OldScene.cs` makes **stand-ins** (`FormatterServices.GetUninitializedObject`: no
constructor, no Unity object behind them) and sets their fields to the old map's scene values. The old code reads a
few real Transforms (the road's frame, the main world), which belong to one hidden object, so the road and building
code runs on the main thread; the height and colour run on any thread.

Found on the way, with the old DLL:

- `TerrainGenerationJob` is a Burst job struct with `NativeList` fields: its type only loads with Unity.Collections
  present, hence the second file. Burst itself isn't needed (the methods run as plain Mono code).
- The road and building code touch `mainscript.M` (`seed`, `load`, `mainWorld`, `world`, `menu`, `DDS`) and
  `TerrainGenerator.TG` (`manager.settings.world2`, `tjob`, `roads`): all stand-ins. `buildings != null` and
  `Kaposztaleves != null` are Unity's null checks, false for a stand-in, so the old road never starts the old homesick
  houses or the old building UI.
- `GenerateOne` only plans; placing is the beta's own (`poiGenScript.AddPoi`).

What stays in the repository: the scene constants (road widths, angles and noise layers; the building groups with
their distances and sides), the glue that maps the beta's coordinates (old world 60 m east of the beta's origin; the
-z road, which the 2023 world didn't have, computed by the same code on the terrain turned by 180 degrees through a
Harmony prefix on the old `GetTerrainHeightWorld`, its points turned back), the old biome thresholds the beta asks
for with a colour alone, and the hooks into the beta's generators.

## Multiplayer

Terrain is generated on every machine. A joiner whose host plays the old terrain and who lacks the files is refused
with a message that says how to get them (`OldBuildFiles.JoinRefusal`), instead of building a different world. The
pinned hashes make every machine in a session run the same old code.

## Checked on the test desktop (2026-10-24)

One run on a test copy, seed 12345, with the port this replaced compiled in for the comparison only (never committed):

| | |
| --- | --- |
| Finding the files | 23 ms from the cache (first start: found under `Z:/home/…/ubuntu12_32/steamapps/content`, copied) |
| Rewrite and load | 122 ms at the first old-terrain world; the stand-in scene 15 ms |
| Heights (22,801 points over x ±20 km, z ±40 km) | 13,801 bit-equal to the port, the rest within 1.2 mm; colours all equal |
| The road, every 10 m over ±40 km (8,001 points) | within 5.3 mm of the port (the largest at z = -39,670, the -z road) |
| Roadside buildings | 1,228 planned: 612 on the 2023 road (+z), 616 on the -z road; types and rotations as the port's, 1,226 within 1 mm, the largest offset 5.7 mm |
| In the world | the start (60, 47.32, 0); 7 road pieces with 734 elements, 4 buildings queued and placed, 0 missing |
| Cost of one height | 7.0 µs (the old code makes a `System.Random` per call) against the port's 0.9 µs, on the main thread |

The millimetres are most likely float rounding, not a different world: the old code computes its noise offsets inline
in each call, where the port stored them once as floats (a float expression kept in a register can round differently).
The old code is what the 2023 game ran, so where the two differ the old code is the 2023 world.

With the files hidden (the download and the cache moved away): `terrain oldcode` reports `Missing`, the new-game
screen shows "Old terrain (2023)" greyed out with the one-line hint under the row, and `terrain select OldTerrain`
is refused with the same text.
