// Part of the old 2023 terrain (see OldTerrain.cs): the building plan is the 2023 build's own roadBuildingScript.
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.WorldGen.Old2023
{
    /// Puts the old building plan into the beta's building (POI) generator in old terrain mode: a poiGenScript chunk
    /// gets no random beta buildings; instead the old plan's buildings that fall in it are added with AddPoi at their
    /// old position and rotation once the roads there are done (as the beta's own AddPois waits). The start house and
    /// the map's fixed buildings are the beta's. Building IDs: 4..16 are the same in both builds, mansion 18 -> 17.
    internal static class OldBuildings
    {
        private static volatile List<OldScene.Building> _plan;   // beta global frame, sorted by z
        private static poiGenScript[] _owners = new poiGenScript[0];
        private static readonly Dictionary<poiGenScript, Dictionary<Vector3d, List<OldScene.Building>>> Pending =
            new Dictionary<poiGenScript, Dictionary<Vector3d, List<OldScene.Building>>>();
        public static long Queued, Placed, Missing;
        public static int Forward;   // the plan of the 2023 road itself (+z)

        public static int Count => _plan != null ? _plan.Count : 0;
        public static IReadOnlyList<OldScene.Building> Plan => _plan;

        /// The plan of both roads (OldScene.BuildingPlan: the 2023 build's roadBuildingScript.FStart/GenAll), moved
        /// into the beta's frame: the old world shifted by OldNoise.OffsetX, y from the start's height, the -z road's
        /// plan turned back by 180 degrees.
        public static void Set(mapSettingScript ms, OldScene scene)
        {
            Pending.Clear();
            Queued = Placed = Missing = 0;
            if (scene == null || ms == null || ms.poiGens == null) { _plan = null; _owners = new poiGenScript[0]; return; }
            var plan = new List<OldScene.Building>();
            List<OldScene.Building> fwd, back;
            double h0;
            lock (scene.Lock)
            {
                fwd = scene.BuildingPlan(back: false);
                back = scene.BuildingPlan(back: true);
                h0 = scene.Start(scene.Forward).y;
            }
            foreach (var b in fwd)
                plan.Add(new OldScene.Building { X = b.X + OldNoise.OffsetX, Y = b.Y + h0, Z = b.Z, Ry = b.Ry, OldType = b.OldType });
            foreach (var b in back)
                plan.Add(new OldScene.Building { X = -b.X + OldNoise.OffsetX, Y = b.Y + h0, Z = -b.Z, Ry = b.Ry + 180f, OldType = b.OldType });
            plan.Sort((a, b) => a.Z.CompareTo(b.Z));
            _plan = plan;
            Forward = fwd.Count;
            _owners = ms.poiGens.ToArray();
            Plugin.Log.LogInfo("Old buildings: " + plan.Count + " in the 2023 plan along the old road (" + fwd.Count + " towards +z)");
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
                    if (!Pending.TryGetValue(__instance, out var byChunk)) Pending[__instance] = byChunk = new Dictionary<Vector3d, List<OldScene.Building>>();
                    if (!byChunk.TryGetValue(_chunk, out var list)) byChunk[_chunk] = list = new List<OldScene.Building>();
                    list.Add(b);
                    Queued++;
                }
                return false;
            }
        }

        private static int First(List<OldScene.Building> plan, double z)
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
