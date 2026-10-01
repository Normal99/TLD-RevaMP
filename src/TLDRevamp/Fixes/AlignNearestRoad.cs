using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Game bug (building placement, poiGenScript.AlignPosToRoad): to find the road nearest to one building, the game calls
    /// roadGenScript.RoadsNear(pos, notJustChunks: true). When no suitable road is registered in the few inner chunks
    /// around the building, that falls back to every road of the 3×3 road chunks (30 × 30 km) or even of all loaded
    /// chunks, and generates every one of them (≈1 ms each) before picking the nearest. Measured on arrival: 160 of 256
    /// building placements took the fallback, generating 20–84 roads at once, up to 47 ms in one frame.
    ///
    /// Fix: the same candidate list (exact copy of RoadsNear's collection, same order), but a road is only generated if
    /// it can still be the nearest. A road's bones stay within MaxLateral of the straight line between its end dots
    /// (the road noise is 2D simplex noise ×70, provably |n| ≤ 2.74, summed over the octaves with amplitude×persistence^i,
    /// times a factor ≤ 1; bone stepping adds ≤ 10 m; +20 m margin). The game's per-road nearest point is never closer
    /// than the road's true nearest bone, so a road whose straight line is more than MaxLateral farther than the best road
    /// found so far can't be chosen. Those roads are left for the game's normal per-frame road generation.
    /// Result: the same road and the same point for the building (Verify compares with the original on every call);
    /// only the moment those far roads get generated changes (later, spread over frames, as without this building).
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.AlignNearestRoad.Enabled / .Verify
    [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AlignPosToRoad))]
    public static class AlignNearestRoad
    {
        /// On by default since v0.29.0: verify 2 × 256 placements, 0 mismatches; arrivals: worst frame 141/106 → 51/55 ms,
        /// frames > 50 ms 7/5 → 2/2 (4 areas, off,on,on,off).
        public static bool Enabled = true;
        public static bool Verify = false;
        public static long Calls, Candidates, Generated, Pruned, VerifyChecks, VerifyMismatches;
        public static string LastMismatch = "";
        public static int Patched;

        private const double NoiseBound = 2.74, StepSlack = 30.0;

        private static readonly MethodInfo RoadsNearM = AccessTools.Method(typeof(roadGenScript), nameof(roadGenScript.RoadsNear),
            new[] { typeof(Vector3d), typeof(bool), typeof(poiGenScript.alignToRoadEmu) });
        private static readonly MethodInfo Replacement = AccessTools.Method(typeof(AlignNearestRoad), nameof(Candidates_));

        private static readonly AccessTools.FieldRef<roadGenScript, int> Octaves = AccessTools.FieldRefAccess<roadGenScript, int>("octaves");
        private static readonly AccessTools.FieldRef<roadGenScript, double> Ampl = AccessTools.FieldRefAccess<roadGenScript, double>("ampl");
        private static readonly AccessTools.FieldRef<roadGenScript, double> Persistence = AccessTools.FieldRefAccess<roadGenScript, double>("persistence");
        private static readonly AccessTools.FieldRef<roadGenScript, int> BAround = AccessTools.FieldRefAccess<roadGenScript, int>("baround");
        private static readonly AccessTools.FieldRef<roadGenScript, int> IAround = AccessTools.FieldRefAccess<roadGenScript, int>("iaround");
        private static readonly AccessTools.FieldRef<roadGenScript, float> InnerChunkSize = AccessTools.FieldRefAccess<roadGenScript, float>("innerChunkSize");

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                if (ins.Calls(RoadsNearM)) { ins.opcode = OpCodes.Call; ins.operand = Replacement; Patched++; }
                yield return ins;
            }
        }

        /// Replaces `roadgen.RoadsNear(_gpos, true, align)` inside AlignPosToRoad (stack: roadgen, pos, bool, align).
        public static List<roadGenScript.roadClass> Candidates_(roadGenScript g, Vector3d pos, bool notJustChunks, poiGenScript.alignToRoadEmu align)
        {
            if (!Enabled) return g.RoadsNear(pos, notJustChunks, align);
            Calls++;
            var list = Collect(g, pos, notJustChunks, align);
            Candidates += list.Count;

            // Best distance among roads that already exist (the same per-road measure NearestPointOnRoadBone uses)
            double best = double.MaxValue;
            foreach (var r in list)
                if (r.generated && r.roadElements.Count > 0) best = System.Math.Min(best, RoadDist(pos, r));

            // Ungenerated candidates, most promising first (lowest lower bound): the true nearest is found after one or two
            // generations and tightens `best`, so the rest can be pruned. Which road wins doesn't depend on this order.
            double lateral = MaxLateral(g);
            var pending = new List<(double lb, roadGenScript.roadClass r)>();
            foreach (var r in list)
                if (!r.generated) pending.Add((SegmentDist(pos, r.start, r.end) - lateral, r));
            pending.Sort((x, y) => x.lb.CompareTo(y.lb));
            var generatedNow = new HashSet<roadGenScript.roadClass>();
            foreach (var (lb, r) in pending)
            {
                if (lb > best) { Pruned++; continue; } // can't be nearer than a road we already have
                r.Generate(g);                          // same call RoadsNear would have made for it
                Generated++;
                generatedNow.Add(r);
                if (r.generated && r.roadElements.Count > 0) best = System.Math.Min(best, RoadDist(pos, r));
            }
            var keep = new List<roadGenScript.roadClass>(list.Count);
            foreach (var r in list) // original order (NearestPointOnRoadBone's tie-break)
                if (r.generated || generatedNow.Contains(r)) keep.Add(r);

            if (Verify)
            {
                VerifyChecks++;
                var a = roadGenScript.NearestPointOnRoadBone(pos, keep, out var pa);
                var full = g.RoadsNear(pos, notJustChunks, align); // the original: generates everything
                var b = roadGenScript.NearestPointOnRoadBone(pos, full, out var pb);
                if (!ReferenceEquals(a, b) || !pa.Equals(pb))
                {
                    VerifyMismatches++;
                    LastMismatch = "pos " + pos + ": ours " + (a != null ? a.selfChunk + "#" + a.selfID : "null") + " " + pa + " vs " + (b != null ? b.selfChunk + "#" + b.selfID : "null") + " " + pb;
                }
            }
            return keep;
        }

        /// Exact copy of roadGenScript.RoadsNear's collection (everything except its final "generate all" loop).
        private static List<roadGenScript.roadClass> Collect(roadGenScript g, Vector3d _pos, bool _notJustChunks, poiGenScript.alignToRoadEmu _align)
        {
            var list = new List<roadGenScript.roadClass>();
            var list2 = new List<Vector3d>();
            var list3 = new List<Vector3d>();
            int baround = BAround(g), iaround = IAround(g);
            for (int i = -baround; i <= baround; i++)
                for (int j = -baround; j <= baround; j++)
                {
                    Vector3d chunkPos = mainscript.GetChunkPos(_pos, j, i, g.chunkSize);
                    if (g.chunks.ContainsKey(chunkPos)) list2.Add(chunkPos);
                }
            for (int k = -iaround; k <= iaround; k++)
                for (int l = -iaround; l <= iaround; l++)
                    list3.Add(mainscript.GetChunkPos(_pos, l, k, InnerChunkSize(g)));
            foreach (Vector3d item in list2)
            {
                for (int m = 0; m < list3.Count; m++)
                {
                    if (!g.chunks[item].innerChunks.TryGetValue(list3[m], out var icc0) || icc0.roads == null) continue;
                    for (int n = 0; n < icc0.roads.Count; n++)
                        if (!g.hasMainRoad || g.CheckAlignRoad(g.chunks[item].roads[icc0.roads[n].roadId].roadType, _align))
                            list.Add(g.chunks[item].roads[icc0.roads[n].roadId]);
                }
            }
            if (_notJustChunks)
            {
                if (list.Count == 0)
                    foreach (Vector3d item2 in list2)
                        foreach (var road in g.chunks[item2].roads)
                            if (!g.hasMainRoad || g.CheckAlignRoad(road.roadType, _align)) list.Add(road);
                if (list.Count == 0)
                    foreach (var chunk in g.chunks)
                        foreach (var road2 in chunk.Value.roads)
                            if (!g.hasMainRoad || g.CheckAlignRoad(road2.roadType, _align)) list.Add(road2);
            }
            return list;
        }

        /// The distance NearestPointOnRoadBone(list) compares for one road (horizontal, y set to the query's).
        private static double RoadDist(Vector3d pos, roadGenScript.roadClass r)
        {
            var p = roadGenScript.NearestPointOnRoadBone(pos, r);
            p.y = pos.y;
            return (p - pos).magnitude;
        }

        private static double MaxLateral(roadGenScript g)
        {
            double sum = 0, w = 1;
            for (int i = 0; i <= Octaves(g); i++) { sum += w; w *= Persistence(g); }
            return NoiseBound * System.Math.Abs(Ampl(g)) * sum + StepSlack;
        }

        /// Horizontal distance from p to the segment a–b.
        private static double SegmentDist(Vector3d p, Vector3d a, Vector3d b)
        {
            double ax = a.x, az = a.z, bx = b.x - ax, bz = b.z - az, px = p.x - ax, pz = p.z - az;
            double len2 = bx * bx + bz * bz;
            double t = len2 > 0 ? System.Math.Max(0, System.Math.Min(1, (px * bx + pz * bz) / len2)) : 0;
            double dx = px - t * bx, dz = pz - t * bz;
            return System.Math.Sqrt(dx * dx + dz * dz);
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"patched\":" + Patched + ",\"calls\":" + Calls + ",\"candidates\":" + Candidates +
            ",\"generated\":" + Generated + ",\"pruned\":" + Pruned + ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches +
            ",\"lastMismatch\":" + Json.Str(LastMismatch) + "}";
    }
}
