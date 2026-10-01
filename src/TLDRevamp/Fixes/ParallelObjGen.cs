using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// objGenScript.categoryClass.UpdGen generates one whole chunk of scattered objects (cacti, rocks…) per call: a loop
    /// over resolution² grid cells doing two System.Random constructions, a chance test, road/POI distance checks and a
    /// terrain Height sample per kept object. Profiled: 222 frames >5 ms per seeded pass, worst ~37 ms.
    ///
    /// Same result, same frame, spread over cores:
    ///   1. main:    per distinct biome chunk in cell order, fill the category's biome cache (game's GetFirstBiomeInt)
    ///   2. workers: per cell, the random chance test and jitter (pure; per-cell seeded Randoms)
    ///   3. main:    for surviving cells in order, tile cache + RoadsNear/POIsNear (may generate roads; main thread only)
    ///   4. workers: distance checks, Height (AsyncTerrain struct-copy version), prefab/rotation/scale from the cell's Random
    ///   5. main:    insert the objects into the chunk in cell order, mark generated
    /// Everything that reads or writes shared game state happens on the main thread, in the original order. Workers only
    /// read, while the main thread waits. Verify mode computes our result first, then lets the game generate the chunk,
    /// and compares every object.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.ParallelObjGen.Enabled / .Verify / .Workers
    [HarmonyPatch]
    public static class ParallelObjGen
    {
        /// Off by default: verified exact (999 chunks, 0 mismatches) but no frame-time gain in the seeded A/B, objGen spikes
        /// only 221→191 frames >5 ms, and world-gen frames >5 ms went UP 617→975 (2026-09-25). Kept for experiments.
        public static bool Enabled = false;
        public static bool Verify = false;
        public static int Workers = 8;

        public static long Chunks, Cells, Objects, VerifyChecks, VerifyMismatches, Fallbacks;
        public static double MaxChunkMs;
        public static string LastMismatch = "", LastError = "";

        internal sealed class Cell
        {
            public int Index;
            public Vector3d PosID, ChunkID, GPos, GPosID;
            public float R, CurrentChance;
            public System.Random ObjRnd;
            public List<roadGenScript.roadClass> Roads;
            public List<poiGenScript.poiClass> Pois;
            public objGenScript.objClass Obj; // null = rejected
        }

        [HarmonyPatch(typeof(objGenScript.categoryClass), nameof(objGenScript.categoryClass.UpdGen))]
        [HarmonyPrefix]
        private static bool Prefix(objGenScript.categoryClass __instance, bool _moreFrame)
        {
            if (!Enabled) return true;
            var cat = __instance;
            var script = cat.script;
            if (script == null || script.terrain == null || script.terrain.mapSetting == null || script.terrain.mapSetting.imageMapLoad != null)
                return true;
            try
            {
                cat.UpdChunksNeeded();
                for (int i = 0; i < cat.chunksNeeded.Count; i++)
                {
                    var key = cat.chunksNeeded[i];
                    if (!cat.generatedChunks.ContainsKey(key)) cat.generatedChunks.Add(key, new objGenScript.chunkClass());
                    var chunk = cat.generatedChunks[key];
                    if (chunk.generated) continue;

                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var objs = GenerateChunk(cat, key);
                    if (Verify)
                    {
                        // Let the game generate this chunk (it sees the caches we filled, as it would have filled them), then compare
                        VerifyAgainstGame(cat, key, chunk, objs, _moreFrame);
                        return false;
                    }
                    foreach (var c in objs) chunk.objs.Add(c.GPosID, c.Obj);
                    chunk.generated = true;
                    Chunks++;
                    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (ms > MaxChunkMs) MaxChunkMs = ms;
                    if (_moreFrame) break;
                }
                RemoveUnneeded(cat);
                return false;
            }
            catch (Exception e)
            {
                // Before anything was inserted into the chunk the game can simply redo it; after, disable to stay safe
                Fallbacks++;
                LastError = e.ToString();
                Enabled = false;
                Plugin.Log.LogError("ParallelObjGen disabled after error: " + e);
                return true;
            }
        }

        /// The original's tail: chunks no longer needed stop placing; empty ones are dropped.
        internal static void RemoveUnneeded(objGenScript.categoryClass cat)
        {
            cat.chunksToRemove.Clear();
            foreach (var generatedChunk in cat.generatedChunks)
            {
                if (cat.chunksNeeded.Contains(generatedChunk.Key)) continue;
                int count = 0;
                foreach (var obj in generatedChunk.Value.objs)
                {
                    obj.Value.place = false;
                    if (obj.Value.obj != null) count++;
                }
                if (count == 0) cat.chunksToRemove.Add(generatedChunk.Key);
            }
            for (int n = 0; n < cat.chunksToRemove.Count; n++) cat.generatedChunks.Remove(cat.chunksToRemove[n]);
        }

        // ------------------------------------------------------------------ generation

        internal static List<Cell> GenerateChunk(objGenScript.categoryClass cat, Vector3d key)
        {
            var script = cat.script;
            int res = cat.resolution;
            int n = res * res;
            Cells += n;
            int seedBase = script.mapSetting.seed + cat.rndCategoryOffset;

            // 1. main: cell positions and biome cache in cell order (GetFirstBiomeInt isn't thread-safe; the cache insert order matches)
            var posIDs = new Vector3d[n];
            var chunkIDs = new Vector3d[n];
            var mult = new float[n];
            for (int j = 0; j < res; j++)
                for (int k = 0; k < res; k++)
                {
                    int idx = j * res + k;
                    Vector3d posID = key + new Vector3d(1f, 0f, 1f) * cat.offset + new Vector3d(k, 0f, j) * cat.gridSize;
                    Vector3d chunkID = mainscript.GetChunkPos(posID, cat.chunkSize);
                    float chanceMultiplierBiome = 1f;
                    if (cat.biomes.TryGetValue(chunkID, out int biomeInt))
                        chanceMultiplierBiome *= cat.biomeMultipliers[biomeInt];
                    else
                    {
                        int biome = terrainGenSettings.GetFirstBiomeInt(chunkID);
                        cat.biomes.Add(chunkID, biome);
                        chanceMultiplierBiome *= cat.biomeMultipliers[biome];
                    }
                    posIDs[idx] = posID; chunkIDs[idx] = chunkID; mult[idx] = chanceMultiplierBiome;
                }

            // 2. workers: chance test + jitter
            var stage1 = new Cell[n];
            Parallel(n, idx =>
            {
                Vector3d posID = posIDs[idx], chunkID = chunkIDs[idx];
                var chunkRnd = new System.Random(seedBase + chunkID.GetHashCode());
                int selectedChance = SelectRandomProperty(cat.chance, chunkRnd);
                float currentChance = Mathf.Lerp(cat.chance[selectedChance].min, cat.chance[selectedChance].max, (float)chunkRnd.NextDouble()) * cat.chanceMultiplier * mult[idx];
                var objRnd = new System.Random(seedBase + posID.GetHashCode() + Mathd.Round((posID - chunkID).sqrMagnitude).GetHashCode());
                float r = (float)objRnd.NextDouble();
                if (r > currentChance * script.globalChanceMultiplier) return;
                float tollasx = (float)objRnd.NextDouble();
                float tollasz = (float)objRnd.NextDouble();
                Vector3d gpos = posID;
                gpos.x += Mathf.Lerp(0f - cat.tollas, cat.tollas, tollasx) * cat.gridSize;
                gpos.z += Mathf.Lerp(0f - cat.tollas, cat.tollas, tollasz) * cat.gridSize;
                stage1[idx] = new Cell { Index = idx, PosID = posID, ChunkID = chunkID, GPos = gpos, GPosID = gpos, R = r, CurrentChance = currentChance, ObjRnd = objRnd };
            });

            // 3. main: destroyed check (dict read) + tile cache / RoadsNear / POIsNear in cell order
            bool needLists = cat.useTerrainHeight || cat.useRemoveInsidePOI || cat.minDistFromRoad > 0f || cat.maxDistFromRoad > 0f || cat.minDistFromPOI > 0f;
            var cells = new List<Cell>();
            for (int idx = 0; idx < n; idx++)
            {
                var c = stage1[idx];
                if (c == null) continue;
                if (cat.saveDestroy && cat.destroyed.ContainsKey(c.ChunkID) && cat.destroyed[c.ChunkID].Contains(c.GPosID)) continue;
                if (needLists)
                {
                    SceneryTileSync.Lists(script, cat.useTile, c.GPos, out var roads, out var pois);
                    c.Roads = roads;
                    c.Pois = pois;
                }
                cells.Add(c);
            }

            // 4. workers: distance filters, height, object properties
            Parallel(cells.Count, i => cells[i].Obj = Finish(cat, cells[i]));

            // 5. keep order; drop rejected
            var kept = new List<Cell>(cells.Count);
            foreach (var c in cells) if (c.Obj != null) kept.Add(c);
            Objects += kept.Count;
            return kept;
        }

        internal static objGenScript.objClass Finish(objGenScript.categoryClass cat, Cell c)
        {
            var script = cat.script;
            Vector3d gpos = c.GPos;
            var roadsNear = c.Roads;
            var poisNear = c.Pois;
            if (cat.minDistFromRoad > 0f)
            {
                for (int l = 0; l < roadsNear.Count; l++)
                {
                    Vector3d roadpoint = roadGenScript.NearestPointOnRoadBone(gpos, roadsNear[l]);
                    roadpoint.y = gpos.y;
                    if ((roadpoint - gpos).magnitude < (double)cat.minDistFromRoad && (cat.nearRoadMultiplier == 0f || c.R > c.CurrentChance * cat.nearRoadMultiplier))
                        return null;
                }
            }
            if (cat.maxDistFromRoad > 0f)
            {
                bool roadTooFar = true;
                for (int m = 0; m < roadsNear.Count; m++)
                {
                    Vector3d roadpoint = roadGenScript.NearestPointOnRoadBone(gpos, roadsNear[m]);
                    roadpoint.y = gpos.y;
                    if ((roadpoint - gpos).magnitude < (double)cat.maxDistFromRoad) { roadTooFar = false; break; }
                }
                if (roadTooFar) return null;
            }
            if (cat.minDistFromPOI > 0f)
            {
                for (int p = 0; p < poisNear.Count; p++)
                {
                    Vector3d poipoint = poisNear[p].pos;
                    poipoint.y = gpos.y;
                    if ((poipoint - gpos).magnitude < (double)cat.minDistFromPOI) return null;
                }
            }
            if (cat.useRemoveInsidePOI)
            {
                for (int p = 0; p < poisNear.Count; p++)
                {
                    for (int q = 0; q < poisNear[p].terrainHoleQuads.Count; q++)
                        if (mainscript.IsPointInRectangle(gpos, poisNear[p].terrainHoleQuads[q].corners)) return null;
                    for (int q = 0; q < poisNear[p].noObjQuads.Count; q++)
                        if (mainscript.IsPointInRectangle(gpos, poisNear[p].noObjQuads[q].corners)) return null;
                }
            }
            if (cat.useTerrainHeight)
                gpos.y = AsyncTerrain.Height(script.terrain, gpos, roadsNear, poisNear);
            gpos.y += cat.offsetty;

            var objRnd = c.ObjRnd;
            var currentObj = new objGenScript.objClass();
            currentObj.seed = objRnd.Next(int.MinValue, int.MaxValue);
            float lmaxChance = 0f;
            for (int i = 0; i < cat.prefabs.Count; i++)
                lmaxChance = cat.prefabs[i].chance == 0f ? lmaxChance + 1 : lmaxChance + cat.prefabs[i].chance;
            float selected = (float)objRnd.NextDouble() * lmaxChance;
            for (int i = 0; i < cat.prefabs.Count; i++)
            {
                selected = cat.prefabs[i].chance == 0f ? selected - 1 : selected - cat.prefabs[i].chance;
                if (selected <= 0f) { currentObj.prefabIndex = i; break; }
            }
            if (cat.useRandomRotY)
                currentObj.rot.y = (float)objRnd.NextDouble() * 360f;
            if (cat.useRandomRotXZ)
            {
                int sel = SelectRandomProperty(cat.randomRotXZ, objRnd);
                float min = cat.randomRotXZ[sel].min, max = cat.randomRotXZ[sel].max;
                if (objRnd.Next(0, 2) == 0) { min = 0f - min; max = 0f - max; }
                currentObj.rot.x = Mathf.Lerp(min, max, (float)objRnd.NextDouble());
                currentObj.rot.z = Mathf.Lerp(min, max, (float)objRnd.NextDouble());
            }
            var prefab = cat.prefabs[currentObj.prefabIndex];
            if (prefab.useOverrideRandomScale)
            {
                int sel = SelectRandomProperty(prefab.randomScale, objRnd);
                currentObj.scale = Mathf.Lerp(prefab.randomScale[sel].min, prefab.randomScale[sel].max, (float)objRnd.NextDouble());
            }
            else if (cat.useRandomScale)
            {
                int sel = SelectRandomProperty(cat.randomScale, objRnd);
                currentObj.scale = Mathf.Lerp(cat.randomScale[sel].min, cat.randomScale[sel].max, (float)objRnd.NextDouble());
            }
            else currentObj.scale = 1f;
            currentObj.chunkID = c.ChunkID;
            currentObj.posID = c.PosID;
            currentObj.gposID = c.GPosID;
            currentObj.gpos = gpos;
            currentObj.place = true;
            return currentObj;
        }

        /// Thread-safe copy of objGenScript.SelectRandomProperty (the game's version uses static scratch fields).
        internal static int SelectRandomProperty(List<objGenScript.randomPropertyClass> _list, System.Random _rnd)
        {
            float lmaxChance = 0f;
            for (int i = 0; i < _list.Count; i++)
                lmaxChance = _list[i].chance == 0f ? lmaxChance + 1 : lmaxChance + _list[i].chance;
            float selected = (float)_rnd.NextDouble() * lmaxChance;
            for (int j = 0; j < _list.Count; j++)
            {
                selected = _list[j].chance == 0f ? selected - 1 : selected - _list[j].chance;
                if (selected <= 0f) return j;
            }
            return -1; // same as the game (the caller then fails the same way)
        }

        private static void Parallel(int count, Action<int> body)
        {
            if (count <= 0) return;
            int workers = Math.Max(1, Math.Min(Workers, count / 64));
            if (workers == 1) { for (int i = 0; i < count; i++) body(i); return; }
            Exception error = null;
            using (var done = new CountdownEvent(workers))
            {
                for (int w = 0; w < workers; w++)
                {
                    int from = count * w / workers, to = count * (w + 1) / workers;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { for (int i = from; i < to; i++) body(i); }
                        catch (Exception e) { error = e; }
                        finally { done.Signal(); }
                    });
                }
                done.Wait();
            }
            if (error != null) throw new Exception("worker failed", error);
        }

        // ------------------------------------------------------------------ verify

        private static void VerifyAgainstGame(objGenScript.categoryClass cat, Vector3d key, objGenScript.chunkClass chunk, List<Cell> ours, bool moreFrame)
        {
            // Let the original generate exactly this chunk: temporarily disable, call UpdGen, restore
            Enabled = false;
            try { cat.UpdGen(moreFrame); }
            finally { Enabled = true; }
            VerifyChecks++;
            string diff = null;
            if (!chunk.generated) diff = "game did not generate the chunk";
            else if (chunk.objs.Count != ours.Count) diff = $"object count game {chunk.objs.Count} ours {ours.Count}";
            else
            {
                int i = 0;
                foreach (var kv in chunk.objs)
                {
                    var a = kv.Value; var b = ours[i].Obj;
                    if (!kv.Key.Equals(ours[i].GPosID)) diff = $"obj {i} key";
                    else if (a.seed != b.seed || a.prefabIndex != b.prefabIndex) diff = $"obj {i} seed/prefab";
                    else if (!a.gpos.Equals(b.gpos)) diff = $"obj {i} gpos game {a.gpos} ours {b.gpos}";
                    else if (!a.rot.Equals(b.rot) || a.scale != b.scale) diff = $"obj {i} rot/scale";
                    else if (!a.chunkID.Equals(b.chunkID) || !a.posID.Equals(b.posID)) diff = $"obj {i} ids";
                    if (diff != null) break;
                    i++;
                }
            }
            if (diff != null)
            {
                VerifyMismatches++;
                LastMismatch = $"{cat.name} chunk {key}: {diff}";
                if (VerifyMismatches <= 5) Plugin.Log.LogWarning("ParallelObjGen verify mismatch " + LastMismatch);
            }
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"chunks\":" + Chunks + ",\"cells\":" + Cells + ",\"objects\":" + Objects +
            ",\"maxChunkMs\":" + MaxChunkMs.ToString("F2") + ",\"fallbacks\":" + Fallbacks + ",\"verifyChecks\":" + VerifyChecks +
            ",\"verifyMismatches\":" + VerifyMismatches + ",\"lastMismatch\":" + Json.Str(LastMismatch) + ",\"lastError\":" + Json.Str(LastError) + "}";
    }
}
