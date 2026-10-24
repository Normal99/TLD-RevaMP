// The old 2023 terrain, ported from YujiroX2's TerrainChoice 0.4.1 (github.com/YujiroX2/TLD-TerrainChoice,
// GPL-3.0, the same licence as TLD RevaMP), with YujiroX2's permission as a project collaborator. The formulas are
// theirs: re-implemented from the public game v2023.05.02d and checked against it (terrain heights equal, the road within
// 1 mm over 40 km, all 612 roadside buildings of seed 12345 in place). Changes for RevaMP: the namespace, the switch-on
// (TerrainHooks -> OldWorld.Apply instead of the mod's own LoadMap/FStart patches) and RevaMP's worker-thread terrain
// (Fixes.AsyncTerrain asks OldTerrain.TryHeight/TryBiome directly instead of being patched by reflection).
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.WorldGen.Old2023
{
    /// The landscape of the public build v2023.05.02d (height and biome colour), reimplemented from
    /// TerrainGenerationJob.randomize / VertexColor / Noise / Landscape / Rnv. Every constant there is hard-coded in
    /// the old code, so this is the old shape for a given seed without any of the old build's assets.
    /// Stateless after construction: the beta (and RevaMP) ask for heights on worker threads.
    /// The old code works in float on Vector2(x, z) of its local world coordinates; the beta's global x/z map 1:1.
    internal sealed class OldNoise
    {
        /// The old world sits this far east of the beta's origin: the beta puts the start house 45 m beside the main
        /// road on the side facing the origin (poiGenScript.StartHouseStart), so the road must not run through it.
        public const double OffsetX = 60.0;

        public readonly int Seed;
        private readonly Vector2[] _hv = new Vector2[8];   // randomize: Rnv(rnd) x8, in the old order
        private readonly Vector2[] _cv = new Vector2[3];   // VertexColor: Rnv(rnd) x3
        private readonly Vector2[] _oct = new Vector2[4];  // Noise(): new System.Random(seed) per call, one offset per octave

        public OldNoise(int seed)
        {
            Seed = seed;
            var r = new System.Random(seed);
            for (int i = 0; i < _hv.Length; i++) _hv[i] = Rnv(r);
            r = new System.Random(seed);
            for (int i = 0; i < _cv.Length; i++) _cv[i] = Rnv(r);
            r = new System.Random(seed);
            for (int i = 0; i < _oct.Length; i++)
            {
                float x = (float)r.Next(-100000, 100000) * 1E-05f;   // x first: C# evaluates arguments left to right
                float y = (float)r.Next(-100000, 100000) * 1E-05f;
                _oct[i] = new Vector2(x, y);
            }
        }

        private static Vector2 Rnv(System.Random rnd)
        {
            float x = (float)rnd.Next(-100000, 100000) * 1E-05f;
            float y = (float)rnd.Next(-100000, 100000) * 1E-05f;
            return new Vector2(x, y);
        }

        /// randomize(c, edgeOfTheWorld: false, alignToRoad: false, alignToBuilding: false). The beta does its own
        /// road and building blending on top; the old edge of the world isn't used (the beta's world has none).
        public float Height(float x, float z)
        {
            var c = new Vector2(x, z);
            float num = 0f;
            num += Noise(c, _hv[1], 1000f, 100f, 2, 0.05f, 0.1f) * Landscape(c, _hv[0], 100000f, 0.85f, 0.9f);
            num += Noise(c, _hv[3], 10000f, 875f, 3, 0.05f, 0.1f) * Landscape(c, _hv[2], 100000f, 0.3f, 0.5f);
            num += Noise(c, _hv[5], 3000f, 250f, 3, 0.05f, 0.1f) * Landscape(c, _hv[4], 100000f, 0.45f, 0.55f);
            num += Noise(c, _hv[7], 10000f, 2500f, 4, 0.05f, 0.1f) * Landscape(c, _hv[6], 100000f, 0.5f, 0.7f);
            return num;
        }

        /// VertexColor(c, h): r desert, g grass, b snow (by height), a detail.
        public Color Color(float x, float z, float h)
        {
            var c = new Vector2(x, z);
            const float num = 1000f;
            float value = Noise(c, _cv[0], 50f * num, 1f, 2, 0.3f, 0.25f);
            float value2 = Noise(c, _cv[1], 50f * num, 1f, 2, 0.3f, 0.25f);
            float b = Mathf.InverseLerp(1600f, 2500f, h);
            float value3 = Noise(c, _cv[2], 20f * num, 1f, 2, 0.3f, 0.25f);
            value = Mathf.InverseLerp(0.55f, 0.65f, value);
            value2 = Mathf.InverseLerp(0.3f, 0.4f, value2);
            value3 = Mathf.InverseLerp(0.6f, 0.8f, value3);
            return new Color(value, value2, b, value3);
        }

        private float Noise(Vector2 c, Vector2 pos, float frequency, float amplitude, int octaves, float persistence, float lacunarity)
        {
            float num = 0f, num2 = 1f, num3 = 1f;
            for (int i = 0; i < octaves; i++)
            {
                var v = new Vector2(_oct[i].x + pos.x, _oct[i].y + pos.y);
                num += Noise(c, v, frequency * num2, amplitude * num3);
                num2 *= lacunarity;
                num3 *= persistence;
            }
            return num;
        }

        private static float Noise(Vector2 c, Vector2 pos, float frequency, float amplitude) =>
            Mathf.PerlinNoise(pos.x + c.x / frequency, pos.y + c.y / frequency) * amplitude;

        private static float Landscape(Vector2 c, Vector2 pos, float frequency, float minValue, float maxValue) =>
            Mathf.InverseLerp(minValue, maxValue, Noise(c, pos, frequency, 1f));

        // old GetBiome: b > 0.25 winter, r > 0.5 desert, else grass (the beta uses 0.33 / 0.33)
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

        /// From the map's FStart prefix (main thread, before any generator starts).
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
            Plugin.Log.LogInfo("Old terrain: the 2023 landscape for seed " + seed + " on " + _owners.Length + " terrain setting(s)");
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
            Heights++;
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
            Colors++;
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
