// The old 2023 terrain: the public game v2023.05.02d's own code, from the player's Steam download (OldBuild). The
// way it is put into the beta's world (the old world 60 m east of the beta's origin, its one road, its roadside
// buildings) follows YujiroX2's TerrainChoice (github.com/YujiroX2/TLD-TerrainChoice, GPL-3.0, the same licence as
// TLD RevaMP), with YujiroX2's permission as a project collaborator.
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.WorldGen.Old2023
{
    /// The landscape of the 2023 build (height and biome colour): its TerrainGenerationJob.randomize and VertexColor,
    /// run on a job seeded like the old game's. Every constant there is hard-coded in the old code, so the shape needs
    /// none of the old build's assets. The beta (and RevaMP) ask for heights on worker threads: each thread gets its
    /// own job (the job writes scratch fields of its own while it computes).
    /// The old code works in float on Vector2(x, z) of its local world coordinates; the beta's global x/z map 1:1.
    internal sealed class OldNoise
    {
        /// The old world sits this far east of the beta's origin: the beta puts the start house 45 m beside the main
        /// road on the side facing the origin (poiGenScript.StartHouseStart), so the road must not run through it.
        public const double OffsetX = 60.0;

        public readonly int Seed;
        [ThreadStatic] private static object _job;
        [ThreadStatic] private static OldNoise _jobFor;

        public OldNoise(int seed) { Seed = seed; }

        private object Job()
        {
            if (!ReferenceEquals(_jobFor, this)) { _job = OldBuild.NewJob(Seed); _jobFor = this; }
            return _job;
        }

        /// randomize(c, edgeOfTheWorld: false, alignToRoad: false, alignToBuilding: false). The beta does its own
        /// road and building blending on top; the old edge of the world isn't used (the beta's world has none).
        public float Height(float x, float z) => OldBuild.Randomize(Job(), new Vector2(x, z));

        /// VertexColor(c, h): r desert, g grass, b snow (by height), a detail.
        public Color Color(float x, float z, float h) => OldBuild.VertexColor(Job(), new Vector2(x, z), h);

        // The old TerrainGenerator.GetBiome reads the colour with these thresholds: b > 0.25 winter, r > 0.5 desert,
        // else grass (the beta uses 0.33 / 0.33). The beta asks with the colour alone, so the thresholds are kept here.
        public static int BiomeInt(Color c) => c.b > 0.25f ? 1 : c.r > 0.5f ? 0 : 2;
        public static int BiomeIntNoSnow(Color c) => c.r > 0.5f ? 0 : 2;
    }

    /// Puts the old landscape into the beta's terrain: terrainGenSettings.Height / Biome / GetBiomeInt of the loaded
    /// map (and RevaMP's worker-thread copies of Height/Biome) use OldNoise. The beta's own POI and road blending
    /// still runs on top, so roads and buildings sit on the old ground as they sit on the beta's.
    internal static class OldTerrain
    {
        private static volatile OldNoise _noise;
        private static volatile terrainGenSettings[] _owners = new terrainGenSettings[0];
        public static long Heights, Colors;

        public static bool Active => _noise != null;
        public static OldNoise Noise => _noise;

        /// From the map's FStart prefix (main thread, before any generator starts). on: the old code is loaded.
        public static void Set(mapSettingScript ms, bool on, int seed)
        {
            if (!on || ms == null || ms.terrainGenSettings == null || ms.terrainGenSettings.Count == 0 || ms.imageMapLoad != null)
            {
                _noise = null;
                _owners = new terrainGenSettings[0];
                return;
            }
            _owners = ms.terrainGenSettings.ToArray();
            _noise = new OldNoise(seed);
            Heights = Colors = 0;
            Plugin.Log.LogInfo("Old terrain: the 2023 build's landscape for seed " + seed + " on " + _owners.Length + " terrain setting(s)");
        }

        private static OldNoise For(terrainGenSettings s)
        {
            var n = _noise;
            if (n == null) return null;
            foreach (var o in _owners) if (ReferenceEquals(o, s)) return n;
            return null;
        }

        /// The beta's Height(gpos, roads, pois) with the old base height (same blending code as the beta's).
        private static double Height(OldNoise n, Vector3d _gpos, List<roadGenScript.roadClass> _roadlist, List<poiGenScript.poiClass> _poiList)
        {
            System.Threading.Interlocked.Increment(ref Heights);
            double num = n.Height((float)(_gpos.x - OldNoise.OffsetX), (float)_gpos.z);
            if (_poiList != null && _poiList.Count > 0)
            {
                var align = poiGenScript.TerrainAlignsValue(_gpos, _poiList);
                if (align != null && align.lerp != 0.0) num = Mathd.Lerp(num, align.height, align.lerp);
            }
            if (_roadlist != null && _roadlist.Count > 0)
            {
                var road = roadGenScript.NearestPointOnRoadBone(_gpos, _roadlist, out var _result);
                if (road != null)
                {
                    Vector3d flat = _result;
                    flat.y = _gpos.y;
                    double magnitude = (flat - _gpos).magnitude;
                    if (magnitude != 0.0 && magnitude != double.MaxValue)
                        num = Mathd.Lerp(num, _result.y, Mathd.InverseLerp(-1.0, 1.0, Mathd.Sin(Mathd.Lerp(-90.0, 90.0, Mathd.InverseLerp(road.terrainAlignMaxDist, road.terrainAlignMinDist, magnitude)) * 0.01745329)));
                }
            }
            return num;
        }

        private static Color Biome(OldNoise n, Vector3d _gpos, double _height)
        {
            System.Threading.Interlocked.Increment(ref Colors);
            return n.Color((float)(_gpos.x - OldNoise.OffsetX), (float)_gpos.z, (float)_height);
        }

        [HarmonyPatch(typeof(terrainGenSettings), nameof(terrainGenSettings.Height), new[] { typeof(Vector3d), typeof(List<roadGenScript.roadClass>), typeof(List<poiGenScript.poiClass>) })]
        private static class GameHeight
        {
            [HarmonyPrefix]
            private static bool Prefix(terrainGenSettings __instance, Vector3d _gpos, List<roadGenScript.roadClass> _roadlist, List<poiGenScript.poiClass> _poiList, ref double __result)
            {
                var n = For(__instance);
                if (n == null) return true;
                __result = Height(n, _gpos, _roadlist, _poiList);
                return false;
            }
        }

        [HarmonyPatch(typeof(terrainGenSettings), nameof(terrainGenSettings.Biome), new[] { typeof(Vector3d), typeof(double) })]
        private static class GameBiome
        {
            [HarmonyPrefix]
            private static bool Prefix(terrainGenSettings __instance, Vector3d _gpos, double _height, ref Color __result)
            {
                var n = For(__instance);
                if (n == null) return true;
                __result = Biome(n, _gpos, _height);
                return false;
            }
        }

        [HarmonyPatch(typeof(terrainGenSettings), nameof(terrainGenSettings.GetBiomeInt), new[] { typeof(Color) })]
        private static class GameBiomeInt
        {
            [HarmonyPrefix]
            private static bool Prefix(terrainGenSettings __instance, Color _color, ref int __result)
            {
                if (For(__instance) == null) return true;
                __result = OldNoise.BiomeInt(_color);
                return false;
            }
        }

        [HarmonyPatch(typeof(terrainGenSettings), nameof(terrainGenSettings.GetBiomeIntNoSnow), new[] { typeof(Color) })]
        private static class GameBiomeIntNoSnow
        {
            [HarmonyPrefix]
            private static bool Prefix(terrainGenSettings __instance, Color _color, ref int __result)
            {
                if (For(__instance) == null) return true;
                __result = OldNoise.BiomeIntNoSnow(_color);
                return false;
            }
        }

        /// RevaMP's worker-thread copies of the game's Height/Biome (Fixes.AsyncTerrain, used by the terrain tiles
        /// and ParallelRoadGen) ask here first.
        internal static bool TryHeight(terrainGenSettings s, Vector3d _gpos, List<roadGenScript.roadClass> _roadlist, List<poiGenScript.poiClass> _poiList, out double height)
        {
            height = 0;
            var n = For(s);
            if (n == null) return false;
            height = Height(n, _gpos, _roadlist, _poiList);
            return true;
        }

        internal static bool TryBiome(terrainGenSettings s, Vector3d _gpos, double _height, out Color color)
        {
            color = default;
            var n = For(s);
            if (n == null) return false;
            color = Biome(n, _gpos, _height);
            return true;
        }
    }
}
