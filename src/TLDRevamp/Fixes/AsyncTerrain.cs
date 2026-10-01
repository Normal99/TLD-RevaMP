using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Each new terrain tile costs ~5.5 ms on the main thread: ~4.8 ms in terrainGenScript.GenerateTileMesh (pure
    /// height/colour/normal maths into arrays) and ~0.7 ms in PlaceMesh (Unity mesh + object). The game's own
    /// "async" path is synchronous (an async method with no await). This computes the arrays on a worker thread
    /// and only runs PlaceMesh on the main thread.
    ///
    /// Thread safety (audit: docs/knowledge/internals.md, "Fix #3"):
    /// - The original writes shared scratch arrays and returns them. Workers use per-job arrays. t/u are built once
    ///   per FStart and only read, so they're shared as the original shares them.
    /// - Road/POI lookups (mapSettingScript.RoadsNear/POIsNear) read main-thread data, so they run on the main thread at
    ///   dispatch. DoPlace only asks for a tile once RoadsDoneAtPlace is true.
    /// - terrainGenSettings.Height/Biome call Noises.Noise, which writes scratch fields (r, d, s) inside the struct.
    ///   Workers call it on a *copy* of each struct, so the scratch is private. Everything below is locals/read-only.
    /// - Anything unusual falls back to the original synchronous code: image maps, useAsync2, regen requests, POI
    ///   terrain holes, and any tile whose worker job threw.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.AsyncTerrain.Enabled / .Verify / .MaxInFlight / .MaxPlacePerFrame
    [HarmonyPatch]
    public static class AsyncTerrain
    {
        public static bool Enabled = true;
        /// On placement, also run the game's GenerateTileMesh on the main thread and compare every array.
        public static bool Verify = false;
        public static int MaxInFlight = 4;
        public static int MaxPlacePerFrame = 1;
        /// Also compute tiles whose POIs cut terrain holes (building foundations) on workers. Off = old main-thread fallback.
        public static bool WorkerHoles = true;

        public static long Dispatched, Placed, DroppedStale, DroppedUnneeded, Fallbacks, Errors, VerifyChecks, VerifyMismatches;
        public static long WorkerTicks;
        public static string LastMismatch = "", LastError = "";

        private static readonly AccessTools.FieldRef<terrainGenScript, Vector3> OffsetRef = AccessTools.FieldRefAccess<terrainGenScript, Vector3>("offset");
        private static readonly AccessTools.FieldRef<terrainGenScript, int[]> TRef = AccessTools.FieldRefAccess<terrainGenScript, int[]>("t");
        private static readonly AccessTools.FieldRef<terrainGenScript, Vector2[]> URef = AccessTools.FieldRefAccess<terrainGenScript, Vector2[]>("u");
        private static readonly AccessTools.FieldRef<terrainGenScript, int[]> T2Ref = AccessTools.FieldRefAccess<terrainGenScript, int[]>("t2");

        private static readonly Action<terrainGenScript, Vector3d, terrainGenScript.smesh> PlaceMesh =
            AccessTools.MethodDelegate<Action<terrainGenScript, Vector3d, terrainGenScript.smesh>>(
                AccessTools.Method(typeof(terrainGenScript), "PlaceMesh"));
        private static readonly Func<terrainGenScript, Vector3d, terrainGenScript.smesh> OriginalGenerate =
            AccessTools.MethodDelegate<Func<terrainGenScript, Vector3d, terrainGenScript.smesh>>(
                AccessTools.Method(typeof(terrainGenScript), "GenerateTileMesh"));

        private class Job
        {
            // Snapshot taken on the main thread at dispatch
            public Vector3d GPos;
            public Dictionary<Vector3d, newTerrainTileScript> Generation; // FStart replaces this dict, so it marks staleness
            public terrainGenSettings Setting;
            public int Resolution;
            public float VertexDist;
            public float TileSize;
            public Vector3 Offset;
            public int[] T, T2;
            public Vector2[] U;
            public List<roadGenScript.roadClass> Roads;
            public List<poiGenScript.poiClass> Pois;
            // Filled by the worker
            public terrainGenScript.smesh Mesh;
            public Exception Error;
        }

        private class State
        {
            public readonly HashSet<Vector3d> Pending = new HashSet<Vector3d>();
            public readonly HashSet<Vector3d> ForceSync = new HashSet<Vector3d>();
            public readonly ConcurrentQueue<Job> Done = new ConcurrentQueue<Job>();
            public Dictionary<Vector3d, newTerrainTileScript> Generation;
        }

        private static readonly ConditionalWeakTable<terrainGenScript, State> States = new ConditionalWeakTable<terrainGenScript, State>();

        private static State StateOf(terrainGenScript gen)
        {
            var st = States.GetValue(gen, _ => new State());
            if (st.Generation != gen.terrains)
            {
                // FStart/Restart rebuilt the terrain: everything in flight belongs to the old generation
                st.Generation = gen.terrains;
                st.Pending.Clear();
                st.ForceSync.Clear();
            }
            return st;
        }

        // ---------------------------------------------------------------- dispatch (main thread)

        [HarmonyPatch(typeof(terrainGenScript), "Tile", new[] { typeof(Vector3d), typeof(bool), typeof(bool) })]
        [HarmonyPrefix]
        private static bool TilePrefix(terrainGenScript __instance, Vector3d _gpos, bool _regenIfExists, ref bool __result)
        {
            try { return Dispatch(__instance, _gpos, _regenIfExists, ref __result); }
            catch (Exception e) { Disable("dispatch", e); return true; }
        }

        private static void Disable(string where, Exception e)
        {
            Enabled = false;
            Errors++;
            LastError = where + ": " + e;
            Plugin.Log.LogError("AsyncTerrain disabled after error in " + where + ": " + e);
        }

        private static bool Dispatch(terrainGenScript __instance, Vector3d _gpos, bool _regenIfExists, ref bool __result)
        {
            var gen = __instance;
            if (!Enabled || _regenIfExists || gen.mapSetting == null || gen.mapSetting.useAsync2
                || gen.mapSetting.imageMapLoad != null || gen.setting == null || gen.setting.mapSetting == null
                || gen.setting.mapSetting.imageMapLoad != null)
                return true;

            if (gen.terrains.ContainsKey(_gpos)) { __result = false; return false; } // same as original

            var st = StateOf(gen);
            if (st.ForceSync.Remove(_gpos)) { Fallbacks++; return true; }
            if (st.Pending.Contains(_gpos)) { __result = false; return false; } // in flight: let DoPlace try the next coord
            if (st.Pending.Count >= MaxInFlight) { __result = true; return false; } // throttle: spend this DoPlace call waiting

            var job = new Job
            {
                GPos = _gpos,
                Generation = gen.terrains,
                Setting = gen.setting,
                Resolution = gen.resolution,
                VertexDist = gen.vertexDist,
                TileSize = gen.tileSize,
                Offset = OffsetRef(gen),
                T = TRef(gen),
                T2 = T2Ref(gen),
                U = URef(gen),
                Roads = gen.roadAffectsThis ? mapSettingScript.RoadsNear(_gpos) : null,
                Pois = gen.poiAffectsThis ? mapSettingScript.POIsNear(_gpos, gen.tileSize) : null,
            };
            if (!WorkerHoles && HasHoles(job.Pois)) { Fallbacks++; return true; } // old behaviour: keep original for hole tiles

            st.Pending.Add(_gpos);
            Dispatched++;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // Must never throw (an unhandled background-thread exception crashed the game once, via the bridge)
                try
                {
                    long t0 = Stopwatch.GetTimestamp();
                    try { job.Mesh = Generate(job); }
                    catch (Exception e) { job.Error = e; }
                    Interlocked.Add(ref WorkerTicks, Stopwatch.GetTimestamp() - t0);
                    st.Done.Enqueue(job);
                }
                catch { }
            });
            __result = true; // like the original: one new tile per DoPlace call
            return false;
        }

        private static bool HasHoles(List<poiGenScript.poiClass> pois)
        {
            if (pois == null) return false;
            for (int i = 0; i < pois.Count; i++)
                if (pois[i].terrainHoleQuads != null && pois[i].terrainHoleQuads.Count > 0) return true;
            return false;
        }

        // ---------------------------------------------------------------- placement (main thread)

        [HarmonyPatch(typeof(terrainGenScript), nameof(terrainGenScript.Upd))]
        [HarmonyPostfix]
        private static void UpdPostfix(terrainGenScript __instance)
        {
            try { PlaceDone(__instance); }
            catch (Exception e) { Disable("placement", e); }
        }

        private static void PlaceDone(terrainGenScript gen)
        {
            if (!States.TryGetValue(gen, out _)) return; // never dispatched anything for this instance
            var live = StateOf(gen);
            int placed = 0;
            while (placed < MaxPlacePerFrame && live.Done.TryDequeue(out var job))
            {
                if (job.Generation != gen.terrains || !gen.isActiveAndEnabled) { DroppedStale++; continue; }
                live.Pending.Remove(job.GPos);
                if (job.Error != null)
                {
                    Errors++;
                    LastError = job.Error.GetType().Name + ": " + job.Error.Message;
                    live.ForceSync.Add(job.GPos); // DoPlace will ask again; that time the original runs
                    continue;
                }
                if (gen.terrains.ContainsKey(job.GPos) || !gen.coordsNeeded.Contains(job.GPos)) { DroppedUnneeded++; continue; }

                if (TerrainSync.Outdated(gen, job.GPos, job.Roads, job.Pois)) { DroppedStale++; continue; } // DoPlace asks again
                if (Verify) Compare(gen, job);
                TerrainSync.Next(gen, job.GPos, job.Roads, job.Pois);
                PlaceMesh(gen, job.GPos, job.Mesh);
                Placed++;
                placed++;
            }
        }

        private static void Compare(terrainGenScript gen, Job job)
        {
            VerifyChecks++;
            var orig = OriginalGenerate(gen, job.GPos); // returns the game's shared scratch arrays; compare before anything else runs
            string diff = Diff("v", orig.v, job.Mesh.v) ?? Diff("n", orig.n, job.Mesh.n) ?? Diff("c", orig.c, job.Mesh.c)
                          ?? (ReferenceEquals(orig.t, job.Mesh.t) || Same(orig.t, job.Mesh.t) ? null : "t differs")
                          ?? (ReferenceEquals(orig.u, job.Mesh.u) || Same(orig.u, job.Mesh.u) ? null : "u differs");
            if (diff != null)
            {
                VerifyMismatches++;
                LastMismatch = "tile " + job.GPos + ": " + diff;
                if (VerifyMismatches <= 5) Plugin.Log.LogWarning("AsyncTerrain verify mismatch " + LastMismatch);
            }
        }

        private static string Diff<T>(string name, T[] a, T[] b) where T : struct
        {
            if (a == null || b == null || a.Length != b.Length) return name + " length";
            for (int i = 0; i < a.Length; i++)
                if (!a[i].Equals(b[i])) return $"{name}[{i}] game={a[i]} ours={b[i]}";
            return null;
        }

        private static bool Same<T>(T[] a, T[] b) where T : struct => Diff("x", a, b) == null;

        // ---------------------------------------------------------------- generation (worker thread)

        /// Mirror of terrainGenScript.GenerateTileMesh without the POI-hole branch (those tiles never get here),
        /// with per-job arrays. Operation order is kept identical so results match bit for bit (Verify checks it).
        private static terrainGenScript.smesh Generate(Job job)
        {
            int resolution = job.Resolution;
            var v = new Vector3[resolution * resolution];
            var n = new Vector3[resolution * resolution];
            var c = new Color[resolution * resolution];
            var v2 = new Vector3[(resolution + 2) * (resolution + 2)];
            var n2 = new Vector3[(resolution + 2) * (resolution + 2)];
            var t2 = job.T2;

            for (int i = -1; i < resolution + 1; i++)
            {
                for (int j = -1; j < resolution + 1; j++)
                {
                    int i2 = j + 1 + (i + 1) * (resolution + 2);
                    Vector3 currentPos = new Vector3(j, 0f, i) * job.VertexDist + job.Offset;
                    Vector3d currentGPos = job.GPos + currentPos;
                    currentGPos.y = Height(job.Setting, currentGPos, job.Roads, job.Pois);
                    currentPos.y = (float)currentGPos.y;
                    v2[i2] = currentPos;
                    if (j >= 0 && j < resolution && i >= 0 && i < resolution)
                    {
                        int idx = j + i * resolution;
                        v[idx] = currentPos;
                        c[idx] = Biome(job.Setting, currentGPos, currentGPos.y);
                    }
                }
            }
            for (int k = 0; k < n2.Length; k++) n2[k] = Vector3.zero;
            for (int l = 0; l + 2 < t2.Length; l += 3)
            {
                Vector3 vertexA = v2[t2[l]];
                Vector3 vertexB = v2[t2[l + 1]];
                Vector3 vertexC = v2[t2[l + 2]];
                Vector3 triangleNormal = terrainGenScript.CalculateTriangleNormal(vertexA, vertexB, vertexC);
                n2[t2[l]] += triangleNormal;
                n2[t2[l + 1]] += triangleNormal;
                n2[t2[l + 2]] += triangleNormal;
            }
            for (int m = 0; m < n2.Length; m++) n2[m] = n2[m].normalized;
            for (int a = 1; a < resolution + 1; a++)
                for (int b = 1; b < resolution + 1; b++)
                    n[b - 1 + (a - 1) * resolution] = n2[b + a * (resolution + 2)];

            var result = new terrainGenScript.smesh { v = v, n = n, c = c, t = job.T, u = job.U };
            if (job.Pois != null) Holes(job, v, ref result);
            return result;
        }

        /// Mirror of the POI terrain-hole branch at the end of terrainGenScript.GenerateTileMesh (building foundations).
        /// Only uses pure mainscript helpers; `v` is this job's own array. Order of operations kept identical.
        private static void Holes(Job job, Vector3[] v, ref terrainGenScript.smesh result)
        {
            var list = job.Pois;
            var t = job.T;
            var u = job.U;
            Vector3d _gpos = job.GPos;
            Vector3d currentGPos;
            bool flag = false;
            List<int> list2 = new List<int>(t);
            for (int num2 = 0; num2 < list.Count; num2++)
            {
                if (list[num2].terrainHoleQuads == null) continue;
                for (int num3 = 0; num3 < list[num2].terrainHoleQuads.Count; num3++)
                {
                    if (!list[num2].terrainHoleQuads[num3].InsideTile(_gpos, (double)job.TileSize * 0.5 + (double)(job.VertexDist * 2f))) continue;
                    List<int> list3 = new List<int>();
                    double num4 = double.MaxValue;
                    Vector3d vector3d = mainscript.Center(list[num2].terrainHoleQuads[num3].corners);
                    int num5 = -1;
                    for (int num6 = 0; num6 < v.Length; num6++)
                    {
                        currentGPos = _gpos + v[num6];
                        Vector3d vector3d2 = currentGPos - vector3d;
                        vector3d2.y = 0.0;
                        double magnitude = vector3d2.magnitude;
                        if (magnitude < num4) { num4 = magnitude; num5 = num6; }
                        if (mainscript.IsPointInRectangle(currentGPos, list[num2].terrainHoleQuads[num3].corners)) list3.Add(num6);
                    }
                    if (num5 == -1) continue;
                    if (!list3.Contains(num5)) list3.Add(num5);
                    flag = true;
                    List<int> list4 = new List<int>();
                    for (int num7 = 0; num7 < list3.Count; num7++)
                    {
                        List<int> connectedVertices = mainscript.GetConnectedVertices(t, list3[num7]);
                        for (int num8 = 0; num8 < connectedVertices.Count; num8++)
                            if (!list3.Contains(connectedVertices[num8]) && !list4.Contains(connectedVertices[num8])) list4.Add(connectedVertices[num8]);
                    }
                    list3.AddRange(list4);
                    for (int num9 = 0; num9 < list3.Count; num9++)
                    {
                        currentGPos = _gpos + v[list3[num9]];
                        v[list3[num9]] = (Vector3)(mainscript.ClosestNoY(currentGPos, list[num2].terrainHoleQuads[num3].corners) - _gpos);
                    }
                    for (int num10 = 0; num10 < list2.Count; num10 += 3)
                    {
                        if (((_gpos + v[list2[num10]] + (_gpos + v[list2[num10 + 1]])) * 0.5 - vector3d).magnitude < 0.1 || ((_gpos + v[list2[num10 + 1]] + (_gpos + v[list2[num10 + 2]])) * 0.5 - vector3d).magnitude < 0.1 || ((_gpos + v[list2[num10 + 2]] + (_gpos + v[list2[num10]])) * 0.5 - vector3d).magnitude < 0.1)
                        {
                            for (int num11 = 0; num11 < 3; num11++)
                            {
                                currentGPos = _gpos + v[list2[num10 + num11]];
                                v[list2[num10 + num11]] = (Vector3)(mainscript.ClosestNoY(currentGPos, list[num2].terrainHoleQuads[num3].corners) - _gpos);
                            }
                            list2.RemoveRange(num10, 3);
                            num10 -= 3;
                        }
                    }
                    for (int num12 = 0; num12 < v.Length; num12++)
                    {
                        currentGPos = _gpos + v[num12];
                        Vector3d vector3d3 = mainscript.ClosestNoY(currentGPos, list[num2].terrainHoleQuads[num3].corners);
                        Vector3d vector3d2 = currentGPos - vector3d3;
                        vector3d2.y = 0.0;
                        if (vector3d2.magnitude <= (double)(job.VertexDist * 2f)) v[num12] = (Vector3)(vector3d3 - _gpos);
                    }
                }
            }
            if (flag)
            {
                Vector2[] array = new Vector2[u.Length];
                for (int num13 = 0; num13 < array.Length; num13++)
                    array[num13] = new Vector2((float)(((double)v[num13].x - _gpos.x - (double)job.TileSize * 0.5) / (double)job.TileSize), (float)(((double)v[num13].z - _gpos.z - (double)job.TileSize * 0.5) / (double)job.TileSize));
                result.t = list2.ToArray();
                result.u = array;
            }
        }

        /// terrainGenSettings.Height(gpos, roads, pois) with noise evaluated on struct copies (imageMapLoad excluded at dispatch).
        internal static double Height(terrainGenSettings s, Vector3d _gpos, List<roadGenScript.roadClass> _roadlist, List<poiGenScript.poiClass> _poiList)
        {
            double num = 0.0;
            var noises = s.terrainNoises;
            for (int i = 0; i < noises.Length; i++)
            {
                terrainGenSettings.Noises copy = noises[i];
                num += copy.Noise(_gpos);
            }
            if (_poiList != null && _poiList.Count > 0)
            {
                poiGenScript.terrainAlignValueClass terrainAlignValueClass = poiGenScript.TerrainAlignsValue(_gpos, _poiList);
                if (terrainAlignValueClass != null && terrainAlignValueClass.lerp != 0.0)
                    num = Mathd.Lerp(num, terrainAlignValueClass.height, terrainAlignValueClass.lerp);
            }
            if (_roadlist != null && _roadlist.Count > 0)
            {
                roadGenScript.roadClass roadClass = roadGenScript.NearestPointOnRoadBone(_gpos, _roadlist, out var _result);
                if (roadClass != null)
                {
                    Vector3d vector3d = _result;
                    vector3d.y = _gpos.y;
                    double magnitude = (vector3d - _gpos).magnitude;
                    float terrainAlignMinDist = roadClass.terrainAlignMinDist;
                    float terrainAlignMaxDist = roadClass.terrainAlignMaxDist;
                    if (magnitude != 0.0 && magnitude != double.MaxValue)
                        num = Mathd.Lerp(num, _result.y, Mathd.InverseLerp(-1.0, 1.0, Mathd.Sin(Mathd.Lerp(-90.0, 90.0, Mathd.InverseLerp(terrainAlignMaxDist, terrainAlignMinDist, magnitude)) * 0.01745329)));
                }
            }
            return num;
        }

        /// terrainGenSettings.Biome(gpos, height) with noise on struct copies.
        internal static Color Biome(terrainGenSettings s, Vector3d _gpos, double _height)
        {
            var bn = s.biomeNoises;
            terrainGenSettings.Noises b0 = bn[0], b1 = bn[1], b2 = bn[2], b3 = bn[3];
            Color result = default;
            result.r = Mathf.Clamp((float)b0.Noise(_gpos), 0f, 1f);
            result.g = Mathf.Clamp((float)b1.Noise(_gpos), 0f, 1f);
            result.b = Mathf.Clamp((float)Mathd.Max(Mathd.InverseLerp(s.snowMinHeight, s.snowMaxHeight, _height + s.snowHeightNoise.Noise(_gpos)), b2.Noise(_gpos)), 0f, 1f);
            result.a = Mathf.Clamp((float)b3.Noise(_gpos), 0f, 1f);
            return result;
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"dispatched\":" + Dispatched + ",\"placed\":" + Placed +
            ",\"droppedStale\":" + DroppedStale + ",\"droppedUnneeded\":" + DroppedUnneeded + ",\"fallbacks\":" + Fallbacks +
            ",\"errors\":" + Errors + ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches +
            ",\"workerMsTotal\":" + (WorkerTicks * 1000.0 / Stopwatch.Frequency).ToString("F0") +
            ",\"lastMismatch\":" + Json.Str(LastMismatch) + ",\"lastError\":" + Json.Str(LastError) + "}";
    }
}
