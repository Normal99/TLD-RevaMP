using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RoadEnd
{
    /// The main road (the one with the power poles) stops at the end line; side roads, the land and everything on it
    /// go on. A main road's pieces (roadElementClass, ~100 m of 10 m bones each) are cut right
    /// after roadClass.Generate made them, before anything places them or shapes the terrain to them: pieces past the
    /// line go, the piece across it keeps its bones up to the line. The road's entries in the chunk's inner-chunk
    /// index (what UpdPlaceRemove places and RoadsNear finds) follow.
    [HarmonyPatch]
    public static class Roads
    {
        public static int Trimmed, Dropped;
        // the main road piece the road ends on (its power pole has nothing to string its wires to)
        static roadGenScript.roadClass _endRoad;
        static int _endElement = -1;

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdChunks))]
        [HarmonyPostfix]
        private static void AfterChunks(roadGenScript __instance)
        {
            try { EndPlan.Update(__instance); }
            catch (Exception e) { Plugin.Once("end plan", e); }
        }

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.Restart))]
        [HarmonyPostfix]
        private static void AfterRestart()
        {
            _endRoad = null; _endElement = -1;
            EndPlan.Reset();
            Grave.Reset();
        }

        [HarmonyPatch(typeof(roadGenScript.roadClass), nameof(roadGenScript.roadClass.Generate))]
        [HarmonyPostfix]
        private static void AfterGenerate(roadGenScript.roadClass __instance, roadGenScript _roadgen)
        {
            if (!Plugin.Enabled) return;
            try { Trim(__instance, _roadgen); }
            catch (Exception e) { Plugin.Once("road trim", e); }
        }

        /// Roads cut away entirely have no pieces: NearestPointOnRoadBone would answer the asked point itself for them
        /// (distance 0), so nothing may find them.
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.RoadsNear))]
        [HarmonyPostfix]
        private static void AfterRoadsNear(List<roadGenScript.roadClass> __result)
        {
            if (!Plugin.Enabled || __result == null) return;
            for (int i = __result.Count - 1; i >= 0; i--)
                if (__result[i] != null && __result[i].generated && __result[i].roadElements.Count == 0) __result.RemoveAt(i);
        }

        internal static void Trim(roadGenScript.roadClass road, roadGenScript rg)
        {
            var els = road.roadElements;
            if (els.Count == 0 || road.roadType != roadGenScript.roadTypeEmu.mainRoad) return;
            double cut = EndPlan.Current.Cut;
            bool any = false;
            foreach (var e in els) { foreach (var b in e.bonePos) if (EndPlan.U(b) > cut) { any = true; break; } if (any) break; }
            if (!any) return;

            // the road as one list of bones; a piece's last bone is the next one's first
            // keep one stretch on our side of the line: from the road's start up to where it first crosses, or (a road
            // coming from past the line) from where it last comes back
            bool startsInside = els[0].bonePos.Count > 0 && EndPlan.U(els[0].bonePos[0]) <= cut;
            var keep = new List<roadGenScript.roadElementClass>();
            var oldIndex = new List<int>();
            if (startsInside)
            {
                for (int i = 0; i < els.Count; i++)
                {
                    var bones = els[i].bonePos;
                    int n = 0;
                    while (n < bones.Count && EndPlan.U(bones[n]) <= cut) n++;
                    if (n == bones.Count) { keep.Add(els[i]); oldIndex.Add(i); continue; }
                    if (n >= 2) { Cut(els[i], 0, n, cut); keep.Add(els[i]); oldIndex.Add(i); }
                    else if (n == 1 && bones.Count >= 2 && keep.Count > 0) EndAtLine(keep[keep.Count - 1], OnLine(bones[0], bones[1], cut));
                    break;
                }
            }
            else
            {
                for (int i = els.Count - 1; i >= 0; i--)
                {
                    var bones = els[i].bonePos;
                    int n = 0;
                    while (n < bones.Count && EndPlan.U(bones[bones.Count - 1 - n]) <= cut) n++;
                    if (n == bones.Count) { keep.Insert(0, els[i]); oldIndex.Insert(0, i); continue; }
                    if (n >= 2) { Cut(els[i], bones.Count - n, bones.Count, cut); keep.Insert(0, els[i]); oldIndex.Insert(0, i); }
                    break;
                }
            }

            int before = els.Count;
            foreach (var e in els) if (!keep.Contains(e) && e.obj != null) UnityEngine.Object.Destroy(e.obj);
            els.Clear();
            els.AddRange(keep);
            Reindex(road, rg, oldIndex);
            if (keep.Count == 0) Dropped++; else Trimmed++;

            // the last piece left is the end of the road: the grave goes there
            if (keep.Count > 0)
            {
                var first = keep[0].bonePos; var last = keep[keep.Count - 1].bonePos;
                Vector3d at, from;
                if (startsInside) { at = last[last.Count - 1]; from = last.Count >= 2 ? last[last.Count - 2] : first[0]; }
                else { at = first[0]; from = first.Count >= 2 ? first[1] : last[last.Count - 1]; }
                if (Math.Abs(at.x) < 3000 && EndPlan.Current.Cut - EndPlan.U(at) < 30)
                {
                    EndPlan.SetRoadEnd(at, at - from);
                    _endRoad = road;
                    _endElement = startsInside ? keep.Count - 1 : 0;
                }
            }
            if (Plugin.Verbose) Plugin.Log.LogInfo($"road {road.selfChunk.z:0}/{road.selfID} {road.roadType}: {before} pieces -> {keep.Count}");
        }

        /// The last power pole: the game strings each pole's wires to the next piece's pole, and a pole with none keeps
        /// its wires at their prefab length, 100 m straight on into nothing. They go.
        [HarmonyPatch(typeof(roadscript), nameof(roadscript.PowerPole))]
        [HarmonyPostfix]
        private static void AfterPowerPole(roadscript __instance)
        {
            if (!Plugin.Enabled || _endRoad == null || __instance.element != _endElement) return;
            try
            {
                var rg = __instance.roadgen;
                if (rg == null || !rg.chunks.TryGetValue(__instance.chunk, out var ch) || __instance.roadID < 0 || __instance.roadID >= ch.roads.Count || ch.roads[__instance.roadID] != _endRoad) return;
                var rope = __instance.GetRope();
                if (rope == null || rope.ropes == null) return;
                foreach (var w in rope.ropes) if (w != null) w.localScale = Vector3.zero;
            }
            catch (Exception e) { Plugin.Once("last pole", e); }
        }

        /// Keep bones [from, to) of a piece; the bone on the far side becomes the crossing point on the line.
        private static void Cut(roadGenScript.roadElementClass e, int from, int to, double cut)
        {
            var bones = e.bonePos;
            bool tail = from == 0;   // the part kept is the piece's start: the line is after it
            int outside = tail ? to : from - 1;
            Vector3d cross = default;
            bool hasCross = outside >= 0 && outside < bones.Count;
            if (hasCross)
            {
                var inside = tail ? bones[to - 1] : bones[from];
                cross = OnLine(inside, bones[outside], cut);
            }
            var kept = bones.GetRange(from, to - from);
            if (hasCross)
            {
                // a crossing too close to the last bone kept would make a zero-length bone: move that bone instead
                var near = tail ? kept[kept.Count - 1] : kept[0];
                var flat = cross - near; flat.y = 0;
                if (flat.magnitude < 1.0) { if (tail) kept[kept.Count - 1] = cross; else kept[0] = cross; }
                else if (tail) kept.Add(cross); else kept.Insert(0, cross);
            }
            bones.Clear();
            bones.AddRange(kept);
        }

        /// The piece before a dropped one ended where the dropped one started, short of the line: it goes on to the line.
        private static void EndAtLine(roadGenScript.roadElementClass e, Vector3d cross)
        {
            var flat = cross - e.bonePos[e.bonePos.Count - 1]; flat.y = 0;
            if (flat.magnitude >= 1.0) e.bonePos.Add(cross);
        }

        private static Vector3d OnLine(Vector3d inside, Vector3d outside, double cut)
        {
            double ui = EndPlan.U(inside), uo = EndPlan.U(outside);
            double t = uo == ui ? 0 : (cut - ui) / (uo - ui);
            if (t < 0) t = 0; if (t > 1) t = 1;
            return inside + (outside - inside) * t;
        }

        /// The chunk's inner-chunk index lists piece numbers per road: renumber them after the cut, drop gone pieces.
        private static void Reindex(roadGenScript.roadClass road, roadGenScript rg, List<int> oldIndex)
        {
            if (rg == null || !rg.chunks.TryGetValue(road.selfChunk, out var chunk)) return;
            var map = new Dictionary<int, int>();
            for (int i = 0; i < oldIndex.Count; i++) map[oldIndex[i]] = i;
            foreach (var inner in chunk.innerChunks.Values)
            {
                for (int r = inner.roads.Count - 1; r >= 0; r--)
                {
                    var rc = inner.roads[r];
                    if (rc.roadId != road.selfID) continue;
                    var renumbered = new List<int>();
                    foreach (int e in rc.roadElements) if (map.TryGetValue(e, out int n) && !renumbered.Contains(n)) renumbered.Add(n);
                    rc.roadElements.Clear();
                    rc.roadElements.AddRange(renumbered);
                    if (renumbered.Count == 0) inner.roads.RemoveAt(r);
                }
            }
        }
    }
}
