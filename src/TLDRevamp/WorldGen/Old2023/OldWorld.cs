// Part of the old 2023 terrain (see OldTerrain.cs).
using System.Collections.Generic;

namespace TLDRevamp.WorldGen.Old2023
{
    /// Switches the old 2023 world on or off for the map that is about to generate. Called from TerrainHooks before the
    /// map's generators start (mapSettingScript.FStart), so no terrain, road or building exists yet.
    ///
    /// The old world has only its one road, so the generators run as "main road only" (TerrainChoice.ApplyTo sets the
    /// flags); this puts the old landscape, the old road and the old roadside buildings on top of them. All three are
    /// the 2023 build's own code (OldBuild), so the world can only be old when its files are on this machine.
    internal static class OldWorld
    {
        public static bool Active => OldTerrain.Active;
        private static OldScene _scene;
        public static double SceneMs;

        /// on: the world should be the old one. Returns whether it is (false when the old code can't be loaded).
        public static bool Apply(mapSettingScript map, bool on, int seed)
        {
            _scene?.Dispose();
            _scene = null;
            if (on && !OldBuild.EnsureLoaded())
            {
                Plugin.Log.LogWarning("Old terrain: the 2023 game code is not available (" + OldBuild.Error + "): the world is not the old one");
                on = false;
            }
            if (on)
            {
                var t0 = System.DateTime.UtcNow;
                try { _scene = new OldScene(seed); }
                catch (System.Exception e)
                {
                    Plugin.Log.LogError("Old terrain: the 2023 world could not be set up: " + e);
                    _scene = null;
                    on = false;
                }
                SceneMs = (System.DateTime.UtcNow - t0).TotalMilliseconds;
            }
            OldTerrain.Set(map, on, seed);
            OldRoad.Set(map, OldTerrain.Active ? _scene : null, RoadGens(map));
            OldBuildings.Set(map, OldTerrain.Active ? _scene : null);
            return OldTerrain.Active;
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
            ",\"code\":" + OldBuild.Stats() +
            ",\"seed\":" + (OldTerrain.Noise != null ? OldTerrain.Noise.Seed : 0) +
            ",\"sceneMs\":" + SceneMs.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"heights\":" + OldTerrain.Heights + ",\"colors\":" + OldTerrain.Colors +
            ",\"roadChunks\":" + OldRoad.Chunks + ",\"roads\":" + OldRoad.Roads + ",\"roadElements\":" + OldRoad.Elements +
            ",\"roadSegments\":" + (OldRoad.Path != null ? OldRoad.Path.ForwardSegments : 0) +
            ",\"buildingsPlanned\":" + OldBuildings.Count + ",\"buildingsForward\":" + OldBuildings.Forward +
            ",\"buildingsQueued\":" + OldBuildings.Queued +
            ",\"buildingsPlaced\":" + OldBuildings.Placed + ",\"buildingsMissing\":" + OldBuildings.Missing + "}";
    }
}
