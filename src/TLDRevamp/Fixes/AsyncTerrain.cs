using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
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
    /// The worker runs the game's own GenerateTileMesh: a copy of its code made at runtime (a reverse patch, never in
    /// this source) with a few calls swapped (GamePatch). Thread safety (audit: docs/knowledge/internals.md, "Fix #3"):
    /// - The original writes shared scratch fields (the v/v2/n/n2/c arrays, positions, indices) and returns the arrays.
    ///   The worker's copy runs on a stand-in generator per job (Stand-in: the generator's fields copied, its own
    ///   scratch arrays), so every write is the job's own. t/u/t2 are built once per FStart and only read, so they're
    ///   shared as the original shares them.
    /// - Road/POI lookups (mapSettingScript.RoadsNear/POIsNear) read main-thread data, so they run on the main thread at
    ///   dispatch and the copy's two lookup calls return the job's lists. DoPlace only asks for a tile once
    ///   RoadsDoneAtPlace is true.
    /// - terrainGenSettings.Height/Biome call Noises.Noise, which writes scratch fields (r, d, s) inside the struct.
    ///   The copy calls runtime copies of Height/Biome that evaluate each Noises on a *copy* of the struct, so the
    ///   scratch is private.
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

        /// The copies made at runtime: 1 each when their calls were found (else the game's synchronous code runs).
        public static int WorkerPatched, HeightPatched, BiomePatched;

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
            public terrainGenScript StandIn;
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
            if (!Enabled || WorkerPatched == 0 || HeightPatched == 0 || BiomePatched == 0 || ScratchFields == null
                || _regenIfExists || gen.mapSetting == null || gen.mapSetting.useAsync2
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
                Roads = gen.roadAffectsThis ? mapSettingScript.RoadsNear(_gpos) : null,
                Pois = gen.poiAffectsThis ? mapSettingScript.POIsNear(_gpos, gen.tileSize) : null,
            };
            if (!WorkerHoles && HasHoles(job.Pois)) { Fallbacks++; return true; } // old behaviour: keep original for hole tiles
            job.StandIn = StandIn(gen);

            st.Pending.Add(_gpos);
            Dispatched++;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // Must never throw (an unhandled background-thread exception crashed the game once, via the bridge)
                try
                {
                    long t0 = Stopwatch.GetTimestamp();
                    try { job.Mesh = Generate(job); job.StandIn = null; }
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

        private static readonly FieldInfo[] GenFields = AccessTools.GetDeclaredFields(typeof(terrainGenScript)).FindAll(f => !f.IsStatic).ToArray();
        /// The generator's scratch arrays GenerateTileMesh fills and returns: each job gets its own.
        private static readonly FieldInfo[] ScratchFields = Fields("v", "v2", "n", "n2", "c");

        private static FieldInfo[] Fields(params string[] names)
        {
            var all = new FieldInfo[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                all[i] = AccessTools.Field(typeof(terrainGenScript), names[i]);
                if (all[i] == null || !all[i].FieldType.IsArray)
                {
                    Plugin.Log.LogError("AsyncTerrain: terrainGenScript." + names[i] + " isn't an array field any more; tiles stay on the main thread");
                    return null;
                }
            }
            return all;
        }

        /// A stand-in for the generator, for one job (made on the main thread): every field of it copied, then its own
        /// scratch arrays, so the copied GenerateTileMesh writes only what this job owns. Never a live Unity object:
        /// the copy touches its fields only.
        private static terrainGenScript StandIn(terrainGenScript gen)
        {
            var s = (terrainGenScript)FormatterServices.GetUninitializedObject(typeof(terrainGenScript));
            foreach (var f in GenFields) f.SetValue(s, f.GetValue(gen));
            foreach (var f in ScratchFields)
            {
                var a = f.GetValue(gen) as Array;
                f.SetValue(s, a == null ? null : Array.CreateInstance(a.GetType().GetElementType(), a.Length));
            }
            return s;
        }

        [ThreadStatic] private static Job _current;

        private static terrainGenScript.smesh Generate(Job job)
        {
            _current = job;
            try { return WorkerGenerateTileMesh(job.StandIn, job.GPos); }
            finally { _current = null; }
        }

        /// terrainGenScript.GenerateTileMesh as the game has it, copied at runtime, run on a job's stand-in: its road and
        /// POI lookups give the job's lists (looked up on the main thread), its Height and Biome the thread-safe copies.
        [HarmonyReversePatch(HarmonyReversePatchType.Original)]
        [HarmonyPatch(typeof(terrainGenScript), "GenerateTileMesh")]
        private static terrainGenScript.smesh WorkerGenerateTileMesh(terrainGenScript standIn, Vector3d _gpos)
        {
            IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> instructions)
            {
                var p = new GamePatch(nameof(AsyncTerrain) + ".GenerateTileMesh", instructions);
                p.Calls(AccessTools.Method(typeof(mapSettingScript), nameof(mapSettingScript.RoadsNear), new[] { typeof(Vector3d) }), AccessTools.Method(typeof(AsyncTerrain), nameof(JobRoads)), 1);
                p.Calls(AccessTools.Method(typeof(mapSettingScript), nameof(mapSettingScript.POIsNear), new[] { typeof(Vector3d), typeof(float) }), AccessTools.Method(typeof(AsyncTerrain), nameof(JobPois)), 1);
                p.Calls(HeightM, AccessTools.Method(typeof(AsyncTerrain), nameof(Height)), 1);
                p.Calls(BiomeM, AccessTools.Method(typeof(AsyncTerrain), nameof(Biome)), 1);
                return p.Result(ref WorkerPatched);
            }
            _ = Swap(null);
            throw new NotImplementedException("a reverse patch: replaced at runtime");
        }

        public static List<roadGenScript.roadClass> JobRoads(Vector3d _) => _current.Roads;
        public static List<poiGenScript.poiClass> JobPois(Vector3d _, float __) => _current.Pois;

        private static readonly MethodInfo HeightM = AccessTools.Method(typeof(terrainGenSettings), nameof(terrainGenSettings.Height),
            new[] { typeof(Vector3d), typeof(List<roadGenScript.roadClass>), typeof(List<poiGenScript.poiClass>) });
        private static readonly MethodInfo BiomeM = AccessTools.Method(typeof(terrainGenSettings), nameof(terrainGenSettings.Biome), new[] { typeof(Vector3d), typeof(double) });

        /// terrainGenSettings.Height(gpos, roads, pois) safe on any thread (imageMapLoad excluded at dispatch).
        internal static double Height(terrainGenSettings s, Vector3d _gpos, List<roadGenScript.roadClass> _roadlist, List<poiGenScript.poiClass> _poiList)
        {
            // the old 2023 terrain mode: its own landscape (the game's Height is patched the same way)
            if (WorldGen.Old2023.OldTerrain.Active && WorldGen.Old2023.OldTerrain.TryHeight(s, _gpos, _roadlist, _poiList, out double old)) return old;
            return SafeHeight(s, _gpos, _roadlist, _poiList);
        }

        /// terrainGenSettings.Biome(gpos, height) safe on any thread.
        internal static Color Biome(terrainGenSettings s, Vector3d _gpos, double _height)
        {
            if (WorldGen.Old2023.OldTerrain.Active && WorldGen.Old2023.OldTerrain.TryBiome(s, _gpos, _height, out Color oldColor)) return oldColor;
            return SafeBiome(s, _gpos, _height);
        }

        /// The game's Height(gpos, roads, pois), copied at runtime, each Noises evaluated on a copy of the struct.
        [HarmonyReversePatch(HarmonyReversePatchType.Original)]
        [HarmonyPatch(typeof(terrainGenSettings), nameof(terrainGenSettings.Height), new[] { typeof(Vector3d), typeof(List<roadGenScript.roadClass>), typeof(List<poiGenScript.poiClass>) })]
        private static double SafeHeight(terrainGenSettings s, Vector3d _gpos, List<roadGenScript.roadClass> _roadlist, List<poiGenScript.poiClass> _poiList)
        {
            IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> instructions)
            {
                var p = new GamePatch(nameof(AsyncTerrain) + ".Height", instructions);
                p.Calls(NoisesNoise, AccessTools.Method(typeof(AsyncTerrain), nameof(NoiseOnCopy)), 1);
                return p.Result(ref HeightPatched);
            }
            _ = Swap(null);
            throw new NotImplementedException("a reverse patch: replaced at runtime");
        }

        /// The game's Biome(gpos, height), copied at runtime, each Noises evaluated on a copy of the struct.
        [HarmonyReversePatch(HarmonyReversePatchType.Original)]
        [HarmonyPatch(typeof(terrainGenSettings), nameof(terrainGenSettings.Biome), new[] { typeof(Vector3d), typeof(double) })]
        private static Color SafeBiome(terrainGenSettings s, Vector3d _gpos, double _height)
        {
            IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> instructions)
            {
                var p = new GamePatch(nameof(AsyncTerrain) + ".Biome", instructions);
                p.Calls(NoisesNoise, AccessTools.Method(typeof(AsyncTerrain), nameof(NoiseOnCopy)), 4);
                return p.Result(ref BiomePatched);
            }
            _ = Swap(null);
            throw new NotImplementedException("a reverse patch: replaced at runtime");
        }

        private static readonly MethodInfo NoisesNoise = AccessTools.Method(typeof(terrainGenSettings.Noises), nameof(terrainGenSettings.Noises.Noise));

        /// `noises[i].Noise(gpos)` on a copy: Noise writes its scratch (r, d, s) into the struct it runs on.
        public static double NoiseOnCopy(ref terrainGenSettings.Noises n, Vector3d gpos)
        {
            var copy = n;
            return copy.Noise(gpos);
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"dispatched\":" + Dispatched + ",\"placed\":" + Placed +
            ",\"droppedStale\":" + DroppedStale + ",\"droppedUnneeded\":" + DroppedUnneeded + ",\"fallbacks\":" + Fallbacks +
            ",\"errors\":" + Errors + ",\"patched\":" + (WorkerPatched & HeightPatched & BiomePatched) + ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches +
            ",\"workerMsTotal\":" + (WorkerTicks * 1000.0 / Stopwatch.Frequency).ToString("F0") +
            ",\"lastMismatch\":" + Json.Str(LastMismatch) + ",\"lastError\":" + Json.Str(LastError) + "}";
    }
}
