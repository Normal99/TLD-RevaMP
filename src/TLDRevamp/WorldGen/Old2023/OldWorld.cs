// Part of the old 2023 terrain port (see OldTerrain.cs for its origin and licence).
using System.Collections.Generic;

namespace TLDRevamp.WorldGen.Old2023
{
    /// Switches the old 2023 world on or off for the map that is about to generate. Called from TerrainHooks before the
    /// map's generators start (mapSettingScript.FStart), so no terrain, road or building exists yet.
    ///
    /// The old world has only its one road, so the generators run as "main road only" (TerrainChoice.ApplyTo sets the
    /// flags); this puts the old landscape, the old road and the old roadside buildings on top of them.
    internal static class OldWorld
    {
        public static bool Active => OldTerrain.Active;

        public static void Apply(mapSettingScript map, bool on, int seed)
        {
            OldTerrain.Set(map, on, seed);
            OldRoad.Set(map, OldTerrain.Active ? OldTerrain.Noise : null, RoadGens(map));
            OldBuildings.Set(map, OldTerrain.Active ? OldRoad.Path : null, OldTerrain.Noise);
        }

        /// The map's road generators, and any other in its hierarchy (as the mod found them).
        internal static List<roadGenScript> RoadGens(mapSettingScript ms)
        {
            var list = new List<roadGenScript>();
            if (ms == null) return list;
            if (ms.roadGens != null)
                foreach (var g in ms.roadGens) if (g != null && !list.Contains(g)) list.Add(g);
            foreach (var g in ms.transform.root.GetComponentsInChildren<roadGenScript>(true))
                if (!list.Contains(g)) list.Add(g);
            return list;
        }

        public static string Stats() =>
            "{\"active\":" + (OldTerrain.Active ? "true" : "false") +
            ",\"seed\":" + (OldTerrain.Noise != null ? OldTerrain.Noise.Seed : 0) +
            ",\"heights\":" + OldTerrain.Heights + ",\"colors\":" + OldTerrain.Colors +
            ",\"roadChunks\":" + OldRoad.Chunks + ",\"roads\":" + OldRoad.Roads + ",\"roadElements\":" + OldRoad.Elements +
            ",\"buildingsPlanned\":" + OldBuildings.Count + ",\"buildingsQueued\":" + OldBuildings.Queued +
            ",\"buildingsPlaced\":" + OldBuildings.Placed + ",\"buildingsMissing\":" + OldBuildings.Missing + "}";
    }
}
