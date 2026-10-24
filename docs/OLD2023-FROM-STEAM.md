# The old 2023 world from the player's own Steam copy (design)

Status: design only, nothing built yet (one loading check below). Today the "Old terrain (2023)" mode
([TERRAIN-MODES.md](TERRAIN-MODES.md)) runs YujiroX2's port of the 2023 generator (`src/TLDRevamp/WorldGen/Old2023/`): the old formulas re-written in C#. This
page describes how RevaMP could instead run **the 2023 build's own code**, fetched by the player from Steam with their
own login, so that the repository keeps only scene constants and glue.

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

The one file needed is `TheLongDrive_Data/Managed/Assembly-CSharp.dll` of that manifest (about 1.5 MB). It is the
player's own copy of the game's code: it stays in their cache folder, is never committed, shipped or uploaded.

## Fetching it

The tool never sees the player's password. Three ways, best first:

1. **DepotDownloader** (SteamRE, open source, .NET):

   ```
   DepotDownloader -app 1017180 -depot 1017181 -manifest 7731485613881054580 -os windows -osarch 64 \
       -filelist old2023-files.txt -dir <cache>/old2023 -username <name> -remember-password
   ```

   with `old2023-files.txt` holding `TheLongDrive_Data/Managed/Assembly-CSharp.dll` (a line may start with `regex:`).
   The password (and Steam Guard code) is typed by the player into DepotDownloader itself, or `-qr` shows a QR code to
   approve in the Steam mobile app. `-remember-password` keeps a login token for the next fetch. `-filelist` makes it
   download only that file's chunks, not the 338 MB depot. `-manifest-only` lists the manifest's files first if the
   path ever changes. Pinning `-manifest` (instead of `-beta public`) keeps the fetched file identical even if the
   default branch moves.
2. **The Steam client's console** (no extra program, no login: the running client's own session):
   `steam://open/console`, then `download_depot 1017180 1017181 7731485613881054580`. It downloads the whole depot
   (338 MB) into `<Steam>/steamapps/content/app_1017180/depot_1017181/`; RevaMP's tool copies the one DLL out and the
   player may delete the rest. Simplest for a player, heavy on bandwidth.
3. **steamcmd** `+login <name> +download_depot 1017180 1017181 7731485613881054580 +quit`: also the whole depot, and
   it needs its own login.

After the fetch the tool checks the DLL against a SHA-256 pinned in RevaMP (a hash of the player's file, not the
file), so every machine in a session runs the same old code.

## Loading it beside the beta's

Both builds call their code `Assembly-CSharp`; Mono won't load a second assembly of the same identity. So the tool
rewrites a copy with Mono.Cecil (already in `BepInEx/core`) before loading it:

- the assembly and module name become `Assembly-CSharp-2023`;
- every namespace gets an `Old2023.` prefix (the global namespace becomes `Old2023`, `UnityEngine.Vector3d` becomes
  `Old2023.UnityEngine.Vector3d`), so `AccessTools.TypeByName` and other mods' type lookups never find an old type by
  accident;
- references to Unity's own assemblies (UnityEngine.*, mscorlib) stay as they are and bind to the beta's.

The rewritten bytes are loaded with `Assembly.Load(byte[])` (never written next to the game's DLLs), and RevaMP calls
the old methods by reflection (compiled once into delegates: `AccessTools.MethodDelegate`), so nothing in RevaMP is
compiled against the old build.

## What runs from it

The port today reimplements, from the 2023 build:

| Port (`WorldGen/Old2023/`) | Old code it follows | Needs an instance? |
| --- | --- | --- |
| `OldNoise` (height, biome colour) | `TerrainGenerationJob.randomize`, `VertexColor`, `Noise`, `Landscape`, `Rnv` | yes: the job object holds the seeded offsets (`Rnv` draws) its methods read |
| `OldRoad` | `roadGenScript.CalcOneRoad` | yes: a MonoBehaviour; reads the generator's serialized fields (widths, noise layers, per-bone limits) |
| `OldBuildings` | `roadBuildingScript.FStart` / `GenAll` / `GenerateOne` | yes: a MonoBehaviour with the building lists of the old scene |

None of these classes exist in the beta's assembly (checked: no `TerrainGenerationJob`, no `roadBuildingScript`, no
`CalcOneRoad`), so the old code can only come from the 2023 DLL.

