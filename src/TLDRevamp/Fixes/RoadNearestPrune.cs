using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// roadGenScript.NearestPointOnRoadBone(gpos, roads, out result) runs the per-road nearest-point search on every
    /// road in the list and keeps the strictly closest (XZ). poiGenScript.AlignPosToRoad passes it *every road in
    /// every loaded chunk* when a building has no road nearby: up to ~40 ms in one call (profiled while driving).
    ///
    /// The per-road result is a lerp between two of that road's bone points, so it lies inside the XZ bounding box
    /// of the road's bones. The distance from gpos to that box is therefore a lower bound on the road's distance.
    /// Roads whose bound can't beat the best so far are skipped. Same order, same strict "<" as the original, so
    /// the result is identical. (Verify mode runs the game's own method too, a runtime copy made by a reverse patch, and
    /// compares.) Short lists are left to the game's method.
    ///
    /// Boxes are cached per road once `generated` is true. Bones are only added/changed inside roadClass.Generate,
    /// before that flag is set. Also called from AsyncTerrain workers, so everything here is thread-safe.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.RoadNearestPrune.Enabled / .Verify
    [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.NearestPointOnRoadBone),
        new[] { typeof(Vector3d), typeof(List<roadGenScript.roadClass>), typeof(Vector3d) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
    public static class RoadNearestPrune
    {
        public static bool Enabled = true;
        public static bool Verify = false;

        public static long Calls, RoadsChecked, RoadsSkipped, VerifyChecks, VerifyMismatches;
        public static string LastMismatch = "";
        public static int Patched;   // the game's own method copied for verify mode

        // Tiny slack so rounding in the game's lerp can never make a skipped road the true winner
        private const double Slack = 1e-6;

        private sealed class Box { public double MinX, MaxX, MinZ, MaxZ; public bool Valid; }
        private static readonly ConditionalWeakTable<roadGenScript.roadClass, Box> Boxes = new ConditionalWeakTable<roadGenScript.roadClass, Box>();

        [HarmonyPrefix]
        private static bool Prefix(Vector3d _gpos, List<roadGenScript.roadClass> _nearRoads, ref Vector3d _result, ref roadGenScript.roadClass __result)
        {
            if (!Enabled || _nearRoads == null) return true;
            if (_nearRoads.Count < SortThreshold && !Verify)
            {
                System.Threading.Interlocked.Increment(ref Calls);
                System.Threading.Interlocked.Add(ref RoadsChecked, _nearRoads.Count);
                return true;   // the game's own loop: sorting costs more than it saves
            }
            try
            {
                __result = Pruned(_gpos, _nearRoads, out _result, out long checkedN, out long skippedN);
                System.Threading.Interlocked.Increment(ref Calls);
                System.Threading.Interlocked.Add(ref RoadsChecked, checkedN);
                System.Threading.Interlocked.Add(ref RoadsSkipped, skippedN);
                if (Verify)
                {
                    System.Threading.Interlocked.Increment(ref VerifyChecks);
                    var theirs = Original(_gpos, _nearRoads, out var theirPoint);
                    if (!ReferenceEquals(theirs, __result) || !theirPoint.Equals(_result))
                    {
                        System.Threading.Interlocked.Increment(ref VerifyMismatches);
                        LastMismatch = $"gpos {_gpos}: game {theirPoint} ours {_result}";
                    }
                }
                return false;
            }
            catch
            {
                // Anything odd (e.g. a list changing under a worker thread): let the game's own code handle this call
                return true;
            }
        }

        [ThreadStatic] private static double[] _lb, _keys;
        [ThreadStatic] private static int[] _order;
        /// Below this many roads, sorting costs more than it saves: the game's own loop.
        public static int SortThreshold = 8;

        /// Evaluates roads in increasing lower-bound order and stops once the next bound exceeds the best distance.
        /// Returns the minimum distance with ties going to the lowest original index, which is exactly what the original's
        /// first-strictly-smaller loop returns. Roads without a usable box get bound 0, so they're always evaluated.
        private static roadGenScript.roadClass Pruned(Vector3d _gpos, List<roadGenScript.roadClass> roads, out Vector3d _result,
            out long checkedN, out long skippedN)
        {
            _result = _gpos;
            checkedN = skippedN = 0;
            if (roads == null) return null;
            int n = roads.Count;
            if (_lb == null || _lb.Length < n) { _lb = new double[Math.Max(n, 64)]; _keys = new double[Math.Max(n, 64)]; _order = new int[Math.Max(n, 64)]; }
            for (int i = 0; i < n; i++)
            {
                var road = roads[i];
                double lb = 0.0;
                if (road != null && road.generated)
                {
                    var box = Boxes.GetValue(road, BuildBox);
                    if (box.Valid) lb = LowerBound(box, _gpos);
                }
                _lb[i] = lb;
                _keys[i] = lb;
                _order[i] = i;
            }
            // Order among equal bounds doesn't matter: the tie rule below picks the lowest index regardless
            Array.Sort(_keys, _order, 0, n);

            roadGenScript.roadClass result = null;
            double best = double.MaxValue;
            int bestIndex = int.MaxValue;
            for (int k = 0; k < n; k++)
            {
                int i = _order[k];
                if (_lb[i] > best) { skippedN += n - k; break; } // every remaining road is strictly farther
                checkedN++;
                var road = roads[i];
                Vector3d onRoad = roadGenScript.NearestPointOnRoadBone(_gpos, road);
                Vector3d level = onRoad;
                level.y = _gpos.y;   // the distance is measured flat, at the query's height
                double dist = (level - _gpos).magnitude;
                // the game's rule: the first road strictly nearer than the best so far (which starts at MaxValue)
                if (dist < best || (result != null && dist == best && i < bestIndex))
                {
                    best = dist;
                    bestIndex = i;
                    _result = onRoad;
                    result = road;
                }
            }
            return result;
        }

        /// The game's own NearestPointOnRoadBone(gpos, roads, out result), copied at runtime (not patched), for verify mode.
        [HarmonyReversePatch(HarmonyReversePatchType.Original)]
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.NearestPointOnRoadBone),
            new[] { typeof(Vector3d), typeof(List<roadGenScript.roadClass>), typeof(Vector3d) },
            new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
        public static roadGenScript.roadClass Original(Vector3d _gpos, List<roadGenScript.roadClass> _nearRoads, out Vector3d _result)
        {
            IEnumerable<CodeInstruction> Made(IEnumerable<CodeInstruction> instructions) { Patched = 1; return instructions; }   // the copy exists
            _ = Made(null);
            throw new NotImplementedException("a reverse patch: replaced at runtime");
        }

        private static Box BuildBox(roadGenScript.roadClass road)
        {
            var b = new Box { MinX = double.MaxValue, MaxX = double.MinValue, MinZ = double.MaxValue, MaxZ = double.MinValue };
            int points = 0;
            foreach (var el in road.roadElements)
            {
                if (el?.bonePos == null) continue;
                foreach (var p in el.bonePos)
                {
                    if (p.x < b.MinX) b.MinX = p.x;
                    if (p.x > b.MaxX) b.MaxX = p.x;
                    if (p.z < b.MinZ) b.MinZ = p.z;
                    if (p.z > b.MaxZ) b.MaxZ = p.z;
                    points++;
                }
            }
            // No bones: the game's search returns Vector3d.zero-based results we don't bound, so never skip such roads
            b.Valid = points > 0 && !double.IsNaN(b.MinX) && !double.IsNaN(b.MinZ);
            return b;
        }

        private static double LowerBound(Box b, Vector3d p)
        {
            double dx = Math.Max(0.0, Math.Max(b.MinX - p.x, p.x - b.MaxX));
            double dz = Math.Max(0.0, Math.Max(b.MinZ - p.z, p.z - b.MaxZ));
            return Math.Max(0.0, Math.Sqrt(dx * dx + dz * dz) - Slack);
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"patched\":" + Patched + ",\"calls\":" + Calls + ",\"roadsChecked\":" + RoadsChecked +
            ",\"roadsSkipped\":" + RoadsSkipped + ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches +
            ",\"lastMismatch\":" + Json.Str(LastMismatch) + "}";
    }
}
