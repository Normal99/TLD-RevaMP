using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TLDRevamp.Fixes
{
    /// objGenScript.categoryClass.UpdPlaceRemove rebuilds its placement order every frame for every category:
    /// generatedChunks.Keys.ToList() then OrderBy(distance).ToList() — two new lists plus LINQ buffers, 7 categories per
    /// frame (measured ~720 KB/s of garbage standing still). placeOrder is only used inside this method.
    ///
    /// The game's own UpdPlaceRemove with its two ToList calls and its OrderBy swapped at runtime (GamePatch): the order
    /// is built in the category's existing placeOrder list (cleared, refilled in dictionary key order, then a stable
    /// insertion sort by the game's own key function = LINQ OrderBy's order). Its Instantiate and Destroy calls go
    /// through timers (Stats). Verify: also orders a copy with LINQ and compares element by element (Equals).
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.ObjPlaceInPlace.Enabled / .Verify
    [HarmonyPatch(typeof(objGenScript.categoryClass), nameof(objGenScript.categoryClass.UpdPlaceRemove))]
    public static class ObjPlaceInPlace
    {
        /// On by default since v0.23.0: exact (25,319 verified calls, 0 mismatches); driving A/B p99 15.20 → 14.85, garbage −5 KB/frame.
        public static bool Enabled = true;
        public static bool Verify = false;
        public static long Calls, VerifyChecks, VerifyMismatches, Instantiated, InstTicks, InstMaxTicks, Destroyed;
        public static int Patched;
        private static double[] _keys = new double[64];

        private static MethodInfo Our(string name) => AccessTools.Method(typeof(ObjPlaceInPlace), name);

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var toList = AccessTools.Method(typeof(Enumerable), nameof(Enumerable.ToList)).MakeGenericMethod(typeof(Vector3d));
            var orderBy = typeof(Enumerable).GetMethods().First(m => m.Name == nameof(Enumerable.OrderBy) && m.GetParameters().Length == 2)
                .MakeGenericMethod(typeof(Vector3d), typeof(double));
            var instantiate = typeof(Object).GetMethods().First(m => m.Name == nameof(Object.Instantiate) && m.IsGenericMethodDefinition &&
                m.GetParameters().Length == 4 && m.GetParameters()[3].ParameterType == typeof(Transform)).MakeGenericMethod(typeof(GameObject));
            var p = new GamePatch(nameof(ObjPlaceInPlace), instructions);
            p.Calls(toList, Our(nameof(ToList)), 2, withThis: true);
            p.Calls(orderBy, Our(nameof(OrderInPlace)), 1);
            p.Calls(instantiate, Our(nameof(Instantiate)), 1);
            p.Calls(AccessTools.Method(typeof(Object), nameof(Object.Destroy), new[] { typeof(Object) }), Our(nameof(Destroy)), 1);
            return p.Result(ref Patched);
        }

        [HarmonyPrefix]
        private static void Count() { if (Enabled) Calls++; }

        /// Both `.ToList()`: the keys into the category's own placeOrder (cleared), and the ordered placeOrder as it is.
        public static List<Vector3d> ToList(IEnumerable<Vector3d> source, objGenScript.categoryClass cat)
        {
            if (!Enabled) return source.ToList();
            var order = cat.placeOrder;
            if (order != null && ReferenceEquals(source, order)) return order;   // OrderInPlace's result
            if (order == null) order = new List<Vector3d>();
            order.Clear();
            if (source is Dictionary<Vector3d, objGenScript.chunkClass>.KeyCollection keys) foreach (var k in keys) order.Add(k);
            else order.AddRange(source);
            return order;
        }

        /// `.OrderBy(key)` on placeOrder: a stable insertion sort of the list itself by the game's own key.
        public static IEnumerable<Vector3d> OrderInPlace(IEnumerable<Vector3d> source, Func<Vector3d, double> key)
        {
            if (!Enabled || !(source is List<Vector3d> order)) return source.OrderBy(key);
            List<Vector3d> reference = Verify ? new List<Vector3d>(order).OrderBy(key).ToList() : null;
            int n = order.Count;
            if (_keys.Length < n) _keys = new double[n * 2];
            var keys = _keys;
            for (int i = 0; i < n; i++) keys[i] = key(order[i]);
            for (int i = 1; i < n; i++)
            {
                double k = keys[i]; var v = order[i]; int j = i - 1;
                while (j >= 0 && keys[j] > k) { keys[j + 1] = keys[j]; order[j + 1] = order[j]; j--; }
                keys[j + 1] = k; order[j + 1] = v;
            }
            if (reference != null)
            {
                VerifyChecks++;
                bool same = reference.Count == order.Count;
                for (int i = 0; same && i < order.Count; i++) same = order[i].Equals(reference[i]);
                if (!same) VerifyMismatches++;
            }
            return order;
        }

        public static GameObject Instantiate(GameObject prefab, Vector3 pos, Quaternion rot, Transform parent)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var go = Object.Instantiate(prefab, pos, rot, parent);
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            Instantiated++; InstTicks += dt; if (dt > InstMaxTicks) InstMaxTicks = dt;
            return go;
        }

        public static void Destroy(Object o) { Object.Destroy(o); Destroyed++; }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"patched\":" + Patched + ",\"calls\":" + Calls +
            ",\"instantiated\":" + Instantiated + ",\"instAvgMs\":" + (Instantiated > 0 ? (InstTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Instantiated).ToString("F3") : "0") +
            ",\"instMaxMs\":" + (InstMaxTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F2") + ",\"destroyed\":" + Destroyed + ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches + "}";
    }
}
