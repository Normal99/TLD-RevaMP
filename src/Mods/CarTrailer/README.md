# Car Trailer

A trailer for carrying a car, built from the game's own parts: the Brakas transporter's bed (meshes, colliders and
hinged ramp) low between four wheels, on the game's trailer (hitching, brake, lights, saving). A car driven up the
ramp is held the way the Brakas holds one: listed in its storage area with its wheels locked. It is rare loot, at
the places the game's trailers and vehicles spawn.

## What it shows

This is the example of a mod that needs **no multiplayer code**. It never references `TLDRevamp.Multiplayer`. It
adds a game item, so the game saves and loads it, and TLD RevaMP shares it like the game's own trailer, including a
car carried on it (a carried car follows whichever machine simulates its carrier).

What it can't get without the API: the join only checks mods that register with the API, so every player has to
install it by hand, and any state it kept outside the game's save records would stay on one machine. Compare with
[Head Unit](../HeadUnit/README.md), which registers.

Also useful outside multiplayer:
- registering a new item in the game's item database with an id far above the game's own (`Plugin.ItemId`; saves
  store it, so it never changes), and adding it to the loot tables with a weight;
- building a prefab from pieces of other items' prefabs, scripts included (`BrakasBed.cs`).

## Files

| File | What it does |
| --- | --- |
| `Plugin.cs` | Registers the item, builds the trailer from the game's trailer, loot weights, the bed's dimensions |
| `BrakasBed.cs` | Copies the Brakas's bed, colliders and ramp onto the trailer |

Tested in a two-machine session by `tools/cartrailer.py`, plus `tools/trailerload.py` and the other trailer tests.
