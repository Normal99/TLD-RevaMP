using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// mapSettingScript.GetChunksNeeded builds a new List and sorts it with LINQ (OrderBy(...).ToList()) on every call.
    /// It's called every frame by the road generator and by each object category (8×/frame), and the callers only
    /// assign the result to their own `chunksNeeded` field, which nothing else keeps (checked in the decompiled code
    /// and in this mod).
    ///
    /// Same result, no garbage: at those two call sites the caller's existing list is passed in, cleared and refilled
    /// with the game's exact loop, then stable-sorted by the same key (LINQ OrderBy is stable; insertion sort with a
    /// strict comparison keeps ties in order). The caller then assigns the same list object to its field.
    /// Verify: also runs the game's GetChunksNeeded and compares element by element (Equals).
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.ChunksNeededInPlace.Enabled / .Verify
    [HarmonyPatch]
    public static class ChunksNeededInPlace
    {
        /// On by default since v0.20.1: exact (21,175 verified calls, 0 mismatches), driving A/B collections −27%, max −23%.
        public static bool Enabled = true;
        public static bool Verify = false;
        public static long Calls, VerifyChecks, VerifyMismatches;
        public static string LastMismatch = "";
        public static int Patched;

        /// Game bug with several players (multiplayer host: one generation centre per player): the list gets the 5×5 chunks
        /// around every centre appended without removing duplicates (10 players close together: 250 entries, 25 chunks).
        /// Every consumer walks the list; for each of them a repeated chunk can't do anything its first visit didn't
        /// (generation/connection flags, "one action then stop" loops, Contains checks), so dropping repeats is exact.
        /// Dropping them before the stable sort gives the same order as sorting and then dropping later repeats.
        public static bool Dedup = true;
        private static readonly HashSet<Vector3d> Seen = new HashSet<Vector3d>();

        private static readonly MethodInfo Original = AccessTools.Method(typeof(mapSettingScript), nameof(mapSettingScript.GetChunksNeeded));
        private static readonly MethodInfo Replacement = AccessTools.Method(typeof(ChunksNeededInPlace), nameof(Get));
        private static double[] _keys = new double[64];

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(roadGenScript), nameof(roadGenScript.UpdChunksNeeded), new System.Type[0]);
            yield return AccessTools.Method(typeof(objGenScript.categoryClass), nameof(objGenScript.categoryClass.UpdChunksNeeded));
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var field = AccessTools.Field(original.DeclaringType, "chunksNeeded");
            foreach (var ins in instructions)
            {
                if (ins.Calls(Original))
                {
                    // stack: mapSetting, float, int  →  + this.chunksNeeded  →  Get(mapSetting, float, int, list)
                    // (the call itself is never a branch target: its arguments are pushed right before it)
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldfld, field);
                    ins.opcode = OpCodes.Call;
                    ins.operand = Replacement;
                    Patched++;
                    yield return ins;
                    continue;
                }
                yield return ins;
            }
        }

        public static List<Vector3d> Get(mapSettingScript ms, float _chunkSize, int _edge, List<Vector3d> own)
        {
            if (!Enabled || own == null) return ms.GetChunksNeeded(_chunkSize, _edge);
            Calls++;
            var list = own;
            list.Clear();
            var genArounds = ms.genArounds;
            for (int i = 0; i < genArounds.Count; i++)
                for (int j = -_edge / 2; j <= _edge / 2; j++)
                    for (int k = -_edge / 2; k <= _edge / 2; k++)
                    {
                        var c = mainscript.GetChunkPos(genArounds[i].upos, k, j, _chunkSize);
                        if (!Dedup || Seen.Add(c)) list.Add(c);
                    }
            if (Dedup) Seen.Clear();
            if (genArounds.Count > 0)
            {
                Vector3d orderTo = mainscript.GlobalFromUnityPos(genArounds[0].upos);
                int n = list.Count;
                if (_keys.Length < n) _keys = new double[n * 2];
                var keys = _keys;
                for (int i = 0; i < n; i++) keys[i] = (list[i] - orderTo).sqrMagnitude;
                // stable insertion sort (same order as LINQ OrderBy)
                for (int i = 1; i < n; i++)
                {
                    double key = keys[i];
                    var v = list[i];
                    int j = i - 1;
                    while (j >= 0 && keys[j] > key)
                    {
                        keys[j + 1] = keys[j];
                        list[j + 1] = list[j];
                        j--;
                    }
                    keys[j + 1] = key;
                    list[j + 1] = v;
                }
            }
            if (Verify)
            {
                VerifyChecks++;
                var reference = ms.GetChunksNeeded(_chunkSize, _edge);
                if (Dedup) { var seen = new HashSet<Vector3d>(); reference = reference.FindAll(x => seen.Add(x)); }
                string diff = reference.Count != list.Count ? "count " + list.Count + " vs " + reference.Count : null;
                for (int i = 0; diff == null && i < list.Count; i++) if (!list[i].Equals(reference[i])) diff = "element " + i;
                if (diff != null) { VerifyMismatches++; LastMismatch = diff; }
            }
            return list;
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"patched\":" + Patched + ",\"calls\":" + Calls +
            ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches + ",\"lastMismatch\":" + Json.Str(LastMismatch) + "}";
    }
}
