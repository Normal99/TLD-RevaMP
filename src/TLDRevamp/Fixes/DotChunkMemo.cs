using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Every frame roadGenScript.UpdChunksNeeded marks all chunks outside the needed area for removal, UpdChunks
    /// removes them, and then (same call) recreates the 3×3 neighbours of every needed chunk via UpdDotChunk.
    /// The ring of neighbour chunks is therefore destroyed and rebuilt every frame. Each rebuild samples terrain
    /// Height for every dot (~25k Height calls/s on the main thread, profiled 2026-09-24).
    ///
    /// A dot chunk's content is deterministic: seed = hash(cpos) + map seed, dots from that Random and terrain height.
    /// We remember, per chunk position, the dots and the Random's internal state exactly as UpdDotChunk leaves them.
    /// When the same chunk is rebuilt, the chunk object is still new (as in the original: flags reset, no roads),
    /// but it's filled from memory instead of recomputed. Verify mode lets the original run on memory hits and compares.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.DotChunkMemo.Enabled / .Verify / .MaxEntries
    [HarmonyPatch]
    public static class DotChunkMemo
    {
        public static bool Enabled = true;
        public static bool Verify = false;
        public static int MaxEntries = 20000;

        public static long Hits, Misses, VerifyChecks, VerifyMismatches, Clears;

        /// Reuse (v0.19.0): the ring chunks are removed and recreated in the same UpdChunks call every frame; each
        /// recreation allocated a chunk object, 3 lists, a dictionary, a Random + int[56] and ~9 dots with lists
        /// (measured: roads ≈ 40 KB/frame of garbage standing still, 60% of all managed allocation → GC every ~2 s).
        /// A chunk removed in this call is handed back to the game's own chunks.Add (same dictionary operations, same
        /// order) reset to exactly the fresh state: memo dots, cleared lists/flags/dictionary, Random state copied.
        /// Only roadGenScript touches dotChunkClass/dotClass objects, and only through `chunks` or transient fields.
        /// On by default since v0.20.0: exact (90,742 verified reuses, 0 mismatches), driving A/B GCs −39%, FPS +1.4%.
        public static bool Reuse = true;
        public static bool ReuseVerify = false;
        public static long Reused, ReuseChecks, ReuseMismatches;
        public static string LastReuseMismatch = "";
        private static readonly ConditionalWeakTable<roadGenScript, Dictionary<Vector3d, roadGenScript.dotChunkClass>> Pool =
            new ConditionalWeakTable<roadGenScript, Dictionary<Vector3d, roadGenScript.dotChunkClass>>();
        public static string LastMismatch = "";

        private sealed class Entry
        {
            public int Seed, DotCount, Inext, Inextp;
            public int[] SeedArray;
            public Vector3d[] Dots;
        }

        private static readonly ConditionalWeakTable<roadGenScript, Dictionary<Vector3d, Entry>> Memo =
            new ConditionalWeakTable<roadGenScript, Dictionary<Vector3d, Entry>>();

        private static readonly AccessTools.FieldRef<roadGenScript, roadGenScript.dotChunkClass> Dcc0 =
            AccessTools.FieldRefAccess<roadGenScript, roadGenScript.dotChunkClass>("dcc0");
        private static readonly AccessTools.FieldRef<System.Random, int> InextRef = AccessTools.FieldRefAccess<System.Random, int>("_inext");
        private static readonly AccessTools.FieldRef<System.Random, int> InextpRef = AccessTools.FieldRefAccess<System.Random, int>("_inextp");
        private static readonly AccessTools.FieldRef<System.Random, int[]> SeedArrayRef = AccessTools.FieldRefAccess<System.Random, int[]>("_seedArray");

        private enum Pending { None, Record, Compare }

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdDotChunk))]
        [HarmonyPrefix]
        private static bool Prefix(roadGenScript __instance, Vector3d _cPos, out Pending __state)
        {
            __state = Pending.None;
            if (!Enabled) return true;
            var gen = __instance;
            // Same early return as the original: nothing to do
            if (gen.chunks.ContainsKey(_cPos) || (!gen.hasSideRoad && gen.hasMainRoad && _cPos.x != 0.0)) return true;

            var memo = Memo.GetValue(gen, _ => new Dictionary<Vector3d, Entry>());
            if (memo.TryGetValue(_cPos, out var e))
            {
                if (Verify) { __state = Pending.Compare; return true; }
                Hits++;
                if (Reuse && Pool.TryGetValue(gen, out var pool) && pool.TryGetValue(_cPos, out var old) && old.dots.Count == e.Dots.Length)
                {
                    pool.Remove(_cPos);
                    _resetGen = gen; _resetPos = _cPos;
                    ResetTo(old, e);
                    _resetGen = null;
                    gen.chunks.Add(_cPos, old);
                    Dcc0(gen) = old;
                    Reused++;
                    if (ReuseVerify) CompareWithFresh(old, e, _cPos);
                    return false;
                }
                var dcc = new roadGenScript.dotChunkClass();
                gen.chunks.Add(_cPos, dcc);
                Dcc0(gen) = dcc;
                dcc.dots.Clear();
                dcc.seed = e.Seed;
                dcc.rnd = new System.Random(e.Seed);
                InextRef(dcc.rnd) = e.Inext;
                InextpRef(dcc.rnd) = e.Inextp;
                Array.Copy(e.SeedArray, SeedArrayRef(dcc.rnd), e.SeedArray.Length);
                dcc.dotCount = e.DotCount;
                for (int i = 0; i < e.Dots.Length; i++) dcc.dots.Add(new roadGenScript.dotClass(e.Dots[i]));
                return false;
            }
            Misses++;
            __state = Pending.Record;
            return true;
        }

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdDotChunk))]
        [HarmonyPostfix]
        private static void Postfix(roadGenScript __instance, Vector3d _cPos, Pending __state)
        {
            if (__state == Pending.None) return;
            if (!__instance.chunks.TryGetValue(_cPos, out var dcc) || dcc.rnd == null) return;
            var fresh = Snapshot(dcc);
            var memo = Memo.GetValue(__instance, _ => new Dictionary<Vector3d, Entry>());
            if (__state == Pending.Record)
            {
                if (memo.Count >= MaxEntries) { memo.Clear(); Clears++; }
                memo[_cPos] = fresh;
                return;
            }
            // Verify: the original just ran; its result must equal what we would have replayed
            VerifyChecks++;
            var e = memo[_cPos];
            string diff = null;
            if (e.Seed != fresh.Seed || e.DotCount != fresh.DotCount) diff = "seed/dotCount";
            else if (e.Inext != fresh.Inext || e.Inextp != fresh.Inextp) diff = "rnd position";
            else if (e.Dots.Length != fresh.Dots.Length) diff = "dot count";
            else
            {
                for (int i = 0; i < e.SeedArray.Length && diff == null; i++) if (e.SeedArray[i] != fresh.SeedArray[i]) diff = "rnd seed array";
                for (int i = 0; i < e.Dots.Length && diff == null; i++)
                    if (!e.Dots[i].Equals(fresh.Dots[i])) diff = $"dot {i}: memo {e.Dots[i]} game {fresh.Dots[i]}";
            }
            if (diff != null)
            {
                VerifyMismatches++;
                LastMismatch = $"chunk {_cPos}: {diff}";
                if (VerifyMismatches <= 5) Plugin.Log.LogWarning("DotChunkMemo verify mismatch " + LastMismatch);
            }
        }

        /// Chunks this UpdChunks call will remove (in chunksToRemove order, until the first one whose removal the
        /// game would split across frames because a road element object still exists).
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdChunks))]
        [HarmonyPrefix]
        private static void BeforeUpdChunks(roadGenScript __instance)
        {
            if (!Reuse || !Enabled) return;
            var pool = Pool.GetValue(__instance, _ => new Dictionary<Vector3d, roadGenScript.dotChunkClass>());
            pool.Clear();
            // Unconsumed stashes from last frame: drop them (they would only hold references)
            if (Stashes.TryGetValue(__instance, out var st)) foreach (var l in st.Values) l.Clear();
            foreach (var key in __instance.chunksToRemove)
            {
                if (!__instance.chunks.TryGetValue(key, out var dcc)) break;
                bool live = false;
                foreach (var r in dcc.roads)
                {
                    foreach (var el in r.roadElements) if (el.obj != null) { live = true; break; }
                    if (live) break;
                }
                if (live) break; // the game may stop here this frame; don't touch this or later ones
                if (!pool.ContainsKey(key)) pool.Add(key, dcc);
            }
        }

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdChunks))]
        [HarmonyPostfix]
        private static void AfterUpdChunks(roadGenScript __instance)
        {
            if (Pool.TryGetValue(__instance, out var pool)) pool.Clear(); // unused ones become garbage, as in the game
        }

        /// The state UpdDotChunk (memo path) gives a new chunk, written into an existing object.
        private static roadGenScript _resetGen;
        private static Vector3d _resetPos;
        private static readonly ConditionalWeakTable<roadGenScript, Dictionary<Vector3d, List<roadGenScript.roadClass>>> Stashes =
            new ConditionalWeakTable<roadGenScript, Dictionary<Vector3d, List<roadGenScript.roadClass>>>();

        /// Road objects of a ring chunk as it was before this frame's rebuild (consumed by RoadConnectMemo.Replay).
        internal static List<roadGenScript.roadClass> StashFor(roadGenScript gen, Vector3d pos)
        {
            var d = Stashes.GetValue(gen, _ => new Dictionary<Vector3d, List<roadGenScript.roadClass>>());
            if (!d.TryGetValue(pos, out var l)) d[pos] = l = new List<roadGenScript.roadClass>();
            return l;
        }

        private static void ResetTo(roadGenScript.dotChunkClass dcc, Entry e)
        {
            dcc.seed = e.Seed;
            if (dcc.rnd == null) dcc.rnd = new System.Random(e.Seed);
            InextRef(dcc.rnd) = e.Inext;
            InextpRef(dcc.rnd) = e.Inextp;
            Array.Copy(e.SeedArray, SeedArrayRef(dcc.rnd), e.SeedArray.Length);
            dcc.dotCount = e.DotCount;
            for (int i = 0; i < e.Dots.Length; i++)
            {
                dcc.dots[i].pos = e.Dots[i];
                dcc.dots[i].connectedDotIDsInSameChunk.Clear();
            }
            // Keep last frame's road objects for RoadConnectMemo's replay (reused only if never generated)
            if (RoadConnectMemo.ReuseRoads && dcc.roads.Count > 0 && _resetGen != null)
            {
                var stash = StashFor(_resetGen, _resetPos);
                stash.Clear();
                stash.AddRange(dcc.roads);
                stash.Reverse(); // Replay takes from the end: keep the original order
            }
            dcc.roads.Clear();
            dcc.insideConnected = false;
            dcc.outsideUConnected = false;
            dcc.outsideRConnected = false;
            dcc.innerChunkGenerated = false;
            dcc.innerChunks.Clear();
            RoadPlaceCache.ChunkReset(dcc);
            dcc.mainRoad.Clear();
        }

        private static void CompareWithFresh(roadGenScript.dotChunkClass d, Entry e, Vector3d pos)
        {
            ReuseChecks++;
            var f = Snapshot(d);
            string diff = null;
            if (f.Seed != e.Seed || f.DotCount != e.DotCount || f.Inext != e.Inext || f.Inextp != e.Inextp) diff = "seed/rnd";
            else if (f.Dots.Length != e.Dots.Length) diff = "dot count";
            for (int i = 0; diff == null && i < e.SeedArray.Length; i++) if (f.SeedArray[i] != e.SeedArray[i]) diff = "seed array";
            for (int i = 0; diff == null && i < e.Dots.Length; i++)
                if (!f.Dots[i].Equals(e.Dots[i]) || d.dots[i].connectedDotIDsInSameChunk.Count != 0) diff = "dot " + i;
            if (diff == null && (d.roads.Count != 0 || d.mainRoad.Count != 0 || d.innerChunks.Count != 0 || d.insideConnected ||
                                 d.outsideUConnected || d.outsideRConnected || d.innerChunkGenerated)) diff = "collections/flags";
            // the Random must continue exactly like a fresh one
            if (diff == null)
            {
                var fresh = new System.Random(e.Seed);
                InextRef(fresh) = e.Inext; InextpRef(fresh) = e.Inextp;
                Array.Copy(e.SeedArray, SeedArrayRef(fresh), e.SeedArray.Length);
                var probeA = (int[])SeedArrayRef(d.rnd).Clone(); int ia = InextRef(d.rnd), ipa = InextpRef(d.rnd);
                for (int i = 0; i < 5 && diff == null; i++) if (fresh.Next() != d.rnd.Next()) diff = "rnd stream";
                // put the reused Random back where it was (the probe consumed numbers)
                Array.Copy(probeA, SeedArrayRef(d.rnd), probeA.Length); InextRef(d.rnd) = ia; InextpRef(d.rnd) = ipa;
            }
            if (diff != null) { ReuseMismatches++; LastReuseMismatch = pos + ": " + diff; }
        }

        private static Entry Snapshot(roadGenScript.dotChunkClass dcc)
        {
            var dots = new Vector3d[dcc.dots.Count];
            for (int i = 0; i < dots.Length; i++) dots[i] = dcc.dots[i].pos;
            return new Entry
            {
                Seed = dcc.seed,
                DotCount = dcc.dotCount,
                Inext = InextRef(dcc.rnd),
                Inextp = InextpRef(dcc.rnd),
                SeedArray = (int[])SeedArrayRef(dcc.rnd).Clone(),
                Dots = dots,
            };
        }

        /// A restarted/reconfigured road generator (new seed, sizes) must not reuse old content.
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.Restart))]
        [HarmonyPostfix]
        private static void ClearOnStart(roadGenScript __instance)
        {
            if (Memo.TryGetValue(__instance, out var memo)) { memo.Clear(); Clears++; }
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"reuse\":" + (Reuse ? "true" : "false") + ",\"reused\":" + Reused +
            ",\"reuseChecks\":" + ReuseChecks + ",\"reuseMismatches\":" + ReuseMismatches + ",\"lastReuseMismatch\":" + Json.Str(LastReuseMismatch) + ",\"hits\":" + Hits + ",\"misses\":" + Misses + ",\"clears\":" + Clears +
            ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches + ",\"lastMismatch\":" + Json.Str(LastMismatch) + "}";
    }
}
