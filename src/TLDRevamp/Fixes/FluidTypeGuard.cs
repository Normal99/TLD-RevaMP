using System.Collections.Generic;
using HarmonyLib;

namespace TLDRevamp.Fixes
{
    /// A tank holding a fluid type the game has no entry for: mainscript.fluidcontainer.GetColor and
    /// tankcapscript.GetCapString index mainscript.fluids by the type and threw IndexOutOfRangeException every frame the
    /// tank was drawn or looked at (playtest 2026-10-09/10, run B: host 2028, client 1 1537, client 2 470 — on every
    /// machine, from the arrival at one site on: most likely a record that reached everyone). Where the type comes from is
    /// not known yet; every tank's contents come in through save_tankscript.Load (a save, a share, a resync): there it is
    /// checked, an unknown type is dropped and logged with the item and the call stack. The two readers drop one that got
    /// in some other way instead of throwing.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.FluidTypeGuard.Enabled
    [HarmonyPatch]
    public static class FluidTypeGuard
    {
        public static bool Enabled = true;
        public static long Dropped;

        private static int Drop(List<mainscript.fluid> fluids)
        {
            var table = mainscript.s != null ? mainscript.s.fluids : null;
            if (fluids == null || table == null) return 0;
            int n = 0;
            for (int i = fluids.Count - 1; i >= 0; i--)
            {
                var f = fluids[i];
                if (f != null && (int)f.type >= 0 && (int)f.type < table.Length) continue;
                if (n == 0 && Dropped < 20) Plugin.Log.LogWarning("fluid type " + (f != null ? ((int)f.type).ToString() : "null") + " (" + (f != null ? f.amount.ToString("F2") : "-") + " l) — the game has " + table.Length + ": dropped");
                fluids.RemoveAt(i); n++;
            }
            Dropped += n;
            return n;
        }

        /// Before the record is loaded: Load ends in tankscript.Upd, which reads the colour already — a check after it
        /// would only see what the reader guard left.
        [HarmonyPatch(typeof(save_tankscript), nameof(save_tankscript.Load), new[] { typeof(tankscript) })]
        [HarmonyPrefix]
        private static void Loading(save_tankscript __instance, tankscript _script)
        {
            var table = mainscript.s != null ? mainscript.s.fluids : null;
            if (!Enabled || table == null || __instance.type == null || __instance.amount == null) return;
            int n = System.Math.Min(__instance.type.Length, __instance.amount.Length), bad = 0;
            for (int i = 0; i < n; i++) if (__instance.type[i] < 0 || __instance.type[i] >= table.Length) bad++;
            if (bad == 0) return;
            var t = new int[n - bad]; var a = new float[n - bad];
            for (int i = 0, j = 0; i < n; i++)
            {
                if (__instance.type[i] < 0 || __instance.type[i] >= table.Length)
                {
                    if (Dropped < 20) Plugin.Log.LogWarning($"fluid type {__instance.type[i]} ({__instance.amount[i]:F2} l) in a tank record — the game has {table.Length}: dropped");
                    Dropped++; continue;
                }
                t[j] = __instance.type[i]; a[j] = __instance.amount[i]; j++;
            }
            __instance.type = t; __instance.amount = a;
            if (Dropped <= 20)
                Plugin.Log.LogWarning($"  in {(_script != null ? _script.transform.root.name : "?")} (record id {__instance.id}, tank {__instance.ind}), loaded via\n{System.Environment.StackTrace}");
        }

        [HarmonyPatch(typeof(mainscript.fluidcontainer), nameof(mainscript.fluidcontainer.GetColor))]
        [HarmonyPatch(typeof(mainscript.fluidcontainer), nameof(mainscript.fluidcontainer.GetBurnColor))]
        [HarmonyPrefix]
        private static void Reading(mainscript.fluidcontainer __instance)
        {
            if (Enabled) Drop(__instance.fluids);
        }

        [HarmonyPatch(typeof(tankcapscript), nameof(tankcapscript.GetCapString))]
        [HarmonyPrefix]
        private static void Cap(tankcapscript __instance)
        {
            if (Enabled && __instance.Tank != null && __instance.Tank.F != null && Drop(__instance.Tank.F.fluids) > 0 && Dropped <= 20)
                Plugin.Log.LogWarning("  in the tank of " + __instance.transform.root.name);
        }

        /// Test (bridge `guardlab fluid`): the nearest tank within 10 m gets a record with an unknown type loaded, then an
        /// unknown type straight into its contents and its colour read — what threw, what was dropped.
        internal static string Test()
        {
            var pl = mainscript.s.player.transform.position;
            tankscript tank = null; float best = 100f;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<tankscript>())
            {
                float d = (t.transform.position - pl).sqrMagnitude;
                if (d < best && t.F != null && t.transform.root != mainscript.s.player.transform.root) { best = d; tank = t; }   // not the player's own
            }
            if (tank == null) return "{\"error\":\"no tank within 10 m\"}";
            long d0 = Dropped; string loadEx = "", readEx = "";
            var rec = new save_tankscript(tank, 0, 0);
            rec.type = new List<int>(rec.type ?? new int[0]) { 99 }.ToArray();
            rec.amount = new List<float>(rec.amount ?? new float[0]) { 1f }.ToArray();
            try { rec.Load(tank); } catch (System.Exception ex) { loadEx = ex.GetType().Name; }
            long d1 = Dropped;
            tank.F.fluids.Add(new mainscript.fluid((mainscript.fluidenum)99, 1f));
            try { tank.F.GetColor(); } catch (System.Exception ex) { readEx = ex.GetType().Name; }
            int left = 0; foreach (var f in tank.F.fluids) if ((int)f.type >= mainscript.s.fluids.Length) left++;
            tank.F.fluids.RemoveAll(f => (int)f.type >= mainscript.s.fluids.Length);   // the test's own leftovers
            return "{\"tank\":" + Json.Str(tank.transform.root.name) + ",\"enabled\":" + (Enabled ? "true" : "false") +
                   ",\"loadException\":" + Json.Str(loadEx) + ",\"droppedOnLoad\":" + (d1 - d0) +
                   ",\"readException\":" + Json.Str(readEx) + ",\"droppedOnRead\":" + (Dropped - d1) + ",\"unknownLeft\":" + left + "}";
        }
    }
}
