// The old 2023 terrain, ported from AnonymousTLD's TerrainChoice 0.4.1 (github.com/AnonymousTLD/TLD-TerrainChoice,
// GPL-3.0, the same licence as TLD RevaMP), with AnonymousTLD's permission as a project collaborator. The formulas are
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
    /// The roadside building plan of the public build v2023.05.02d: a port of roadBuildingScript.FStart/GenAll/
    /// GenerateOne (P:38457). From the start, buildings follow the road's ideal line (OneValue x width) every few km:
    /// groups of building types picked by a seeded random, each on a random side at a random distance from the line.
    /// The numbers are the public build's scene values (seed-independent), read from the user's own copy with
    /// tools\OldRoadDump; the per-type sides are as the old FStart leaves them (defaults x xDistModifier).
    /// The -z road gets a plan by the same rules (seed + 1), turned like its road.
    internal static class OldBuildingPlan
    {
        public struct Building { public double X, Y, Z; public float Ry; public int OldType; }

        private sealed class Side { public float MinX, MaxX, MinY, MaxY; public Side(float a, float b, float c, float d) { MinX = a; MaxX = b; MinY = c; MaxY = d; } }
        private sealed class BType { public int Type; public float MinDist, MaxDist; public Side[] Sides; }
        private sealed class Group
        {
            public BType[] Types; public int MinCount, MaxCount;
            public List<int> Available = new List<int>(), CurrentAvailable = new List<int>();
            public int CurrentMaxCount, CurrentCount;
        }

        private const float DistModifier = 1f, MinStartDist = 3000f, MaxStartDist = 10000f;
        private const double End = 5000000.0;

        private static Side[] Sides(float near, float far) =>
            new[] { new Side(-far, -near, 165f, 195f), new Side(near, far, -15f, 15f) };

        private static BType T(int type, float minDist, float maxDist, Side[] sides) => new BType { Type = type, MinDist = minDist, MaxDist = maxDist, Sides = sides };

        private static Group[] NewGroups() => new[]
        {
            new Group { MinCount = 3, MaxCount = 6, Types = new[] {
                T(4, 5000f, 10000f, Sides(10f, 20f)),     // benzinkut01
                T(5, 3000f, 6000f, Sides(5f, 10f)),       // buszmegallo
                T(6, 6000f, 10000f, Sides(15f, 30f)),     // legjobbbenzinkut
                T(7, 5000f, 15000f, Sides(15f, 30f)),     // muhely
                T(10, 5000f, 10000f, Sides(15f, 30f)),    // oldpetrolstation
                T(11, 5000f, 8000f, Sides(15f, 30f)) } }, // turrethaz
            new Group { MinCount = 1, MaxCount = 1, Types = new[] {
                T(8, 8000f, 15000f, Sides(30f, 60f)),     // nagybenzinkut
                T(9, 8000f, 15000f, Sides(30f, 60f)),     // oldhouse
                T(12, 8000f, 15000f, new[] { new Side(0f, 0f, -10f, 10f), new Side(0f, 0f, 170f, 190f) }),   // vizmu, on the line
                T(16, 8000f, 15000f, Sides(8f, 16f)),     // yellowfoodkut
                T(18, 8000f, 15000f, Sides(80f, 160f)) } }, // mansion
        };

        /// The plan in the old game's frame (road start at the origin; y relative to the start's height, as the old
        /// buildinginmemory). back: the -z road's plan in its own turned frame.
        public static List<Building> Generate(OldRoadPath path, OldNoise noise, int seed, bool back)
        {
            var rnd = new System.Random(seed);
            float RNDf(float a, float b) => (float)rnd.Next((int)(a * 100f), (int)(b * 100f)) * 0.01f;
            int RNDi(int a, int b) => rnd.Next(a, b);
            float H(float x, float z) => back ? noise.Height(-x, -z) : noise.Height(x, z);

            var groups = NewGroups();
            var list = new List<Building>();
            double nextDist = RNDf(MinStartDist, MaxStartDist) * DistModifier;
            int lastBuilding = 0;
            int currentGroup = RNDi(0, groups.Length);
            void InitCount(Group g) { g.CurrentCount = 0; g.CurrentMaxCount = RNDi(g.MinCount, g.MaxCount); }
            void ResetCurrent(Group g, int count)
            {
                g.CurrentAvailable.Clear();
                if (g.Available.Count < 1) { g.Available.Clear(); for (int i = 0; i < g.Types.Length; i++) g.Available.Add(i); }
                for (int i = 0; i < count; i++)
                {
                    if (g.Available.Count < 1) break;
                    int index = RNDi(0, g.Available.Count);
                    g.CurrentAvailable.Add(g.Available[index]);
                    g.Available.RemoveAt(index);
                }
            }
            int GetRandom(Group g)
            {
                g.CurrentCount++;
                if (g.CurrentAvailable.Count < 1) ResetCurrent(g, g.CurrentMaxCount);
                int index = RNDi(0, g.CurrentAvailable.Count);
                int result = g.CurrentAvailable[index];
                g.CurrentAvailable.RemoveAt(index);
                return result;
            }
            InitCount(groups[currentGroup]);
            float h0 = H(0f, 0f);
            while (nextDist < End)
            {
                // OneCoord with the road's world at the origin: (OneValue(z) * width, 0, z)
                var vector = new Vector3(path.TargetX(back, (float)nextDist), 0f, (float)nextDist);
                var ahead = new Vector3(path.TargetX(back, (float)nextDist + 30f), 0f, (float)nextDist + 30f);
                float yrot = 0f - Vector3.SignedAngle(ahead - vector, Vector3.forward, Vector3.up);
                double x = vector.x, y = H(vector.x, vector.z) - h0, z = nextDist;
                // GenerateOne
                if (groups[currentGroup].CurrentCount >= groups[currentGroup].CurrentMaxCount)
                {
                    currentGroup++;
                    if (currentGroup >= groups.Length) currentGroup = 0;
                    InitCount(groups[currentGroup]);
                }
                var g = groups[currentGroup];
                int random = GetRandom(g);
                if (g.Types[random].Type == lastBuilding) random = GetRandom(g);
                var t = g.Types[random];
                lastBuilding = t.Type;
                int rside = RNDi(0, t.Sides.Length);
                var side = t.Sides[rside];
                Vector3 tempV = Quaternion.Euler(0f, yrot, 0f) * new Vector3(RNDf(side.MinX, side.MaxX), 0f, 0f);
                float ry = yrot + RNDf(side.MinY, side.MaxY);
                list.Add(new Building { X = x + tempV.x, Y = y + tempV.y, Z = z + tempV.z, Ry = ry, OldType = t.Type });
                nextDist += RNDf(t.MinDist, t.MaxDist) * DistModifier;
            }
            return list;
        }
    }

    /// Puts the old building plan into the beta's building (POI) generator in old terrain mode: a poiGenScript chunk
    /// gets no random beta buildings; instead the old plan's buildings that fall in it are added with AddPoi at their
    /// old position and rotation once the roads there are done (as the beta's own AddPois waits). The start house and
    /// the map's fixed buildings are the beta's. Building IDs: 4..16 are the same in both builds, mansion 18 -> 17.
    internal static class OldBuildings
    {
        private static volatile List<OldBuildingPlan.Building> _plan;   // beta global frame, sorted by z
        private static poiGenScript[] _owners = new poiGenScript[0];
        private static readonly Dictionary<poiGenScript, Dictionary<Vector3d, List<OldBuildingPlan.Building>>> Pending =
            new Dictionary<poiGenScript, Dictionary<Vector3d, List<OldBuildingPlan.Building>>>();
        public static long Queued, Placed, Missing;

        public static int Count => _plan != null ? _plan.Count : 0;
        public static IReadOnlyList<OldBuildingPlan.Building> Plan => _plan;

        public static void Set(mapSettingScript ms, OldRoadPath path, OldNoise noise)
        {
            Pending.Clear();
            Queued = Placed = Missing = 0;
            if (path == null || noise == null || ms == null || ms.poiGens == null) { _plan = null; _owners = new poiGenScript[0]; return; }
            var plan = new List<OldBuildingPlan.Building>();
            double h0 = noise.Height(0f, 0f);
            foreach (var b in OldBuildingPlan.Generate(path, noise, noise.Seed, back: false))
                plan.Add(new OldBuildingPlan.Building { X = b.X + OldNoise.OffsetX, Y = b.Y + h0, Z = b.Z, Ry = b.Ry, OldType = b.OldType });
            foreach (var b in OldBuildingPlan.Generate(path, noise, unchecked(noise.Seed + 1), back: true))
                plan.Add(new OldBuildingPlan.Building { X = -b.X + OldNoise.OffsetX, Y = b.Y + h0, Z = -b.Z, Ry = b.Ry + 180f, OldType = b.OldType });
            plan.Sort((a, b) => a.Z.CompareTo(b.Z));
            _plan = plan;
            _owners = ms.poiGens.ToArray();
            Plugin.Log.LogInfo("Old buildings: " + plan.Count + " in the 2023 plan along the old road");
        }

        public static int BetaId(int oldType) => oldType == 18 ? (int)itemdatabase.b.mansion : oldType;

        private static bool Owns(poiGenScript p)
        {
            if (_plan == null || p == null) return false;
            foreach (var o in _owners) if (ReferenceEquals(o, p)) return true;
            return false;
        }

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.GenChunk))]
        private static class Chunk
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First)]
            private static bool Prefix(poiGenScript __instance, Vector3d _chunk)
            {
                if (!Owns(__instance)) return true;
                if (!__instance.chunks.TryGetValue(_chunk, out var value))
                {
                    value = new poiGenScript.chunkClass();
                    __instance.chunks.Add(_chunk, value);
                }
                if (value.generated) return false;
                value.poiToPlace = new List<poiGenScript.tempPoiClass>();   // no random beta buildings
                value.generated = true;
                var plan = _plan;
                double half = __instance.chunkSize * 0.5;
                int i = First(plan, _chunk.z - half);
                for (; i < plan.Count && plan[i].Z <= _chunk.z + half; i++)
                {
                    var b = plan[i];
                    if (!mainscript.GetChunkPos(new Vector3d(b.X, 0.0, b.Z), __instance.chunkSize).Equals(_chunk)) continue;
                    if (!Pending.TryGetValue(__instance, out var byChunk)) Pending[__instance] = byChunk = new Dictionary<Vector3d, List<OldBuildingPlan.Building>>();
                    if (!byChunk.TryGetValue(_chunk, out var list)) byChunk[_chunk] = list = new List<OldBuildingPlan.Building>();
                    list.Add(b);
                    Queued++;
                }
                return false;
            }
        }

        private static int First(List<OldBuildingPlan.Building> plan, double z)
        {
            int lo = 0, hi = plan.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (plan[mid].Z < z) lo = mid + 1; else hi = mid; }
            return lo;
        }

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPois))]
        private static class Place
        {
            [HarmonyPostfix]
            private static void Postfix(poiGenScript __instance)
            {
                if (!Owns(__instance) || !Pending.TryGetValue(__instance, out var byChunk)) return;
                foreach (var chunk in __instance.chunksNeeded)
                {
                    if (!byChunk.TryGetValue(chunk, out var list)) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var b = list[i];
                        var pos = new Vector3d(b.X, b.Y, b.Z);
                        if (__instance.roadAffects && __instance.roadgen != null && !__instance.roadgen.RoadsDoneAtPlace(pos)) continue;
                        list.RemoveAt(i); i--;
                        int id = BetaId(b.OldType);
                        if (itemdatabase.s == null || id <= 0 || id >= itemdatabase.s.pois.Length || itemdatabase.s.pois[id] == null)
                        {
                            Missing++;
                            if (Missing <= 5) Plugin.Log.LogWarning("Old buildings: the beta has no building for old type " + b.OldType);
                            continue;
                        }
                        // the ground height there, with the beta's road blending (as the beta's own AddPois does)
                        pos.y = __instance.terrainGen != null ? __instance.terrainGen.Height(pos, mapSettingScript.RoadsNear(pos)) : b.Y;
                        __instance.AddPoi(pos, new Vector3(0f, b.Ry, 0f), id, _alignedToRoad: true);
                        Placed++;
                    }
                    if (list.Count == 0) byChunk.Remove(chunk);
                }
            }
        }
    }
}
