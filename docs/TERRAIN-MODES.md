# Terrain modes (issue #32)

A new game can be generated in one of three shapes, and a save keeps the shape it was made with.

| Mode | What the world is | How it is made |
|---|---|---|
| **Crossroads** | The game as it ships: a main road down the middle of the world with side roads crossing it. | The game's own road generator with its own flags (`hasMainRoad = true`, `hasSideRoad = true`). Byte-for-byte the world the game generates today. |
| **Main road only** | One long, unbroken road; nothing branches off it. | The same generator with `hasSideRoad = false`, plus RevaMP's fix (`WorldGen/MainRoadOnlyRoads.cs`): the game alone breaks this road at every chunk. See [Main road only](#main-road-only). |
| **Old terrain (2023)** | The world of the public 2023 build: its hills, its one road and its roadside buildings, drawn with today's models. | YujiroX2's port of the 2023 generator (`WorldGen/Old2023/`). See [The old 2023 terrain](#the-old-2023-terrain). |

## Where the choice is made

Only on **the new-game screen**: a **Terrain** dropdown next to the map and biome dropdowns, built from a clone of the
game's own map dropdown so it looks and behaves like the game's other dropdowns. It is not in Settings: the terrain
belongs to a new world, and a world that exists already keeps its own.

The last pick is remembered for the next new game (`newGameTerrain` in RevaMP's settings file; the key the old
Settings row wrote, `terrainMode`, is still read). It is never written into the game's own settings file.

With YujiroX2's own TerrainChoice mod installed, RevaMP stands aside: no second dropdown, no flags or save tag of
RevaMP's; that mod picks the terrain. The main road only fix still works on its worlds.

## Which mode a world gets

In order of precedence:

1. **The host's mode**, when you join somebody else's world. The world is the host's: it was generated on the host's
   machine, and everybody has to drive the same roads. The host sends its mode in `Welcome` (protocol 51, one byte; the old terrain since protocol 52)
   and the joiner takes it before its world loads.
2. **The save's own mode**, when you load a save. A save keeps the terrain it was made with, so loading an old save
   gives the same world it gave before.
3. **Your own choice**, for a new game.

Leaving the host's world puts your own choice back.

## How a save keeps its terrain

The mode is written into the save's map name as a tag: `Dust Belt [MainRoadOnly]`.

- The tag is added when the save is written (`savedatascript.RefreshProperties`).
- The tag is read back when the save is loaded, and the mode comes from it.
- The game never sees the tag: the name the player reads is the plain `Dust Belt`, and the map is still looked up in
  the game's own map list by its plain name.

A save made before this feature existed has no tag and is played on the game's own world (Crossroads), exactly as
before.

## Diagnostics

The bridge command `terrain` reports the mode, where it came from and the road generator's own flags:

```
terrain                     -> the mode, its source, the flags and the old terrain's state
terrain roads               -> every road generator: its chunks, roads, the main road's gaps and side roads, and
                               roads that are generated but can never be placed
terrain oldroad 5000        -> the old 2023 road's point at that z (old terrain only)
terrain select MainRoadOnly -> pick the mode for the next new game
terrain host Crossroads     -> take a host's mode (what Welcome does)
```

## Main road only

With `hasSideRoad = false` the game's own generator does not make one road. Read from the shipped game's
`roadGenScript`:

- each main-road chunk (x = 0) still gets 2 to 4 dots, and `ConnectDots` still joins every pair of neighbouring dots in
  it. Only the path from the southmost dot to the northmost one is the main road; the rest stay as side roads, so
  every chunk shows a crossroad;
- `ConnectChunks` joins two chunks only when the diagonal chunk beside them exists, and in this mode the chunks off
  x = 0 are never made. So no chunk is joined to the next: the road ends at the north edge of every chunk, and a new
  piece starts in the next.

The fix keeps the game's own dots and its own road inside a chunk, so the road wanders the way the game's main road
does. Inside a main-road chunk it keeps only the roads on the main path, and it joins each chunk's northmost dot to the
next chunk's southmost dot with a main road: the pair the game itself joins when it can. A join is made only between
two chunks the game keeps, so it lives as long as they do; a chunk that was already finished when its northern
neighbour arrived is opened again so the join gets generated and placed.

**The grey road in the distance that you can never reach** (read from the code; the test runs did not catch one
in the act). The game makes the chunks beside the main road's line
for a frame while it looks at the needed chunks' neighbours, and its `ConnectChunks` sometimes joins a main-road
chunk to one of them: a road leading off the main road, kept in a chunk the game never needs, so its pieces are never
placed. RevaMP's distant road ribbons (`Fixes/TerrainHorizon.cs`) drew it anyway, so it showed far away and vanished
as you came close. The fix removes every road off the main road's line in this mode, and the distant ribbons draw only
roads in the chunks the game keeps (the ones it will place when you get there), in every mode.

`tools/mainroad.py` drives along the road and checks it (`terrain roads`: 0 gaps, 0 side roads, every main road
generated, no generated road piece the game can never place); `tools/unit/TerrainMainRoadTest.cs` tests the rules.

## The old 2023 terrain

The landscape, the road and the roadside buildings of the public build v2023.05.02d. The code is YujiroX2's, from
their TerrainChoice mod (github.com/YujiroX2/TLD-TerrainChoice, GPL-3.0, the same licence as RevaMP), ported with
their permission as a collaborator on this project. They re-implemented it from the public 2023 build and checked it
against that build: terrain heights equal, the road within 1 mm over 40 km, all roadside buildings of a test seed in
place.

- `Old2023/OldTerrain.cs`: the old height and biome colour. RevaMP's worker-thread terrain (`Fixes/AsyncTerrain.cs`)
  asks it directly.
- `Old2023/OldRoad.cs`: the old road. The 2023 world had no road behind the start; here a second road runs towards -z
  by the same rules, because today's world goes on in both directions.
- `Old2023/OldBuildings.cs`: the old roadside building plan.
- `Old2023/OldWorld.cs`: switches the three on or off before the map's generators start.

The road generators run as "main road only" underneath, so the main road only fix applies here too.

## Provenance

Crossroads and Main road only are derived from **the game's own code** (`Assembly-CSharp.dll`, the shipped game) and
the game's own shipped assets. The old terrain is YujiroX2's code (see above).

The facts the implementation rests on, all read out of the game's own code and assets:

- The road generator's two flags: `roadGenScript.hasMainRoad` and `roadGenScript.hasSideRoad`. The game reads them in
  `UpdDotChunk`, `ConnectDots`, `ConnectChunks`, `ChunkDone` and `UpdChunksNeeded`, so the whole world follows from
  them.
- The game's own default: the shipped default map's one road generator has `hasMainRoad = true` and
  `hasSideRoad = true`. That is what "Crossroads" is, and it is byte-identical to the world the game generates today.
- The main road lives in the chunks whose x is 0; with `hasSideRoad = false` the generator keeps only those chunks
  and makes no side roads.
- Where the mode is applied: `mapSettingScript.FStart` (before `RestartMap` starts the generators) is the one point
  where the map instance exists and the generators have not run yet.
- Where the save keeps it: `saveproperties.map` holds the map's name (`menuhandler.mapSaveClass.mapName`), and
  `savedatascript.RefreshProperties` writes it.
- Where the host's world description is: the `Welcome` message (`Net/Mp.cs`), which already carries the seed, the
  start car, the map type, the game mode and the session settings.
