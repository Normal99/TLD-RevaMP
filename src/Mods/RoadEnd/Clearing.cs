using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RoadEnd
{
    /// The land, the side roads and everything on them go on past the end; only the grave's own spot is kept clear:
    /// no tree or rock on it, no building next to it.
    [HarmonyPatch]
    public static class Clearing
    {
        static bool Near(Vector3d p, double radius)
        {
            if (!EndPlan.Current.HasRoadEnd) return false;
            var d = p - EndPlan.Current.RoadEnd; d.y = 0;
            return d.magnitude < radius;
        }

        /// objGenScript: a category makes all objects of a chunk at once (UpdGen), then places and removes them by
        /// distance. Objects on the grave's spot are taken out right after they are made.
        [HarmonyPatch(typeof(objGenScript.categoryClass), nameof(objGenScript.categoryClass.UpdGen))]
        [HarmonyPostfix]
        private static void AfterObjGen(objGenScript.categoryClass __instance)
        {
            if (!World.Active || !EndPlan.Current.HasRoadEnd || __instance.generatedChunks == null) return;
            var end = EndPlan.Current.RoadEnd;
            double r = Plugin.ObjectClearance;
            List<Vector3d> gone = null;
            foreach (var chunk in __instance.generatedChunks)
            {
                // a chunk spans its key +- its size: skip the ones that can't reach the spot
                if (System.Math.Abs(chunk.Key.x - end.x) > __instance.currentChunkSize + r || System.Math.Abs(chunk.Key.z - end.z) > __instance.currentChunkSize + r) continue;
                foreach (var o in chunk.Value.objs)
                {
                    if (!Near(o.Value.gpos, r)) continue;
                    if (o.Value.obj != null) UnityEngine.Object.Destroy(o.Value.obj);
                    (gone ??= new List<Vector3d>()).Add(o.Key);
                }
                if (gone != null) { foreach (var k in gone) chunk.Value.objs.Remove(k); gone.Clear(); }
            }
        }

        /// Buildings: poiGenScript.AddPois turns each chunk's planned buildings (poiToPlace) into POIs. Planned ones
        /// next to the grave are dropped first, and one already standing there goes.
        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPois))]
        [HarmonyPrefix]
        private static void BeforeAddPois(poiGenScript __instance)
        {
            if (!World.Active || !EndPlan.Current.HasRoadEnd || __instance.chunksNeeded == null) return;
            double r = Plugin.BuildingClearance;
            foreach (var c in __instance.chunksNeeded)
            {
                if (!__instance.chunks.TryGetValue(c, out var chunk)) continue;
                chunk.poiToPlace?.RemoveAll(p => Near(p.pos, r));
                if (chunk.pois == null) continue;
                List<Vector3d> gone = null;
                foreach (var kv in chunk.pois)
                {
                    var poi = kv.Value;
                    if (poi.id < 0 || Grave.IsGrave(poi) || !Near(poi.pos, r)) continue;   // fixed POIs (the start house) stay
                    __instance.RemovePoi(poi);
                    (gone ??= new List<Vector3d>()).Add(kv.Key);
                }
                if (gone != null) foreach (var k in gone) chunk.pois.Remove(k);
            }
        }
    }
}
