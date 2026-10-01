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
    /// the result is identical. (Verify mode recomputes the original and compares.)
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

        // Tiny slack so rounding in the game's lerp can never make a skipped road the true winner
        private const double Slack = 1e-6;

        private sealed class Box { public double MinX, MaxX, MinZ, MaxZ; public bool Valid; }
        private static readonly ConditionalWeakTable<roadGenScript.roadClass, Box> Boxes = new ConditionalWeakTable<roadGenScript.roadClass, Box>();

        [HarmonyPrefix]
        private static bool Prefix(Vector3d _gpos, List<roadGenScript.roadClass> _nearRoads, ref Vector3d _result, ref roadGenScript.roadClass __result)
        {
            if (!Enabled) return true;
            try
            {
                __result = Pruned(_gpos, _nearRoads, out _result, out long checkedN, out long skippedN);
                System.Threading.Interlocked.Increment(ref Calls);
                System.Threading.Interlocked.Add(ref RoadsChecked, checkedN);
                System.Threading.Interlocked.Add(ref RoadsSkipped, skippedN);
                if (Verify)
                {
                    System.Threading.Interlocked.Increment(ref VerifyChecks);
                    var orig = Original(_gpos, _nearRoads, out var origPoint);
                    if (!ReferenceEquals(orig, __result) || !origPoint.Equals(_result))
                    {
                        System.Threading.Interlocked.Increment(ref VerifyMismatches);
                        LastMismatch = $"gpos {_gpos}: game {origPoint} ours {_result}";
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
        /// Below this many roads, sorting costs more than it saves: plain loop (identical to the original).
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
            if (n < SortThreshold)
            {
                checkedN = n;
                return Original(_gpos, roads, out _result);
            }
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
                Vector3d vector3d = roadGenScript.NearestPointOnRoadBone(_gpos, road);
                Vector3d vector3d2 = vector3d;
                vector3d2.y = _gpos.y;
                double magnitude = (vector3d2 - _gpos).magnitude;
                // Original: first road with strictly smaller distance than the running best (which starts at MaxValue)
                if (magnitude < best || (result != null && magnitude == best && i < bestIndex))
                {
                    best = magnitude;
                    bestIndex = i;
                    _result = vector3d;
                    result = road;
                }
            }
            return result;
        }

        /// The game's loop, verbatim, for verify mode.
        private static roadGenScript.roadClass Original(Vector3d _gpos, List<roadGenScript.roadClass> _nearRoads, out Vector3d _result)
        {
            _result = _gpos;
            roadGenScript.roadClass result = null;
            double num = double.MaxValue;
            if (_nearRoads != null)
            {
                for (int i = 0; i < _nearRoads.Count; i++)
                {
                    Vector3d vector3d = roadGenScript.NearestPointOnRoadBone(_gpos, _nearRoads[i]);
                    Vector3d vector3d2 = vector3d;
                    vector3d2.y = _gpos.y;
                    double magnitude = (vector3d2 - _gpos).magnitude;
                    if (magnitude < num)
                    {
                        num = magnitude;
                        _result = vector3d;
                        result = _nearRoads[i];
                    }
                }
            }
            return result;
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
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"calls\":" + Calls + ",\"roadsChecked\":" + RoadsChecked +
            ",\"roadsSkipped\":" + RoadsSkipped + ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches +
            ",\"lastMismatch\":" + Json.Str(LastMismatch) + "}";
    }
}
