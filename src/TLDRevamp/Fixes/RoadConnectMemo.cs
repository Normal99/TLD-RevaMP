using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Every frame roadGenScript destroys and rebuilds the ring of chunks around the needed area (see DotChunkMemo).
    /// The rebuilt chunks start with their "connected" flags cleared, so ConnectChunks re-runs the cross-chunk
    /// ConnectDots for the edge chunks every frame: measured 10 calls/frame, 40 ms/s CPU and 2.9 MB/s garbage while
    /// driving (pairwise dot tests plus HasCommonNeighbor, which allocates lists, plus a System.Random per road).
    ///
    /// ConnectDots(c1 ≠ c2) is a pure function of: the dots of c1, c2 and the extra chunk (plussz), the roads
    /// inside c1 and c2 (read by HasCommonNeighbor/NeighborDots), and generator constants (seed, hasMainRoad,
    /// emptySideRoadChance). Its only effect: ConnectWithRoad(c1, i1, c2, i2, roadSelf, type) calls in order, each
    /// followed by mainRoad.Add for main roads. We record those calls once per input fingerprint and replay them
    /// through the game's own ConnectWithRoad (so the road objects are created exactly as the game creates them).
    /// Verify: on a memo hit the original runs instead and its calls are compared with the memo.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.RoadConnectMemo.Enabled / .Verify / .MaxEntries
    [HarmonyPatch]
    public static class RoadConnectMemo
    {
        /// On by default since v0.20.0: exact (43,436 verified replays, 0 mismatches), driving A/B FPS +4.0%.
        public static bool Enabled = true;
        public static bool Verify = false;
        public static int MaxEntries = 4096;
        public static long Hits, Misses, Replayed, VerifyChecks, VerifyMismatches, Cleared;
        public static string LastMismatch = "";

        private struct Call { public int I1, I2; public roadGenScript.roadTypeEmu Type; }
        private sealed class Entry { public ulong Fingerprint; public Call[] Calls; }
        private sealed class Table { public readonly Dictionary<(Vector3d, Vector3d, Vector3d), Entry> Map = new Dictionary<(Vector3d, Vector3d, Vector3d), Entry>(); }
        private static readonly ConditionalWeakTable<roadGenScript, Table> Tables = new ConditionalWeakTable<roadGenScript, Table>();

        private static List<Call> _recording;

        private sealed class State { public roadGenScript Gen; public (Vector3d, Vector3d, Vector3d) Key; public ulong Fp; public Entry Expected; }

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.ConnectDots))]
        [HarmonyPrefix]
        private static bool Prefix(roadGenScript __instance, Vector3d _c1, Vector3d _c2, List<roadGenScript.dotClass> _d1,
                                   List<roadGenScript.dotClass> _d2, List<roadGenScript.dotClass> plussz, Vector3d _roadSelfChunk, out State __state)
        {
            __state = null;
            if (!Enabled || Eq(_c1, _c2) || _recording != null) return true;
            var gen = __instance;
            ulong fp = Fingerprint(gen, _c1, _c2, _d1, _d2, plussz);
            var key = (_c1, _c2, _roadSelfChunk);
            var table = Tables.GetValue(gen, _ => new Table());
            if (table.Map.TryGetValue(key, out var e) && e.Fingerprint == fp)
            {
                Hits++;
                if (!Verify)
                {
                    Replay(gen, _c1, _c2, _roadSelfChunk, e.Calls);
                    return false;
                }
                __state = new State { Gen = gen, Key = key, Fp = fp, Expected = e };
            }
            else
            {
                Misses++;
                __state = new State { Gen = gen, Key = key, Fp = fp };
            }
            _recording = new List<Call>();
            return true;
        }

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.ConnectDots))]
        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception, State __state)
        {
            if (__state == null) return __exception;
            var rec = _recording;
            _recording = null;
            if (__exception != null || rec == null) return __exception;
            if (__state.Expected != null)
            {
                VerifyChecks++;
                string diff = Compare(__state.Expected.Calls, rec);
                if (diff != null) { VerifyMismatches++; LastMismatch = __state.Key + ": " + diff; }
                return null;
            }
            var table = Tables.GetValue(__state.Gen, _ => new Table());
            if (table.Map.Count >= MaxEntries) { table.Map.Clear(); Cleared++; }
            table.Map[__state.Key] = new Entry { Fingerprint = __state.Fp, Calls = rec.ToArray() };
            return null;
        }

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.ConnectWithRoad))]
        [HarmonyPrefix]
        private static void OnConnectWithRoad(int _i1, int _i2, roadGenScript.roadTypeEmu _roadType)
        {
            _recording?.Add(new Call { I1 = _i1, I2 = _i2, Type = _roadType });
        }

        /// Vector3d.Equals(object) without boxing: the same comparison (double.Equals per component).
        internal static bool Eq(Vector3d a, Vector3d b) => a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);

        private static readonly AccessTools.FieldRef<roadGenScript, roadGenScript.dotChunkClass> Dccc = AccessTools.FieldRefAccess<roadGenScript, roadGenScript.dotChunkClass>("dccc");
        private static readonly AccessTools.FieldRef<roadGenScript, int> ConnectIndex = AccessTools.FieldRefAccess<roadGenScript, int>("connectIndex");
        public static bool ReuseRoads = true;
        public static bool ReplicaVerify = false;
        public static long RoadsReused, RoadsNew, ReplicaChecks, ReplicaMismatches;

        /// What the original's cross branch does per accepted pair: ConnectWithRoad (exact copy; c1 ≠ c2 here, so the
        /// same-chunk dot bookkeeping never applies) followed by mainRoad.Add for main roads. Road objects the ring
        /// chunk had last frame are reused when never generated (generated == false, no elements): such roads are
        /// never handed out by RoadsNear (it generates everything it returns), so nothing else can hold them.
        private static void Replay(roadGenScript gen, Vector3d c1, Vector3d c2, Vector3d roadSelf, Call[] calls)
        {
            var dcc = gen.chunks[roadSelf];
            var stash = ReuseRoads ? DotChunkMemo.StashFor(gen, roadSelf) : null;
            foreach (var c in calls)
            {
                Dccc(gen) = dcc;
                int idx = dcc.roads.Count;
                ConnectIndex(gen) = idx;
                var p1 = gen.chunks[c1].dots[c.I1].pos;
                var p2 = gen.chunks[c2].dots[c.I2].pos;
                roadGenScript.roadClass r = null;
                if (stash != null)
                {
                    while (stash.Count > 0 && r == null)
                    {
                        var cand = stash[stash.Count - 1];
                        stash.RemoveAt(stash.Count - 1);
                        if (cand != null && !cand.generated && cand.roadElements.Count == 0) r = cand;
                    }
                }
                if (r != null)
                {
                    r.chunk1 = c1; r.id1 = c.I1; r.chunk2 = c2; r.id2 = c.I2; r.start = p1; r.end = p2;
                    r.terrainAlignMinDist = gen.terrainAlignMinDist; r.terrainAlignMaxDist = gen.terrainAlignMaxDist;
                    r.selfChunk = roadSelf; r.selfID = idx; r.roadType = c.Type;
                    RoadsReused++;
                }
                else
                {
                    r = new roadGenScript.roadClass(c1, c.I1, c2, c.I2, p1, p2, gen.terrainAlignMinDist, gen.terrainAlignMaxDist, roadSelf, idx, c.Type);
                    RoadsNew++;
                }
                dcc.roads.Add(r);
                if (c.Type == roadGenScript.roadTypeEmu.mainRoad && c1.z > c2.z) r.Swap();
                if (ReplicaVerify) CheckReplica(r, c1, c.I1, c2, c.I2, p1, p2, gen, roadSelf, idx, c.Type);
                if (c.Type == roadGenScript.roadTypeEmu.mainRoad) dcc.mainRoad.Add(dcc.roads.Count - 1);
                Replayed++;
            }
        }

        private static void CheckReplica(roadGenScript.roadClass r, Vector3d c1, int i1, Vector3d c2, int i2, Vector3d p1, Vector3d p2,
                                         roadGenScript gen, Vector3d roadSelf, int idx, roadGenScript.roadTypeEmu type)
        {
            ReplicaChecks++;
            var f = new roadGenScript.roadClass(c1, i1, c2, i2, p1, p2, gen.terrainAlignMinDist, gen.terrainAlignMaxDist, roadSelf, idx, type);
            if (type == roadGenScript.roadTypeEmu.mainRoad && c1.z > c2.z) f.Swap();
            bool same = Eq(f.chunk1, r.chunk1) && f.id1 == r.id1 && Eq(f.chunk2, r.chunk2) && f.id2 == r.id2 && Eq(f.start, r.start) && Eq(f.end, r.end)
                        && f.terrainAlignMinDist == r.terrainAlignMinDist && f.terrainAlignMaxDist == r.terrainAlignMaxDist && Eq(f.selfChunk, r.selfChunk)
                        && f.selfID == r.selfID && f.roadType == r.roadType && f.generated == r.generated && r.roadElements.Count == 0;
            if (!same) { ReplicaMismatches++; LastMismatch = "replica " + roadSelf + " #" + idx; }
        }

        private static string Compare(Call[] expected, List<Call> actual)
        {
            if (expected.Length != actual.Count) return "count " + expected.Length + " vs " + actual.Count;
            for (int i = 0; i < expected.Length; i++)
                if (expected[i].I1 != actual[i].I1 || expected[i].I2 != actual[i].I2 || expected[i].Type != actual[i].Type)
                    return "call " + i;
            return null;
        }

        /// Everything the cross branch of ConnectDots reads, hashed (64-bit FNV-1a over exact bit patterns).
        private static ulong Fingerprint(roadGenScript gen, Vector3d c1, Vector3d c2, List<roadGenScript.dotClass> d1,
                                         List<roadGenScript.dotClass> d2, List<roadGenScript.dotClass> plussz)
        {
            ulong h = 14695981039346656037UL;
            h = Mix(h, gen.mapSetting != null ? gen.mapSetting.seed : 0);
            h = Mix(h, gen.hasMainRoad ? 1 : 0);
            h = Mix(h, BitConverter.SingleToInt32Bits(gen.emptySideRoadChance));
            h = Dots(h, d1);
            h = Dots(h, d2);
            h = plussz == null ? Mix(h, -1) : Dots(h, plussz);
            h = InsideRoads(h, gen, c1);
            h = InsideRoads(h, gen, c2);
            return h;
        }

        private static ulong Dots(ulong h, List<roadGenScript.dotClass> dots)
        {
            h = Mix(h, dots.Count);
            foreach (var d in dots)
            {
                h = Mix(h, BitConverter.DoubleToInt64Bits(d.pos.x));
                h = Mix(h, BitConverter.DoubleToInt64Bits(d.pos.y));
                h = Mix(h, BitConverter.DoubleToInt64Bits(d.pos.z));
            }
            return h;
        }

        /// NeighborDots reads, for a chunk, every road whose chunk1 equals its chunk2 (ids and the dots they point at).
        private static ulong InsideRoads(ulong h, roadGenScript gen, Vector3d c)
        {
            if (!gen.chunks.TryGetValue(c, out var dcc)) return Mix(h, -2);
            foreach (var r in dcc.roads)
            {
                if (!Eq(r.chunk1, r.chunk2)) continue;
                h = Mix(h, r.id1);
                h = Mix(h, r.id2);
                h = Mix(h, r.chunk1.GetHashCode());
            }
            return Mix(h, -3);
        }

        private static ulong Mix(ulong h, long v)
        {
            for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211UL; }
            return h;
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"verify\":" + (Verify ? "true" : "false") +
            ",\"hits\":" + Hits + ",\"misses\":" + Misses + ",\"replayed\":" + Replayed + ",\"cleared\":" + Cleared +
            ",\"roadsReused\":" + RoadsReused + ",\"roadsNew\":" + RoadsNew + ",\"replicaChecks\":" + ReplicaChecks + ",\"replicaMismatches\":" + ReplicaMismatches +
            ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches + ",\"lastMismatch\":" + Json.Str(LastMismatch) + "}";
    }
}
