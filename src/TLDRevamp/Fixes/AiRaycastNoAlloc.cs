using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// newAiScript (the AI characters, e.g. at the haunted mansion) does, per AI per frame, in Update (vision),
    /// AvoidWalls (a grid of rays) and Attack:
    ///     hits = Physics.RaycastAll(...);  hits = hits.OrderBy(h => h.distance).ToArray();
    /// Each allocates a new array plus LINQ buffers. Measured at the mansion (crowdedtest save, 4 AIs): newAiScript.Update
    /// 1.2 MB/s of garbage, the biggest garbage source in the scene.
    ///
    /// Same query, same order, no garbage: RaycastNonAlloc into a buffer, copied into a reused array of exactly the hit
    /// count (per call site and length, so a caller's loop never sees its array change under it — Attack runs inside
    /// AvoidWalls' loop), then a stable insertion sort by distance (LINQ OrderBy is stable). The OrderBy/ToArray calls
    /// become pass-throughs. Buffer full (≥ 128 hits) → the original RaycastAll path. Only difference possible: hits at
    /// exactly equal distance could come back in another order if RaycastNonAlloc lists them differently than RaycastAll;
    /// Verify compares every call with the original.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.AiRaycastNoAlloc.Enabled / .Verify
    [HarmonyPatch]
    public static class AiRaycastNoAlloc
    {
        /// On by default since v0.28.0: crowdedtest (mansion, 4 AIs): garbage 23.8 → 10.8 KB/frame, GC 1.5 → 0 per 10 s;
        /// verify 24,839 calls, 0 mismatches.
        public static bool Enabled = true;
        public static bool Verify = false;
        public static long Calls, Fallbacks, VerifyChecks, VerifyMismatches;
        public static int Patched;

        private static readonly MethodInfo RaycastAll = AccessTools.Method(typeof(Physics), nameof(Physics.RaycastAll),
            new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(int) });
        private static readonly MethodInfo[] Sites =
        {
            AccessTools.Method(typeof(AiRaycastNoAlloc), nameof(Site0)), AccessTools.Method(typeof(AiRaycastNoAlloc), nameof(Site1)),
            AccessTools.Method(typeof(AiRaycastNoAlloc), nameof(Site2)),
        };
        private static readonly MethodInfo KeepOrderM = AccessTools.Method(typeof(AiRaycastNoAlloc), nameof(KeepOrder));
        private static readonly MethodInfo AsArrayM = AccessTools.Method(typeof(AiRaycastNoAlloc), nameof(AsArray));

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(newAiScript), "Update");
            yield return AccessTools.Method(typeof(newAiScript), nameof(newAiScript.AvoidWalls));
            yield return AccessTools.Method(typeof(newAiScript), nameof(newAiScript.Attack));
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int site = original.Name == "Update" ? 0 : original.Name == "AvoidWalls" ? 1 : 2; // each method its own pool
            foreach (var ins in instructions)
            {
                if (ins.Calls(RaycastAll)) { ins.opcode = OpCodes.Call; ins.operand = Sites[site]; Patched++; }
                else if (ins.operand is MethodInfo m && m.DeclaringType == typeof(Enumerable) && m.IsGenericMethod)
                {
                    var args = m.GetGenericArguments();
                    if (m.Name == "OrderBy" && args.Length == 2 && args[0] == typeof(RaycastHit) && args[1] == typeof(float)) ins.operand = KeepOrderM;
                    else if (m.Name == "ToArray" && args.Length == 1 && args[0] == typeof(RaycastHit)) ins.operand = AsArrayM;
                }
                yield return ins;
            }
        }

        public static RaycastHit[] Site0(Vector3 o, Vector3 d, float range, int mask) => Query(0, o, d, range, mask);
        public static RaycastHit[] Site1(Vector3 o, Vector3 d, float range, int mask) => Query(1, o, d, range, mask);
        public static RaycastHit[] Site2(Vector3 o, Vector3 d, float range, int mask) => Query(2, o, d, range, mask);

        // The fast path returns an already sorted array; the pass-throughs below must then not sort again.
        private static bool _lastSorted;
        private static readonly RaycastHit[] Buffer = new RaycastHit[128];
        private static readonly Dictionary<int, RaycastHit[]>[] Pools = { new Dictionary<int, RaycastHit[]>(), new Dictionary<int, RaycastHit[]>(), new Dictionary<int, RaycastHit[]>() };

        private static RaycastHit[] Query(int site, Vector3 o, Vector3 d, float range, int mask)
        {
            if (!Enabled) { _lastSorted = false; return Physics.RaycastAll(o, d, range, mask); }
            Calls++;
            int n = Physics.RaycastNonAlloc(o, d, Buffer, range, mask);
            if (n >= Buffer.Length) { Fallbacks++; _lastSorted = false; return Physics.RaycastAll(o, d, range, mask); }
            var pool = Pools[site];
            if (!pool.TryGetValue(n, out var arr)) pool[n] = arr = new RaycastHit[n];
            System.Array.Copy(Buffer, arr, n);
            for (int i = 1; i < n; i++) // stable insertion sort by distance
            {
                var h = arr[i]; float key = h.distance; int j = i - 1;
                while (j >= 0 && arr[j].distance > key) { arr[j + 1] = arr[j]; j--; }
                arr[j + 1] = h;
            }
            if (Verify) Check(arr, o, d, range, mask);
            _lastSorted = true;
            return arr;
        }

        public static IEnumerable<RaycastHit> KeepOrder(IEnumerable<RaycastHit> src, System.Func<RaycastHit, float> key) =>
            _lastSorted ? src : src.OrderBy(key);

        public static RaycastHit[] AsArray(IEnumerable<RaycastHit> src)
        {
            if (_lastSorted) { _lastSorted = false; return (RaycastHit[])src; }
            return src.ToArray();
        }

        private static void Check(RaycastHit[] ours, Vector3 o, Vector3 d, float range, int mask)
        {
            VerifyChecks++;
            var orig = Physics.RaycastAll(o, d, range, mask).OrderBy(h => h.distance).ToArray();
            bool same = orig.Length == ours.Length;
            for (int i = 0; same && i < orig.Length; i++) same = orig[i].collider == ours[i].collider && orig[i].distance == ours[i].distance && orig[i].point == ours[i].point;
            if (!same) VerifyMismatches++;
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"patched\":" + Patched + ",\"calls\":" + Calls + ",\"fallbacks\":" + Fallbacks +
            ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches + "}";
    }
}
