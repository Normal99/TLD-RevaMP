using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// fpscontroller.RayStuff (what you look at: slots to attach/detach, seats, usables), every frame:
    ///   attachHits = Physics.RaycastAll(cam, forward, FrayRange, attachLayer);
    ///   attachHits = attachHits.OrderBy(x => x.distance).ToArray();
    /// a new hit array, then LINQ's sorting buffers and a second array, every frame (42 KB/s while driving).
    ///
    /// Transpiler on those two lines only: the ray goes into a reused buffer (RaycastNonAlloc: the same query, the same
    /// hits) copied into a reused array of exactly that length, and OrderBy+ToArray become a stable insertion sort by
    /// distance in place (OrderBy is stable too: equal distances keep the query's order). attachHits is private and read
    /// only in the lines right after, within the same call. Exact.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.LookRayNoAlloc.Enabled (the original calls when off) / .Verify
    /// (also runs the original two lines and compares hit by hit).
    [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.RayStuff))]
    public static class LookRayNoAlloc
    {
        public static bool Enabled = true;
        public static bool Verify = false;
        public static long Calls, VerifyChecks, VerifyMismatches, Patched;

        private static readonly RaycastHit[] Buffer = new RaycastHit[256];
        private static readonly Dictionary<int, RaycastHit[]> ByLength = new Dictionary<int, RaycastHit[]>();
        private static Vector3 _o, _d; private static float _range; private static int _mask;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var raycastAll = AccessTools.Method(typeof(Physics), nameof(Physics.RaycastAll), new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(int) });
            var orderBy = typeof(Enumerable).GetMethods().First(m => m.Name == nameof(Enumerable.OrderBy) && m.GetParameters().Length == 2)
                .MakeGenericMethod(typeof(RaycastHit), typeof(float));
            var toArray = AccessTools.Method(typeof(Enumerable), nameof(Enumerable.ToArray)).MakeGenericMethod(typeof(RaycastHit));
            int done = 0;
            foreach (var ci in code)
            {
                if (ci.Calls(raycastAll)) { ci.opcode = OpCodes.Call; ci.operand = AccessTools.Method(typeof(LookRayNoAlloc), nameof(Cast)); done++; }
                else if (ci.Calls(orderBy)) { ci.opcode = OpCodes.Call; ci.operand = AccessTools.Method(typeof(LookRayNoAlloc), nameof(Sort)); done++; }
                else if (ci.Calls(toArray) && done == 2) { ci.opcode = OpCodes.Call; ci.operand = AccessTools.Method(typeof(LookRayNoAlloc), nameof(AsArray)); done++; }
                yield return ci;
            }
            Patched = done;
            if (done != 3) Plugin.Log.LogWarning("LookRayNoAlloc: patched " + done + " of 3 calls in fpscontroller.RayStuff");
        }

        private static RaycastHit[] Cast(Vector3 origin, Vector3 direction, float maxDistance, int layerMask)
        {
            if (!Enabled) return Physics.RaycastAll(origin, direction, maxDistance, layerMask);
            Calls++;
            _o = origin; _d = direction; _range = maxDistance; _mask = layerMask;
            int n = Physics.RaycastNonAlloc(origin, direction, Buffer, maxDistance, layerMask);
            if (n == Buffer.Length) return Physics.RaycastAll(origin, direction, maxDistance, layerMask);   // full: as the game
            if (!ByLength.TryGetValue(n, out var arr)) ByLength[n] = arr = new RaycastHit[n];
            System.Array.Copy(Buffer, arr, n);
            return arr;
        }

        private static IEnumerable<RaycastHit> Sort(IEnumerable<RaycastHit> source, System.Func<RaycastHit, float> key)
        {
            if (!Enabled || !(source is RaycastHit[] a)) return source.OrderBy(key).ToArray();
            for (int i = 1; i < a.Length; i++)
            {
                var h = a[i]; float d = h.distance; int j = i - 1;
                while (j >= 0 && a[j].distance > d) { a[j + 1] = a[j]; j--; }
                a[j + 1] = h;
            }
            if (Verify) Check(a);
            return a;
        }

        private static RaycastHit[] AsArray(IEnumerable<RaycastHit> source) => source as RaycastHit[] ?? source.ToArray();

        private static void Check(RaycastHit[] mine)
        {
            var orig = Physics.RaycastAll(_o, _d, _range, _mask).OrderBy(x => x.distance).ToArray();
            VerifyChecks++;
            bool same = orig.Length == mine.Length;
            for (int i = 0; same && i < orig.Length; i++) same = orig[i].collider == mine[i].collider && orig[i].distance == mine[i].distance;
            if (same) return;
            if (++VerifyMismatches <= 5) Plugin.Log.LogWarning($"LookRayNoAlloc: {mine.Length} hits vs original {orig.Length}");
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"patched\":" + Patched + ",\"calls\":" + Calls + ",\"lengths\":" + ByLength.Count +
            ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches + "}";
    }
}
