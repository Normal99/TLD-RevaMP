using System;
using System.Collections.Generic;

namespace TLDRevamp.WorldGen
{
    /// Applies the chosen terrain mode to the game's own world generators (issue #32).
    ///
    /// How it works, and why it is safe:
    ///
    /// The shipped game generates its roads with roadGenScript, which already carries two scene flags:
    /// `hasMainRoad` (a main road down the middle of the world, in the chunks whose x is 0) and `hasSideRoad`
    /// (side roads branching off it). The generator reads them in UpdDotChunk, ConnectDots, ConnectChunks,
    /// ChunkDone, UpdChunksNeeded and RoadConnectMemo, so the whole world follows from them.
    ///
    ///   Crossroads    = the game as shipped: hasMainRoad = true, hasSideRoad = true.
    ///   MainRoadOnly  = hasMainRoad = true, hasSideRoad = false. The generator then keeps only the chunks with
    ///                   x = 0, generates the main road along them and no side roads at all.
    ///
    /// We do not add a generator and we do not touch the game's noise, its terrain or its buildings: the world
    /// stays a pure function of the seed and the mode, exactly as before, so two players with the same seed and
    /// mode still get the same world and the multiplayer sync is unaffected.
    ///
    /// The flags are scene-serialised on the map prefab, so they are set on the map instance (not the prefab) and
    /// only before the map starts generating (mapSettingScript.FStart -> roadGenScript.FStart -> Restart).
    /// Changing them later would not rebuild the roads, so the mode is applied at the one point where it counts.
    public static class TerrainChoice
    {
        /// The mode this client wants for a new game. Set by the new-game selector, by the settings screen and by
        /// the host's Welcome message.
        public static TerrainMode Selected = TerrainModes.Default;

        /// The mode the world that is running right now was generated with.
        public static TerrainMode Active = TerrainModes.Default;

        /// Where the running mode came from: "new game", "save", "host" or "default".
        public static string ActiveSource = "default";

        public static long Applied, HostTaken, SaveTaken, Tagged, Errors;

        // ------------------------------------------------------------------ reading the game's own state

        /// The road generators of a map, in the order the game holds them.
        public static List<roadGenScript> RoadGens(mapSettingScript map) =>
            map != null && map.roadGens != null ? map.roadGens : new List<roadGenScript>();

        /// What the map's own generators say today: does it have a main road, does it have side roads?
        public static void ReadFlags(mapSettingScript map, out bool hasMainRoad, out bool hasSideRoad)
        {
            hasMainRoad = true; hasSideRoad = true;   // the game's own default
            var gens = RoadGens(map);
            if (gens.Count == 0) return;
            var g = gens[0];
            hasMainRoad = g.hasMainRoad;
            hasSideRoad = g.hasSideRoad;
        }

        /// The mode a map's own generators describe. Used to notice a map we did not set ourselves.
        public static TerrainMode ModeOf(mapSettingScript map)
        {
            ReadFlags(map, out bool main, out bool side);
            if (main && side) return TerrainMode.Crossroads;
            if (main && !side) return TerrainMode.MainRoadOnly;
            return TerrainMode.Crossroads;   // no main road at all: not a mode we offer; the game as it is
        }

        // ------------------------------------------------------------------ applying a mode

        /// Set the road generator flags of a map to a mode. Returns true when something changed.
        /// The flags are on the map instance, so the map prefab in the assets is never touched.
        public static bool ApplyTo(mapSettingScript map, TerrainMode mode)
        {
            if (map == null) return false;
            if (!TerrainModes.IsSelectable(mode)) return false;   // OldTerrain: not implemented
            bool wantMain = true;                                 // both shipped modes keep the main road
            bool wantSide = mode == TerrainMode.Crossroads;
            var gens = RoadGens(map);
            bool changed = false;
            for (int i = 0; i < gens.Count; i++)
            {
                var g = gens[i];
                if (g == null) continue;
                if (g.hasMainRoad != wantMain) { g.hasMainRoad = wantMain; changed = true; }
                if (g.hasSideRoad != wantSide) { g.hasSideRoad = wantSide; changed = true; }
            }
            return changed;
        }

        /// The mode a save asks for: the terrain tag in the save's map name.
        public static TerrainMode ModeOfSave(string mapName) => TerrainModes.FromMapName(mapName);

        /// The map name a save should be written with: the plain name plus the mode's tag.
        public static string TaggedMapName(string mapName, TerrainMode mode) => TerrainModes.WithTag(mapName, mode);

        // ------------------------------------------------------------------ diagnostics

        /// The diagnostics of the mode. The map is passed in, so this file stays free of the game's own types.
        public static string Stats(mapSettingScript map)
        {
            ReadFlags(map, out bool main, out bool side);
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"selected\":").Append(Json.Str(TerrainModes.Name(Selected)))
              .Append(",\"active\":").Append(Json.Str(TerrainModes.Name(Active)))
              .Append(",\"source\":").Append(Json.Str(ActiveSource))
              .Append(",\"hasMainRoad\":").Append(main ? "true" : "false")
              .Append(",\"hasSideRoad\":").Append(side ? "true" : "false")
              .Append(",\"roadGens\":").Append(RoadGens(map).Count)
              .Append(",\"applied\":").Append(Applied)
              .Append(",\"fromHost\":").Append(HostTaken)
              .Append(",\"fromSave\":").Append(SaveTaken)
              .Append(",\"tagged\":").Append(Tagged)
              .Append(",\"errors\":").Append(Errors)
              .Append(",\"oldTerrainAvailable\":false")
              .Append(",\"oldTerrainBlocker\":").Append(Json.Str(TerrainModes.OldTerrainBlocker))
              .Append('}');
            return sb.ToString();
        }
    }
}