An old MonoBehaviour can't be created as a live Unity object (it is not in any scene, and its assembly isn't a Unity
script assembly). It doesn't need to be: RevaMP makes a **stand-in** (`FormatterServices.GetUninitializedObject`, no
constructor, no Unity object behind it) and sets its serialized fields from the **scene constants** the port already
keeps (read from the old scene with `tools\OldRoadDump`). The old method then runs on the stand-in as long as it
touches only fields, its own static helpers, `System.Random` and pure Unity maths (`Mathf.PerlinNoise`,
`Vector2`/`Vector3`). RevaMP's AsyncTerrain already runs the beta's terrain method this way on worker threads.

What stays in the repository: the scene constants, the glue that maps the beta's coordinates (old world 60 m east of
the beta's origin, the -z road computed on the terrain turned by 180°) and converts the old `Vector3d`/`Vector2`
results to the beta's types, and the fallback (the current port) for players who don't fetch the old build.

## Checked on the test desktop (2026-10-23)

No 2023 DLL is on the test desktop (every `Assembly-CSharp.dll` there is the beta's), and fetching one needs a
player's Steam login, which a test must not type. So the loading was tried with the beta's own DLL standing in for
the old one: a throwaway BepInEx plugin, in a test copy of the game, at plugin load (main menu):

| | |
| --- | --- |
| Rewrite (Cecil, from `BepInEx/core`) and `Assembly.Load(byte[])` | 220 ms, 1.5 MB; 378 global-namespace types moved to `Old2023` |
| Loaded beside the live one | yes: two assemblies, `Assembly-CSharp` and `Assembly-CSharp-2023`; `AccessTools.TypeByName("mainscript")` still finds the live one |
| A static method with Unity types (`terrainGenScript.CalculateTriangleNormal`) | runs from the copy, same result as the live one |
| A static method with the assembly's own struct (`mainscript.GetChunkPos(Vector3d, float)`) | runs, same result; each copy has its own `Vector3d` type, so results are converted field by field |
| Statics | separate: the copy's `mainscript.s` is null (its own singletons never set) |
| A stand-in MonoBehaviour of the copy (`roadGenScript`, `GetUninitializedObject`) | made, its 54 fields settable |
| Whole step | 367 ms at startup, GC heap 40 MB after |

Not tried: the old build's own code (needs the fetch), and DepotDownloader/steamcmd (neither is installed on the
test desktop; the Steam client console route needs no install).

Found on the way: types with a namespace (`UnityEngine.Vector3d`) kept it in that run, so `TypeByName` with the
full name could find the old type; the tool must prefix every namespace, as written above.

## Open questions (they need the old DLL, i.e. one fetch with a player's login)

- Which of the old methods touch `mainscript.s`, `itemdatabase.s` or other singletons: those are null in the loaded
  copy (its statics are its own) and need either a stand-in singleton or a transpiler swap (GamePatch) at load.
- Whether `TerrainGenerationJob` is a Unity job struct using `Unity.Collections`/`Unity.Mathematics`: the beta ships
  neither, so those DLLs would need fetching too (same `-filelist`), or the job's maths calls swapping.
- Whether `roadBuildingScript.GenerateOne` instantiates prefabs on its way to the plan (then only its plan part can
  run, the same split the port makes).
- The Unity version of the 2023 build: an API it calls that Unity 2022.3 removed fails when that method is first
  compiled (MissingMethodException), which the tool would catch per method and fall back to the port.

## Steps to build it

1. `tools/old2023fetch.py`: run DepotDownloader (or explain the console route), check the hash, keep the DLL in
   `BepInEx/cache/RevaMP/old2023/` of the player's game.
2. In the plugin: load, rewrite and cache the assembly at the first new game in old terrain mode; resolve the three
   entry points; fail closed to the port with a line in the log and the bridge's `terrain` status.
3. A check like the port's own: terrain heights, the road within 1 mm over 40 km and the building plan, old code
   against the port, on a few seeds; then the port can be retired.
