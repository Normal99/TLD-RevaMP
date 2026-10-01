using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// roadGenScript.RoadsNear(pos, notJustChunks, align) ends by calling roadClass.Generate on every road in its result
    /// that isn't generated yet. When a building finds no suitable road nearby (poiGenScript.AlignPosToRoad), that
    /// result is every road in every loaded chunk. Dozens of ~1 ms generations in one frame gave 50–130 ms hitches.
    ///
    /// While RoadsNear runs, Generate calls are recorded instead of executed (first occurrence order, deduplicated).
    /// Just before RoadsNear returns, the recorded roads are computed in parallel on worker threads. Each worker
    /// produces the road's elements (bones, biome, roadID) plus the exact sequence of "add element" / "register
    /// inner chunk" steps the original performs. The main thread then replays those steps road by road in the original
    /// order through the game's own UpdInnerChunk, so every shared structure ends up exactly as the original leaves it.
    /// Nothing else runs meanwhile: the game is inside RoadsNear waiting for the result.
    ///
    /// Thread safety: the compute reads dot positions and roadgen settings (not written while the main thread waits),
    /// uses NoiseS3D (read-only tables) and AsyncTerrain's struct-copy Height/Biome (no shared noise scratch).
    /// Image maps and single-road batches use the original code.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.ParallelRoadGen.Enabled / .Verify / .MinBatch
    [HarmonyPatch]
    public static class ParallelRoadGen
    {
        public static bool Enabled = true;
        /// Recompute each batched road on the main thread with the game's own Height/Biome and compare.
        public static bool Verify = false;
        public static int MinBatch = 2;
        /// Stability guard (works with Enabled on or off): a road whose endpoint/self chunk isn't loaded can't be
        /// generated (the game's Generate throws KeyNotFoundException). Uncaught, that exception escapes
        /// mapSettingScript.Update every frame and stops objects/terrain generating (player fell through the world,
        /// 2026-09-24). Such roads are left ungenerated and dropped from RoadsNear's result.
        public static bool GuardMissingChunks = true;
        public static long GuardSkips;

        public static long Batches, RoadsBatched, RoadsSync, Fallbacks, VerifyChecks, VerifyMismatches;
        public static double MaxBatchMs, MaxComputeMs, MaxReplayMs;
        public static int MaxBatchRoads;
        public static string LastMismatch = "", LastError = "";

        [ThreadStatic] private static List<roadGenScript.roadClass> _collect;
        [ThreadStatic] private static HashSet<roadGenScript.roadClass> _collectSet;
        [ThreadStatic] private static int _depth;
        [ThreadStatic] private static bool _bypass;
        [ThreadStatic] private static HashSet<roadGenScript.roadClass> _skipped;

        // ---------------------------------------------------------------- collection

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.RoadsNear),
            new[] { typeof(Vector3d), typeof(bool), typeof(poiGenScript.alignToRoadEmu) })]
        [HarmonyPrefix]
        private static void RoadsNearPrefix(roadGenScript __instance, out bool __state)
        {
            __state = false;
            if (_skipped == null) _skipped = new HashSet<roadGenScript.roadClass>();
            if (_depth == 0) _skipped.Clear();
            if (!Enabled || _depth > 0 || __instance.terrainSettings == null
                || __instance.terrainSettings.mapSetting == null || __instance.terrainSettings.mapSetting.imageMapLoad != null)
                return;
            _depth++;
            __state = true;
            if (_collect == null) { _collect = new List<roadGenScript.roadClass>(); _collectSet = new HashSet<roadGenScript.roadClass>(); }
            _collect.Clear();
            _collectSet.Clear();
        }

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.RoadsNear),
            new[] { typeof(Vector3d), typeof(bool), typeof(poiGenScript.alignToRoadEmu) })]
        [HarmonyFinalizer]
        private static Exception RoadsNearFinalizer(roadGenScript __instance, bool __state, Exception __exception,
            ref List<roadGenScript.roadClass> __result)
        {
            if (!__state)
            {
                DropSkipped(__result, __exception);
                return __exception;
            }
            _depth--;
            var roads = new List<roadGenScript.roadClass>(_collect);
            _collect.Clear();
            _collectSet.Clear();
            // Whatever happens, every recorded road must end up generated before RoadsNear's caller sees the list
            try { GenerateAll(__instance, roads); }
            catch (Exception e)
            {
                Fallbacks++;
                LastError = e.ToString();
                foreach (var r in roads)
                    if (!r.generated && !_skipped.Contains(r)) SyncGenerate(__instance, r);
            }
            DropSkipped(__result, __exception);
            return __exception;
        }

        private static void DropSkipped(List<roadGenScript.roadClass> result, Exception exception)
        {
            if (exception == null && result != null && _skipped != null && _skipped.Count > 0 && _depth == 0)
                result.RemoveAll(r => _skipped.Contains(r));
        }

        private static bool ChunksLoaded(roadGenScript gen, roadGenScript.roadClass road) =>
            gen != null && gen.chunks.ContainsKey(road.chunk1) && gen.chunks.ContainsKey(road.chunk2) && gen.chunks.ContainsKey(road.selfChunk);

        [HarmonyPatch(typeof(roadGenScript.roadClass), nameof(roadGenScript.roadClass.Generate))]
        [HarmonyPrefix]
        private static bool GeneratePrefix(roadGenScript.roadClass __instance, roadGenScript _roadgen)
        {
            if (GuardMissingChunks && !ChunksLoaded(_roadgen, __instance))
            {
                // The original would throw here and stall world generation; leave the road ungenerated instead
                GuardSkips++;
                if (GuardSkips <= 5) Plugin.Log.LogWarning($"Road generation skipped: endpoint chunk not loaded (chunk1 {__instance.chunk1}, chunk2 {__instance.chunk2}, self {__instance.selfChunk})");
                if (_skipped == null) _skipped = new HashSet<roadGenScript.roadClass>();
                _skipped.Add(__instance);
                return false;
            }
            if (_bypass || _depth == 0 || _collect == null) return true;
            // Inside a batching RoadsNear: record (first occurrence), generate later. RoadsNear's loop does nothing else
            // with the road, and the original skips already-generated roads, so deferring changes nothing observable.
            if (__instance.roadElements.Count != 0) return true; // unexpected state: leave it to the game
            if (_collectSet.Add(__instance)) _collect.Add(__instance);
            return false;
        }

        private static void SyncGenerate(roadGenScript gen, roadGenScript.roadClass road)
        {
            _bypass = true;
            try { road.Generate(gen); RoadsSync++; }
            finally { _bypass = false; }
        }

        // ---------------------------------------------------------------- batch

        /// roadGenScript's private road-shape noise settings, snapshotted on the main thread per batch.
        private sealed class Params
        {
            public int Octaves;
            public double Persistence, Lacunarity, Ampl, Freq, NoiseMin, NoiseMax, HeightMin, HeightMax;

            private static readonly AccessTools.FieldRef<roadGenScript, int> OctavesRef = AccessTools.FieldRefAccess<roadGenScript, int>("octaves");
            private static readonly AccessTools.FieldRef<roadGenScript, double> PersistenceRef = AccessTools.FieldRefAccess<roadGenScript, double>("persistence");
            private static readonly AccessTools.FieldRef<roadGenScript, double> LacunarityRef = AccessTools.FieldRefAccess<roadGenScript, double>("lacunarity");
            private static readonly AccessTools.FieldRef<roadGenScript, double> AmplRef = AccessTools.FieldRefAccess<roadGenScript, double>("ampl");
            private static readonly AccessTools.FieldRef<roadGenScript, double> FreqRef = AccessTools.FieldRefAccess<roadGenScript, double>("freq");
            private static readonly AccessTools.FieldRef<roadGenScript, double> NoiseMinRef = AccessTools.FieldRefAccess<roadGenScript, double>("startEndNoiseMarginMin");
            private static readonly AccessTools.FieldRef<roadGenScript, double> NoiseMaxRef = AccessTools.FieldRefAccess<roadGenScript, double>("startEndNoiseMarginMax");
            private static readonly AccessTools.FieldRef<roadGenScript, double> HeightMinRef = AccessTools.FieldRefAccess<roadGenScript, double>("startEndHeightMarginMin");
            private static readonly AccessTools.FieldRef<roadGenScript, double> HeightMaxRef = AccessTools.FieldRefAccess<roadGenScript, double>("startEndHeightMarginMax");

            public static Params Of(roadGenScript g) => new Params
            {
                Octaves = OctavesRef(g), Persistence = PersistenceRef(g), Lacunarity = LacunarityRef(g), Ampl = AmplRef(g),
                Freq = FreqRef(g), NoiseMin = NoiseMinRef(g), NoiseMax = NoiseMaxRef(g), HeightMin = HeightMinRef(g), HeightMax = HeightMaxRef(g),
            };
        }

        private sealed class Result
        {
            public readonly List<roadGenScript.roadElementClass> Elements = new List<roadGenScript.roadElementClass>();
            // Replay steps in original order: >= 0 → add Elements[i]; < 0 → UpdInnerChunk at InnerPos[-(n+1)]
            public readonly List<int> Steps = new List<int>();
            public readonly List<Vector3d> InnerPos = new List<Vector3d>();
            public Exception Error;
        }

        private static void GenerateAll(roadGenScript gen, List<roadGenScript.roadClass> roads)
        {
            if (roads.Count == 0) return;
            if (roads.Count < MinBatch)
            {
                foreach (var r in roads) SyncGenerate(gen, r);
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var prm = Params.Of(gen);
            var results = new Result[roads.Count];
            using (var done = new CountdownEvent(roads.Count))
            {
                for (int i = 0; i < roads.Count; i++)
                {
                    int idx = i;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        var res = new Result();
                        try { Compute(gen, prm, roads[idx], res, threadSafe: true); }
                        catch (Exception e) { res.Error = e; }
                        results[idx] = res;
                        done.Signal();
                    });
                }
                done.Wait();
            }
            long tCompute = System.Diagnostics.Stopwatch.GetTimestamp();

            // Replay on the main thread, road by road, in the original order
            for (int i = 0; i < roads.Count; i++)
            {
                var road = roads[i];
                var res = results[i];
                if (res.Error != null || road.generated || road.roadElements.Count != 0)
                {
                    if (res.Error != null) { Fallbacks++; LastError = res.Error.GetType().Name + ": " + res.Error.Message; }
                    if (!road.generated) SyncGenerate(gen, road);
                    continue;
                }
                if (Verify) VerifyRoad(gen, road, res);
                Apply(gen, road, res);
                RoadsBatched++;
            }
            Batches++;
            long tEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double ms = (tEnd - t0) * f;
            if (ms > MaxBatchMs) { MaxBatchMs = ms; MaxComputeMs = (tCompute - t0) * f; MaxReplayMs = (tEnd - tCompute) * f; MaxBatchRoads = roads.Count; }
        }

        private static void Apply(roadGenScript gen, roadGenScript.roadClass road, Result res)
        {
            var chunk = gen.chunks[road.selfChunk];
            foreach (int step in res.Steps)
            {
                if (step >= 0) road.roadElements.Add(res.Elements[step]);
                else gen.UpdInnerChunk(res.InnerPos[-(step + 1)], chunk, road, road.selfID);
            }
            road.generated = true;
        }

        private static void VerifyRoad(roadGenScript gen, roadGenScript.roadClass road, Result res)
        {
            VerifyChecks++;
            var main = new Result();
            Compute(gen, null, road, main, threadSafe: false);
            string diff = null;
            if (main.Elements.Count != res.Elements.Count) diff = "element count";
            for (int e = 0; diff == null && e < main.Elements.Count; e++)
            {
                var a = main.Elements[e]; var b = res.Elements[e];
                if (a.biome != b.biome || a.roadID != b.roadID) diff = $"element {e} biome/roadID";
                else if (a.bonePos.Count != b.bonePos.Count) diff = $"element {e} bone count";
                else for (int k = 0; k < a.bonePos.Count; k++)
                    if (!a.bonePos[k].Equals(b.bonePos[k])) { diff = $"element {e} bone {k}: game {a.bonePos[k]} ours {b.bonePos[k]}"; break; }
            }
            if (diff == null && (main.Steps.Count != res.Steps.Count || main.InnerPos.Count != res.InnerPos.Count)) diff = "step sequence";
            for (int i = 0; diff == null && i < main.InnerPos.Count; i++)
                if (!main.InnerPos[i].Equals(res.InnerPos[i])) diff = "inner chunk position " + i;
            if (diff != null)
            {
                VerifyMismatches++;
                LastMismatch = diff;
                if (VerifyMismatches <= 5) Plugin.Log.LogWarning("ParallelRoadGen verify mismatch: " + diff);
            }
        }

        // ---------------------------------------------------------------- compute (mirror of roadClass.Generate)

        /// Mirrors roadClass.Generate, writing into `res` instead of the road/roadgen. threadSafe=false uses the game's own
        /// Height/Biome (main thread only, for verify). Operation order kept identical to the original.
        private static void Compute(roadGenScript _roadgen, Params prm, roadGenScript.roadClass road, Result res, bool threadSafe)
        {
            var ts = _roadgen.terrainSettings;
            Vector3d pos = _roadgen.chunks[road.chunk1].dots[road.id1].pos;
            Vector3d pos2 = _roadgen.chunks[road.chunk2].dots[road.id2].pos;
            Vector3d vector3d = pos2 - pos;
            vector3d.y = 0.0;
            Vector3d normalized = vector3d.normalized;
            double magnitude = vector3d.magnitude;
            Vector3d vector3d2 = pos;
            double num = 10.0;
            Vector3d vector3d3 = normalized * num;
            int i = 0;
            var cur = AddElement(res);
            AddInner(res, vector3d2);
            cur.bonePos.Add(vector3d2);
            if (ts != null) SetBiome(ts, road, cur, vector3d2, threadSafe);
            for (; i < 1000000; i++)
            {
                bool flag = false;
                Vector3d vector3d4 = vector3d2;
                vector3d2 = vector3d4 + vector3d3;
                vector3d2 = PointOnRoad(_roadgen, prm, road, vector3d2, threadSafe);
                vector3d2 = vector3d4 + (vector3d2 - vector3d4).normalized * num;
                Vector3d vector3d5 = vector3d2 - pos;
                vector3d5.y = 0.0;
                double magnitude2 = vector3d5.magnitude;
                if (magnitude2 >= magnitude - num * 2.0)
                {
                    vector3d2 = pos2;
                    cur.bonePos.Add(vector3d2);
                    break;
                }
                if (cur.bonePos.Count >= 10)
                {
                    flag = true;
                    cur.bonePos.Add(vector3d2);
                    cur = AddElement(res);
                    AddInner(res, vector3d2);
                    if (ts != null) SetBiome(ts, road, cur, vector3d2, threadSafe);
                }
                cur.bonePos.Add(vector3d2);
                if (flag)
                {
                    vector3d2 += vector3d2 - vector3d4;
                    cur.bonePos.Add(vector3d2);
                }
                if (magnitude2 >= magnitude)
                {
                    break;
                }
            }
            if (_roadgen.temporaryHeightSmoothing)
            {
                double num2 = pos.y;
                double num3 = pos2.y;
                if (num3 < num2)
                {
                    double num4 = num2;
                    num2 = num3;
                    num3 = num4;
                }
                if (num2 != num3)
                {
                    double num5 = (pos.y + pos2.y) * 0.5;
                    double num6 = num3 - num2;
                    for (int j = 0; j < res.Elements.Count; j++)
                    {
                        var bones = res.Elements[j].bonePos;
                        for (int k = 0; k < bones.Count; k++)
                        {
                            Vector3d value = bones[k];
                            value.y = Mathd.Lerp(num5 - num6 * 0.5, num5 + num6 * 0.5, Mathd.InverseLerp(num2, num3, value.y));
                            bones[k] = value;
                        }
                    }
                }
            }
        }

        private static roadGenScript.roadElementClass AddElement(Result res)
        {
            var el = new roadGenScript.roadElementClass();
            res.Steps.Add(res.Elements.Count);
            res.Elements.Add(el);
            return el;
        }

        private static void AddInner(Result res, Vector3d p)
        {
            res.InnerPos.Add(p);
            res.Steps.Add(-res.InnerPos.Count); // -(index+1)
        }

        private static void SetBiome(terrainGenSettings ts, roadGenScript.roadClass road, roadGenScript.roadElementClass el, Vector3d p, bool threadSafe)
        {
            int biomeInt, biomeNoSnow;
            if (threadSafe)
            {
                // GetBiomeInt(p) = GetBiomeInt(Biome(p, Height(p))), same for NoSnow; both evaluate Biome independently
                biomeInt = ts.GetBiomeInt(AsyncTerrain.Biome(ts, p, AsyncTerrain.Height(ts, p, null, null)));
                el.biome = biomeInt;
                biomeNoSnow = ts.GetBiomeIntNoSnow(AsyncTerrain.Biome(ts, p, AsyncTerrain.Height(ts, p, null, null)));
            }
            else
            {
                biomeInt = ts.GetBiomeInt(p);
                el.biome = biomeInt;
                biomeNoSnow = ts.GetBiomeIntNoSnow(p);
            }
            el.roadID = itemdatabase.s.RoadID(biomeNoSnow, biomeInt == 1, road.roadType);
        }

        /// Mirror of roadGenScript.PointOnRoad(road, pos, _useHeight: true).
        private static Vector3d PointOnRoad(roadGenScript g, Params p, roadGenScript.roadClass _road, Vector3d _pos, bool threadSafe)
        {
            if (!threadSafe) return g.PointOnRoad(_road, _pos, _useHeight: true);
            Vector3d pos = g.chunks[_road.chunk1].dots[_road.id1].pos;
            Vector3d pos2 = g.chunks[_road.chunk2].dots[_road.id2].pos;
            Vector3d rhs = pos2 - pos;
            rhs.y = 0.0;
            Vector3d normalized = rhs.normalized;
            Vector3d vector4 = _pos - pos;
            vector4.y = 0.0;
            Vector3d vector3d = Vector3d.Project(vector4, normalized);
            Vector3d vector3d2;
            if (Vector3d.Dot(vector3d, rhs) < 0.0)
                vector3d2 = pos;
            else
                vector3d2 = ((!(vector3d.magnitude > rhs.magnitude)) ? (pos + vector3d) : pos2);
            double num = 0.0;
            double num2 = 1.0;
            double num3 = 1.0;
            for (int i = 0; i <= p.Octaves; i++)
            {
                num += FastNoise.Noise2(vector3d2.x / p.Freq * num3, vector3d2.z / p.Freq * num3) * p.Ampl * num2;
                num2 *= p.Persistence;
                num3 *= p.Lacunarity;
            }
            double num4;
            if (Vector3d.Dot(vector3d, rhs) < 0.0)
                num4 = 0.0;
            else
                num4 = ((!(vector3d.magnitude > rhs.magnitude)) ? Mathd.InverseLerp(0.0, rhs.magnitude, vector3d.magnitude) : 1.0);
            vector3d2 += Vector3d.Cross(normalized, Vector3d.up) * num * mainscript.Sine01FromLinear01(Mathd.InverseLerp(p.NoiseMin, p.NoiseMax, 0.5 - Mathd.Abs(num4 - 0.5)));
            vector3d2.y = _pos.y;
            if (g.terrainSettings != null)
            {
                vector3d2.y = AsyncTerrain.Height(g.terrainSettings, vector3d2, null, null);
                double t = mainscript.Sine01FromLinear01(Mathd.InverseLerp(p.HeightMin, p.HeightMax, 0.5 - Mathd.Abs(num4 - 0.5)));
                if (num4 < 0.5)
                    vector3d2.y = Mathd.Lerp(pos.y, vector3d2.y, t);
                else
                    vector3d2.y = Mathd.Lerp(pos2.y, vector3d2.y, t);
                vector3d2.y += g.offsetty;
            }
            return vector3d2;
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"batches\":" + Batches + ",\"roadsBatched\":" + RoadsBatched +
            ",\"roadsSync\":" + RoadsSync + ",\"fallbacks\":" + Fallbacks + ",\"guardSkips\":" + GuardSkips + ",\"maxBatchMs\":" + MaxBatchMs.ToString("F2") + ",\"maxBatchComputeMs\":" + MaxComputeMs.ToString("F2") +
            ",\"maxBatchReplayMs\":" + MaxReplayMs.ToString("F2") + ",\"maxBatchRoads\":" + MaxBatchRoads +
            ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches +
            ",\"lastMismatch\":" + Json.Str(LastMismatch) + ",\"lastError\":" + Json.Str(LastError) + "}";
    }
}
