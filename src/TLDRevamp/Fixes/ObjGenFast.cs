using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// objGenScript.categoryClass.UpdGen generates one whole chunk of scattered objects (rocks, cacti, grass…) in one
    /// frame. Driving bench (seed 20260924, east route, 90 km/h): UpdGen max 18 ms, 25 frames >5 ms per pass.
    ///
    /// Why (from the code + in-game counts): per grid cell the game constructs TWO System.Randoms (2.37 µs each incl. a
    /// draw, measured in-game): chunkRnd, seeded only by the cell's biome chunk (10,000–40,000 cells share one), and
    /// objRnd, whose first number is the chance test that rejects ~96% of cells. SmallRocks: 2,809 cells/chunk →
    /// ~13 ms of Random construction alone.
    ///
    /// The game's own UpdGen with its two `new System.Random(seed)` swapped at runtime (GamePatch) for a Random that
    /// gives the same numbers without constructing one each time: chunkRnd's two numbers are remembered per seed, objRnd's
    /// first comes from FastRandom.FirstDouble (allocation-free System.Random seeding, checked against the real one
    /// in-game), and a real Random is made only when more numbers are asked for (the cells that pass the chance test),
    /// moved past the ones already given. Verify draws every number from a real Random of the same seed as well and
    /// compares them.
    ///
    /// SceneryTileSync rides on the same patch: the tile cache's lists are computed at the tile's own position (the
    /// first RoadsNear/POIsNear pair, the cached branch), and a chunk it regenerates is generated again by this same
    /// method on a chunk list of its own (Regenerate).
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.ObjGenFast.Enabled / .Verify
    [HarmonyPatch(typeof(objGenScript.categoryClass), nameof(objGenScript.categoryClass.UpdGen))]
    public static class ObjGenFast
    {
        /// On by default since v0.20.0: exact (randcheck 2M seeds, chunk verify 200 chunks: 0 mismatches), driving A/B p99 −11%, max −22%.
        public static bool Enabled = true;
        public static bool Verify = false;
        public static int Patched;
        public static long ChunkRandoms, ObjRandoms, MemoHits, MemoMisses, RealRandoms, Regenerated, VerifyChecks, VerifyMismatches, Fallbacks;
        public static string LastMismatch = "", LastError = "";

        /// Generation is ours (the patched method applied and nothing else generates scenery): SceneryTileSync needs it.
        internal static bool On => Enabled && Patched == 1 && !ParallelObjGen.Enabled;

        private sealed class PerCat
        {
            public readonly SwapRandom Chunk = new SwapRandom(), Obj = new SwapRandom();
            public readonly Dictionary<int, double[]> Memo = new Dictionary<int, double[]>();   // seed → chunkRnd's two numbers
        }
        private static readonly ConditionalWeakTable<objGenScript.categoryClass, PerCat> Cats = new ConditionalWeakTable<objGenScript.categoryClass, PerCat>();
        private const int MemoMax = 200000;

        /// The category being regenerated (Regenerate): its chunk list is ours, not the player's.
        private static objGenScript.categoryClass _regenerating;

        private static MethodInfo Our(string name) => AccessTools.Method(typeof(ObjGenFast), name);

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var cat = typeof(objGenScript.categoryClass);
            var newRandom = AccessTools.Constructor(typeof(System.Random), new[] { typeof(int) });
            var p = new GamePatch(nameof(ObjGenFast), instructions);
            p.Constructs(newRandom, AccessTools.Field(cat, "chunkRnd"), Our(nameof(ChunkRandom)), 1, withThis: true);
            p.Constructs(newRandom, AccessTools.Field(cat, "objRnd"), Our(nameof(ObjRandom)), 1, withThis: true);
            p.Calls(AccessTools.Method(cat, nameof(objGenScript.categoryClass.UpdChunksNeeded)), Our(nameof(ChunksNeeded)), 1);
            // the cached branch's pair comes first, the uncached one's second
            p.CallAt(AccessTools.Method(typeof(mapSettingScript), nameof(mapSettingScript.RoadsNear)), 0, 2, Our(nameof(TileRoadsNear)), withThis: true);
            p.CallAt(AccessTools.Method(typeof(mapSettingScript), nameof(mapSettingScript.POIsNear)), 0, 2, Our(nameof(TilePoisNear)), withThis: true);
            return p.Result(ref Patched);
        }

        /// Before the game's generation: one chunk SceneryTileSync queued is generated again (all when !_moreFrame); with
        /// _moreFrame that is this frame's work, new chunks come next frame.
        [HarmonyPrefix]
        private static bool Prefix(objGenScript.categoryClass __instance, bool _moreFrame)
        {
            if (_regenerating != null || !On) return true;
            try { return !(SceneryTileSync.RegenPending(__instance, _moreFrame) && _moreFrame); }
            catch (Exception e)
            {
                // a regeneration that failed half way leaves the chunk as it was (Merge comes last)
                Fallbacks++;
                LastError = e.ToString();
                Enabled = false;
                Plugin.Log.LogError("ObjGenFast disabled after error: " + e);
                return true;
            }
        }

        /// `chunkRnd = new System.Random(seed)`: its two numbers (SelectRandomProperty's and the lerp's) remembered per seed.
        public static System.Random ChunkRandom(int seed, objGenScript.categoryClass cat)
        {
            if (!Enabled) return new System.Random(seed);
            ChunkRandoms++;
            var pc = Cats.GetValue(cat, _ => new PerCat());
            if (pc.Memo.TryGetValue(seed, out var known)) MemoHits++;
            else
            {
                var r = new System.Random(seed);
                known = new[] { r.NextDouble(), r.NextDouble() };
                if (pc.Memo.Count >= MemoMax) pc.Memo.Clear();
                pc.Memo.Add(seed, known);
                MemoMisses++;
            }
            return pc.Chunk.Reset(seed, known);
        }

        /// `objRnd = new System.Random(seed)`: its first number without a Random, the rest from a real one when asked for.
        public static System.Random ObjRandom(int seed, objGenScript.categoryClass cat)
        {
            if (!Enabled) return new System.Random(seed);
            ObjRandoms++;
            return Cats.GetValue(cat, _ => new PerCat()).Obj.Reset(seed, null);
        }

        public static void ChunksNeeded(objGenScript.categoryClass cat)
        {
            if (cat != _regenerating) cat.UpdChunksNeeded();
        }

        /// The tile cache's lists are computed at the tile, not at the first object that hit it (SceneryTileSync).
        public static List<roadGenScript.roadClass> TileRoadsNear(Vector3d gpos, objGenScript.categoryClass cat) => mapSettingScript.RoadsNear(TileAt(gpos, cat));
        public static List<poiGenScript.poiClass> TilePoisNear(Vector3d gpos, float size, objGenScript.categoryClass cat) => mapSettingScript.POIsNear(TileAt(gpos, cat), size);
        private static Vector3d TileAt(Vector3d gpos, objGenScript.categoryClass cat) =>
            SceneryTileSync.Active && !ImageMap(cat) ? mainscript.GetChunkPos(gpos, cat.script.tileSize) : gpos;

        /// A map from an image: the game's generation as it is (no regeneration, no tile positions).
        private static bool ImageMap(objGenScript.categoryClass cat)
        {
            var s = cat.script;
            return s == null || s.terrain == null || s.terrain.mapSetting == null || s.terrain.mapSetting.imageMapLoad != null;
        }

        /// Generate an already generated chunk again from the current roads/buildings and merge (SceneryTileSync): the
        /// game's UpdGen on a chunk list holding only this chunk, fresh.
        internal static void Regenerate(objGenScript.categoryClass cat, Vector3d key, objGenScript.chunkClass chunk)
        {
            if (ImageMap(cat)) return;
            var generated = cat.generatedChunks;
            var needed = cat.chunksNeeded;
            var fresh = new objGenScript.chunkClass();
            cat.generatedChunks = new Dictionary<Vector3d, objGenScript.chunkClass> { { key, fresh } };
            cat.chunksNeeded = new List<Vector3d> { key };
            _regenerating = cat;
            try { cat.UpdGen(true); }
            finally { cat.generatedChunks = generated; cat.chunksNeeded = needed; _regenerating = null; }
            Regenerated++;
            SceneryTileSync.Merge(cat, chunk, fresh.objs);
        }

        /// The numbers `new System.Random(seed)` gives, without making one until needed: the first NextDouble()s from
        /// what's known about the seed (FastRandom.FirstDouble, or chunkRnd's remembered two), everything after from a
        /// real Random made then and moved past them. One per category and use, reset for every cell (the game keeps
        /// chunkRnd/objRnd only for the cell; UpdGen runs on the main thread).
        private sealed class SwapRandom : System.Random
        {
            private int _seed, _pos;
            private double[] _known;
            private System.Random _real, _shadow;

            public SwapRandom() : base(0) { }

            public SwapRandom Reset(int seed, double[] known)
            {
                _seed = seed; _pos = 0; _known = known; _real = null;
                _shadow = Verify ? new System.Random(seed) : null;
                return this;
            }

            private System.Random Real()
            {
                if (_real != null) return _real;
                _real = new System.Random(_seed);
                for (int i = 0; i < _pos; i++) _real.NextDouble();   // the ones already given
                RealRandoms++;
                return _real;
            }

            public override double NextDouble()
            {
                double v;
                if (_real != null) v = _real.NextDouble();
                else if (_known != null && _pos < _known.Length) v = _known[_pos];
                else if (_known == null && _pos == 0) v = FastRandom.FirstDouble(_seed);
                else v = Real().NextDouble();
                _pos++;
                if (_shadow != null) Check(BitConverter.DoubleToInt64Bits(v) == BitConverter.DoubleToInt64Bits(_shadow.NextDouble()), "NextDouble", v);
                return v;
            }

            protected override double Sample() => NextDouble();

            public override int Next()
            {
                int v = Real().Next();
                if (_shadow != null) Check(v == _shadow.Next(), "Next()", v);
                return v;
            }

            public override int Next(int maxValue)
            {
                int v = Real().Next(maxValue);
                if (_shadow != null) Check(v == _shadow.Next(maxValue), "Next(max)", v);
                return v;
            }

            public override int Next(int minValue, int maxValue)
            {
                int v = Real().Next(minValue, maxValue);
                if (_shadow != null) Check(v == _shadow.Next(minValue, maxValue), "Next(min, max)", v);
                return v;
            }

            public override void NextBytes(byte[] buffer)
            {
                Real().NextBytes(buffer);
                if (_shadow == null) return;
                var b = new byte[buffer.Length];
                _shadow.NextBytes(b);
                bool same = true;
                for (int i = 0; i < b.Length; i++) same &= b[i] == buffer[i];
                Check(same, "NextBytes", buffer.Length);
            }

            private void Check(bool same, string what, double v)
            {
                VerifyChecks++;
                if (same) return;
                VerifyMismatches++;
                LastMismatch = $"seed {_seed} number {_pos} {what}: {v}";
                if (VerifyMismatches <= 5) Plugin.Log.LogWarning("ObjGenFast verify mismatch " + LastMismatch);
            }
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"patched\":" + Patched + ",\"verify\":" + (Verify ? "true" : "false") +
            ",\"chunkRandoms\":" + ChunkRandoms + ",\"objRandoms\":" + ObjRandoms + ",\"memoHits\":" + MemoHits + ",\"memoMisses\":" + MemoMisses +
            ",\"realRandoms\":" + RealRandoms + ",\"regenerated\":" + Regenerated +
            ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches + ",\"fallbacks\":" + Fallbacks +
            ",\"lastMismatch\":" + Json.Str(LastMismatch) + ",\"lastError\":" + Json.Str(LastError) + "}";
    }
}
