using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// objGenScript.categoryClass.UpdPlaceRemove rebuilds its placement order every frame for every category:
    /// generatedChunks.Keys.ToList() then OrderBy(distance).ToList() — two new lists plus LINQ buffers, 7 categories per
    /// frame (measured ~720 KB/s of garbage standing still). placeOrder is only used inside this method.
    ///
    /// Exact copy of UpdPlaceRemove where the order is built in the category's existing placeOrder list (cleared,
    /// refilled in dictionary key order, stable insertion sort by the same key = LINQ OrderBy's order). Everything else is
    /// the game's loop verbatim. Verify: also computes the LINQ order and compares element by element (Equals).
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.ObjPlaceInPlace.Enabled / .Verify
    [HarmonyPatch(typeof(objGenScript.categoryClass), nameof(objGenScript.categoryClass.UpdPlaceRemove))]
    public static class ObjPlaceInPlace
    {
        /// On by default since v0.23.0: exact (25,319 verified calls, 0 mismatches); driving A/B p99 15.20 → 14.85, garbage −5 KB/frame.
        public static bool Enabled = true;
        public static bool Verify = false;
        public static long Calls, VerifyChecks, VerifyMismatches, Instantiated, InstTicks, InstMaxTicks, Destroyed;

        private static readonly AccessTools.FieldRef<objGenScript.categoryClass, int> Placed = AccessTools.FieldRefAccess<objGenScript.categoryClass, int>("placed");
        private static readonly AccessTools.FieldRef<objGenScript.categoryClass, int> Removed = AccessTools.FieldRefAccess<objGenScript.categoryClass, int>("removed");
        private static readonly AccessTools.FieldRef<objGenScript.categoryClass, randomRock[]> RR = AccessTools.FieldRefAccess<objGenScript.categoryClass, randomRock[]>("rr");
        private static readonly AccessTools.FieldRef<objGenScript.categoryClass, soundplayscript[]> SP = AccessTools.FieldRefAccess<objGenScript.categoryClass, soundplayscript[]>("sp");
        private static readonly AccessTools.FieldRef<objGenScript.categoryClass, Vector3d> FirstGenAround = AccessTools.FieldRefAccess<objGenScript.categoryClass, Vector3d>("firstGenAround");
        private static double[] _keys = new double[64];

        [HarmonyPrefix]
        private static bool Prefix(objGenScript.categoryClass __instance, bool _moreFrame)
        {
            if (!Enabled) return true;
            var cat = __instance;
            var script = cat.script;
            Calls++;
            Placed(cat) = 0;
            Removed(cat) = 0;

            // placeOrder = generatedChunks.Keys.ToList(); placeOrder = placeOrder.OrderBy(x => (x - firstGenAround).sqrMagnitude).ToList();
            var order = cat.placeOrder ?? (cat.placeOrder = new List<Vector3d>());
            order.Clear();
            foreach (var k in cat.generatedChunks.Keys) order.Add(k);
            FirstGenAround(cat) = script.mapSetting.GetFirstGenAround();
            Vector3d fga = FirstGenAround(cat);
            int n = order.Count;
            if (_keys.Length < n) _keys = new double[n * 2];
            var keys = _keys;
            for (int i = 0; i < n; i++) keys[i] = (order[i] - fga).sqrMagnitude;
            for (int i = 1; i < n; i++)
            {
                double key = keys[i]; var v = order[i]; int j = i - 1;
                while (j >= 0 && keys[j] > key) { keys[j + 1] = keys[j]; order[j + 1] = order[j]; j--; }
                keys[j + 1] = key; order[j + 1] = v;
            }
            if (Verify)
            {
                VerifyChecks++;
                var reference = cat.generatedChunks.Keys.ToList().OrderBy(x => (x - fga).sqrMagnitude).ToList();
                bool same = reference.Count == order.Count;
                for (int i = 0; same && i < order.Count; i++) same = order[i].Equals(reference[i]);
                if (!same) VerifyMismatches++;
            }

            for (int num = 0; num < order.Count; num++)
            {
                var chunk = cat.generatedChunks[order[num]];
                chunk.objsToRemove.Clear();
                foreach (KeyValuePair<Vector3d, objGenScript.objClass> obj in chunk.objs)
                {
                    if (obj.Value.place)
                    {
                        if (cat.saveDestroy && obj.Value.placed && obj.Value.obj == null)
                        {
                            if (!cat.destroyed.ContainsKey(obj.Value.chunkID)) cat.destroyed.Add(obj.Value.chunkID, new List<Vector3d>());
                            cat.destroyed[obj.Value.chunkID].Add(obj.Key);
                            chunk.objsToRemove.Add(obj.Key);
                        }
                        else if (obj.Value.obj == null && (!_moreFrame || Placed(cat) < script.placePerFrame))
                        {
                            long it0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            obj.Value.obj = Object.Instantiate(cat.prefabs[obj.Value.prefabIndex].prefab, mainscript.UnityPosFromGlobal(obj.Value.gpos), Quaternion.Euler(obj.Value.rot), script.parent);
                            RR(cat) = obj.Value.obj.GetComponentsInChildren<randomRock>();
                            var rr = RR(cat);
                            for (int i = 0; i < rr.Length; i++) rr[i].FStart(obj.Value.seed + i);
                            SP(cat) = obj.Value.obj.GetComponentsInChildren<soundplayscript>();
                            var sp = SP(cat);
                            for (int i = 0; i < sp.Length; i++) sp[i].FStart(cat.biomes[obj.Value.chunkID]);
                            obj.Value.obj.transform.localScale *= obj.Value.scale;
                            obj.Value.placed = true;
                            Placed(cat)++;
                            long idt = System.Diagnostics.Stopwatch.GetTimestamp() - it0;
                            Instantiated++; InstTicks += idt; if (idt > InstMaxTicks) InstMaxTicks = idt;
                        }
                    }
                    else if (obj.Value.obj != null && (!_moreFrame || Removed(cat) < script.removePerFrame))
                    {
                        obj.Value.placed = false;
                        Object.Destroy(obj.Value.obj);
                        Removed(cat)++;
                        Destroyed++;
                    }
                    if (_moreFrame && Placed(cat) >= script.placePerFrame && Removed(cat) >= script.removePerFrame) break;
                }
                for (int i = 0; i < chunk.objsToRemove.Count; i++) chunk.objs.Remove(chunk.objsToRemove[i]);
            }
            return false;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"calls\":" + Calls +
            ",\"instantiated\":" + Instantiated + ",\"instAvgMs\":" + (Instantiated > 0 ? (InstTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Instantiated).ToString("F3") : "0") +
            ",\"instMaxMs\":" + (InstMaxTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F2") + ",\"destroyed\":" + Destroyed + ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches + "}";
    }
}
