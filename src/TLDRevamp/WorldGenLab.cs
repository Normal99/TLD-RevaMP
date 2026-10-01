using System.Linq;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp
{
    /// Structure counters for world generation (sizes and call counts, not timings), to check what the code implies.
    /// Bridge: `worldgen` (snapshot + counters since `worldgen reset`), `worldgen reset`, `randbench <n>`.
    [HarmonyPatch]
    public static class WorldGenLab
    {
        public static bool Count = true;
        public static long ConnectInside, ConnectCross, DotChunkCalls, DotChunkCreated, NeighborDotsCalls, ObjGenChunks, ObjGenCells;
        private static int _frame0;

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.ConnectDots))]
        [HarmonyPrefix]
        private static void OnConnectDots(Vector3d _c1, Vector3d _c2)
        {
            if (!Count) return;
            if (Fixes.RoadConnectMemo.Eq(_c1, _c2)) ConnectInside++; else ConnectCross++;
        }

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdDotChunk))]
        [HarmonyPrefix]
        private static void OnDotChunk(roadGenScript __instance, Vector3d _cPos)
        {
            if (!Count) return;
            DotChunkCalls++;
            if (!__instance.chunks.ContainsKey(_cPos)) DotChunkCreated++;
        }

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.NeighborDots))]
        [HarmonyPrefix]
        private static void OnNeighborDots() { if (Count) NeighborDotsCalls++; }

        // Measurement only: skip one generator's per-frame update (state kept) to attribute its allocations.
        public static bool SkipRoads, SkipObjs, SkipTerrain, SkipPois;

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.Upd), new[] { typeof(bool) })]
        [HarmonyPrefix] private static bool SkipRoadUpd() => !SkipRoads;
        [HarmonyPatch(typeof(objGenScript), nameof(objGenScript.Upd))]
        [HarmonyPrefix] private static bool SkipObjUpd() => !SkipObjs;
        [HarmonyPatch(typeof(terrainGenScript), nameof(terrainGenScript.Upd))]
        [HarmonyPrefix] private static bool SkipTerrainUpd() => !SkipTerrain;
        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.Upd))]
        [HarmonyPrefix] private static bool SkipPoiUpd() => !SkipPois;

        // --- building alignment (poiGenScript.AlignPosToRoad → roadGen.RoadsNear(notJustChunks: true))
        private sealed class AlignRec { public double Ms; public int Roads, Ring, Ungenerated, Needed; public string Align; }
        private static readonly List<AlignRec> Aligns = new List<AlignRec>();
        private static AlignRec _cur;
        private static long _alignT0;

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AlignPosToRoad))]
        [HarmonyPrefix] private static void AlignPre(poiGenScript.alignToRoadEmu align) { if (Count) { _cur = new AlignRec { Align = align.ToString() }; _alignT0 = System.Diagnostics.Stopwatch.GetTimestamp(); } }

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AlignPosToRoad))]
        [HarmonyPostfix] private static void AlignPost()
        {
            if (!Count || _cur == null) return;
            _cur.Ms = (System.Diagnostics.Stopwatch.GetTimestamp() - _alignT0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Aligns.Add(_cur); if (Aligns.Count > 400) Aligns.RemoveAt(0);
            _cur = null;
        }

        /// Runs before ParallelRoadGen's finalizer generates the list (prefix order: this is a prefix + postfix pair
        /// on the original; the list the original built is inspected in the postfix).
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.RoadsNear), new[] { typeof(Vector3d), typeof(bool), typeof(poiGenScript.alignToRoadEmu) })]
        [HarmonyPostfix] private static void RoadsNearPost(roadGenScript __instance, bool _notJustChunks, List<roadGenScript.roadClass> __result)
        {
            if (!Count || _cur == null || !_notJustChunks || __result == null) return;
            _cur.Roads += __result.Count;
            foreach (var r in __result)
            {
                bool needed = __instance.chunksNeeded.Contains(r.selfChunk);
                if (needed) _cur.Needed++; else _cur.Ring++;
                if (!r.generated) _cur.Ungenerated++;
            }
        }

        public static string AlignStats()
        {
            var sb = new StringBuilder("{\"calls\":").Append(Aligns.Count).Append(",\"slowest\":[");
            bool first = true;
            foreach (var a in Aligns.OrderByDescending(x => x.Ms).Take(12))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"ms\":").Append(a.Ms.ToString("F1")).Append(",\"align\":").Append(Json.Str(a.Align)).Append(",\"roads\":").Append(a.Roads)
                  .Append(",\"inNeededChunks\":").Append(a.Needed).Append(",\"inRingChunks\":").Append(a.Ring).Append(",\"ungenerated\":").Append(a.Ungenerated).Append('}');
            }
            double tot = Aligns.Sum(x => x.Ms);
            return sb.Append("],\"totalMs\":").Append(tot.ToString("F0")).Append(",\"fallbackCalls(>20 roads)\":").Append(Aligns.Count(x => x.Roads > 20)).Append('}').ToString();
        }

        public static string Reset()
        {
            ConnectInside = ConnectCross = DotChunkCalls = DotChunkCreated = NeighborDotsCalls = 0;
            _frame0 = Time.frameCount;
            return "{\"reset\":true}";
        }

        public static string Snapshot()
        {
            int frames = Mathf.Max(1, Time.frameCount - _frame0);
            var sb = new StringBuilder("{\"frames\":").Append(frames);
            sb.Append(",\"perFrame\":{\"connectInside\":").Append(F(ConnectInside, frames)).Append(",\"connectCross\":").Append(F(ConnectCross, frames))
              .Append(",\"dotChunkCalls\":").Append(F(DotChunkCalls, frames)).Append(",\"dotChunkCreated\":").Append(F(DotChunkCreated, frames))
              .Append(",\"neighborDots\":").Append(F(NeighborDotsCalls, frames)).Append('}');
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            sb.Append(",\"roadGens\":[");
            if (map != null && map.roadGens != null)
            {
                bool first = true;
                foreach (var rg in map.roadGens)
                {
                    if (rg == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    int dots = 0, roads = 0, gen = 0, needed = 0;
                    foreach (var kv in rg.chunks)
                    {
                        dots += kv.Value.dots.Count;
                        roads += kv.Value.roads.Count;
                        foreach (var r in kv.Value.roads) if (r.generated) gen++;
                        if (rg.chunksNeeded.Contains(kv.Key)) needed++;
                    }
                    sb.Append("{\"name\":").Append(Json.Str(rg.name)).Append(",\"active\":").Append(rg.isActiveAndEnabled ? "true" : "false")
                      .Append(",\"main\":").Append(rg.hasMainRoad ? "true" : "false").Append(",\"side\":").Append(rg.hasSideRoad ? "true" : "false")
                      .Append(",\"chunkSize\":").Append(rg.chunkSize).Append(",\"chunkDist\":").Append(rg.chunkDist)
                      .Append(",\"roadPlaceDist\":").Append(rg.roadPlaceDist)
                      .Append(",\"chunksNeeded\":").Append(rg.chunksNeeded.Count).Append(",\"chunks\":").Append(rg.chunks.Count)
                      .Append(",\"chunksInNeeded\":").Append(needed)
                      .Append(",\"dotsPerChunk\":").Append(rg.chunks.Count > 0 ? (dots / (float)rg.chunks.Count).ToString("F1") : "0")
                      .Append(",\"minDots\":").Append(rg.minDotCount).Append(",\"maxDots\":").Append(rg.maxDotCount)
                      .Append(",\"roads\":").Append(roads).Append(",\"roadsGenerated\":").Append(gen).Append('}');
                }
            }
            sb.Append("],\"objGens\":[");
            if (map != null && map.objGens != null)
            {
                bool first = true;
                foreach (var og in map.objGens)
                {
                    if (og == null) continue;
                    foreach (var cat in og.categories)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        int objs = 0, placed = 0, done = 0;
                        foreach (var kv in cat.generatedChunks)
                        {
                            objs += kv.Value.objs.Count;
                            if (kv.Value.generated) done++;
                            foreach (var o in kv.Value.objs) if (o.Value.placed) placed++;
                        }
                        // Cells per biome chunk: how many cells share one chunkRnd seed
                        float cellsPerBiomeChunk = cat.chunkSize > 0 && cat.gridSize > 0 ? (cat.chunkSize / cat.gridSize) * (cat.chunkSize / cat.gridSize) : 0;
                        sb.Append("{\"gen\":").Append(Json.Str(og.name)).Append(",\"cat\":").Append(Json.Str(cat.name))
                          .Append(",\"off\":").Append(og.disabled || cat.disable ? "true" : "false")
                          .Append(",\"drawDist\":").Append(cat.currentDrawDist).Append(",\"chunkSize\":").Append(cat.currentChunkSize)
                          .Append(",\"resolution\":").Append(cat.resolution).Append(",\"cellsPerChunk\":").Append(cat.resolution * cat.resolution)
                          .Append(",\"gridSize\":").Append(cat.gridSize).Append(",\"biomeChunkSize\":").Append(cat.chunkSize)
                          .Append(",\"cellsPerBiomeChunk\":").Append(cellsPerBiomeChunk.ToString("F0"))
                          .Append(",\"chunksNeeded\":").Append(cat.chunksNeeded.Count).Append(",\"generatedChunks\":").Append(cat.generatedChunks.Count)
                          .Append(",\"doneChunks\":").Append(done).Append(",\"objs\":").Append(objs).Append(",\"placed\":").Append(placed)
                          .Append(",\"placePerFrame\":").Append(og.placePerFrame).Append(",\"removePerFrame\":").Append(og.removePerFrame)
                          .Append(",\"useTile\":").Append(cat.useTile ? "true" : "false").Append(",\"terrainHeight\":").Append(cat.useTerrainHeight ? "true" : "false")
                          .Append(",\"minDistRoad\":").Append(cat.minDistFromRoad).Append('}');
                    }
                }
            }
            return sb.Append("]}").ToString();
        }

        /// Cost of what the object generator does per grid cell: construct a System.Random and draw from it.
        public static string RandBench(int n)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double sink = 0;
            for (int i = 0; i < n; i++) sink += new System.Random(i * 7919).NextDouble();
            double ctorUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            var rnd = new System.Random(1);
            sw.Restart();
            for (int i = 0; i < n; i++) sink += rnd.NextDouble();
            double drawUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            return "{\"n\":" + n + ",\"ctorPlusDrawUs\":" + ctorUs.ToString("F3") + ",\"drawUs\":" + drawUs.ToString("F4") + ",\"sink\":" + (sink > 0 ? 1 : 0) + "}";
        }

        // Allocation meter: managed heap growth between frames (all threads; a GC shows as a drop, skipped).
        private static long _lastHeap, _allocBytes, _allocFrames, _gcDrops, _gc0;
        private static float _allocStart;
        public static void AllocTick()
        {
            long heap = System.GC.GetTotalMemory(false);
            if (_lastHeap != 0)
            {
                long d = heap - _lastHeap;
                if (d > 0) _allocBytes += d; else if (d < 0) _gcDrops++;
                _allocFrames++;
            }
            _lastHeap = heap;
        }

        public static string AllocStats(bool reset)
        {
            float secs = Mathf.Max(0.001f, Time.realtimeSinceStartup - _allocStart);
            int gcs = System.GC.CollectionCount(0) - (int)_gc0;
            string r = "{\"seconds\":" + secs.ToString("F1") + ",\"frames\":" + _allocFrames + ",\"allocKBperSec\":" + (_allocBytes / 1024.0 / secs).ToString("F0") +
                       ",\"allocKBperFrame\":" + (_allocBytes / 1024.0 / Mathf.Max(1, _allocFrames)).ToString("F1") + ",\"heapDrops\":" + _gcDrops +
                       ",\"collections\":" + gcs + ",\"allocMBperCollection\":" + (gcs > 0 ? (_allocBytes / 1048576.0 / gcs).ToString("F1") : "0") +
                       ",\"heapMB\":" + (System.GC.GetTotalMemory(false) / 1048576.0).ToString("F0") + "}";
            if (reset) { _allocBytes = 0; _allocFrames = 0; _gcDrops = 0; _gc0 = System.GC.CollectionCount(0); _allocStart = Time.realtimeSinceStartup; }
            return r;
        }

        /// Does this Mono support per-thread allocation counting? Allocates 1 MB on the calling thread.
        public static string AllocProbe()
        {
            try
            {
                long a0 = System.GC.GetAllocatedBytesForCurrentThread();
                var arr = new byte[1 << 20];
                long a1 = System.GC.GetAllocatedBytesForCurrentThread();
                return "{\"perThread\":" + (a1 - a0) + ",\"len\":" + arr.Length + "}";
            }
            catch (System.Exception e) { return "{\"error\":" + Json.Str(e.GetType().Name + ": " + e.Message) + "}"; }
        }

        private static string F(long v, int frames) => (v / (double)frames).ToString("F2");
    }
}
