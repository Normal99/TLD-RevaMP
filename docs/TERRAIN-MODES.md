# Terrain modes (issue #32)

A new game can be generated in one of two shapes, and a save keeps the shape it was made with.

| Mode | What the world is | How it is made |
|---|---|---|
| **Crossroads** | The game as it ships: a main road down the middle of the world with side roads crossing it. | The game's own road generator with its own flags (`hasMainRoad = true`, `hasSideRoad = true`). Byte-for-byte the world the game generates today. |
| **Main road only** | One long ribbon of road; nothing branches off it. | The same generator with `hasSideRoad = false`. The game then keeps only the chunks on the main road's line and generates no side roads. |

The old 2023 terrain is **not offered**. See [The old 2023 terrain](#the-old-2023-terrain).

## Where the choice is made

- **The new-game screen** has a **Terrain** dropdown next to the map and biome dropdowns, built from a clone of the
  game's own map dropdown so it looks and behaves like the game's other dropdowns.
- **Settings → Revamp → World → Terrain for a new game** is the same choice, and it always works: if the game is
  updated and the new-game screen is laid out differently, the dropdown is simply not added and the player picks the
  terrain in the settings instead.

The choice applies to the **next** new game. It is never written into the game's own settings file.

## Which mode a world gets

In order of precedence:

1. **The host's mode**, when you join somebody else's world. The world is the host's: it was generated on the host's
   machine, and everybody has to drive the same roads. The host sends its mode in `Welcome` (protocol 51, one byte)
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
terrain                     -> the mode, its source and the flags
terrain select MainRoadOnly -> pick the mode for the next new game
terrain host Crossroads     -> take a host's mode (what Welcome does)
```

## The old 2023 terrain

**It cannot be reproduced faithfully, so it is disabled.** The exact blockers:

- **The game does not ship the old generator.** The current game contains one road generator (`roadGenScript`) and one
  terrain generator (`terrainGenScript`); the pre-2024 terrain code is not in the shipped assembly. What is left in the
  assembly is an unused `randomterrain` class with a different mesh layout and no road or building logic, so it is not
  the old world either.
- **The only implementation that exists is a third-party mod whose source is not published and carries no licence.**
  `AnonymousTLD/TLD-TerrainChoice` is not reachable (the repository, the user and the release URL all answer 404), and
  the shipped DLL carries no licence or copyright notice. Its code therefore cannot be copied, and with no published
  source there is nothing to re-derive it from either.
- **A look-alike is not the old terrain.** Reconstructing a "similar" noise and road path from the current game would
  produce a different world that only looks similar. That would silently break every save and every multiplayer world
  made with it, so the mode stays off rather than offering something that is not what it claims to be.

The mode is still part of the code (`TerrainMode.OldTerrain`) so that the wire format, the save tag and the settings
have a stable place for it, but it is never selectable, never sent and never taken from a save. Asking for it is
refused at every entry point.

## Provenance

Everything in this feature is derived from **the game's own code** (`Assembly-CSharp.dll`, the shipped game) and from
the game's own shipped assets. No code from any other mod was read or copied.

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
