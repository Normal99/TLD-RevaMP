using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Diagnostics
{
    /// Why does the player let go of what they hold? (fluids run 4: a can picked up through the bridge was gone from the
    /// hand right away.) Logs the first Max drops of the local player with their callers.
    [HarmonyPatch]
    public static class DropLog
    {
        public static bool Enabled = true;
        public static int Max = 20;
        public static long Drops;
        public static string Last = "";

        // the hand also empties without a Drop: stored into the inventory, or moved to the right hand
        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Drop), new[] { typeof(bool) })] [HarmonyPrefix] private static void D(fpscontroller __instance) => Log(__instance, "drop");
        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.InvStore))] [HarmonyPrefix] private static void S(fpscontroller __instance) => Log(__instance, "invstore");
        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.RightHandGrip))] [HarmonyPrefix] private static void R(fpscontroller __instance) => Log(__instance, "righthand");

        private static void Log(fpscontroller __instance, string kind)
        {
            if (!Enabled || __instance == null || __instance.pickedUp == null || mainscript.s == null || __instance != mainscript.s.player) return;
            Drops++;
            if (Drops > Max) return;
            var st = new System.Diagnostics.StackTrace(2, false);
            var calls = new System.Text.StringBuilder();
            for (int i = 0; i < st.FrameCount && i < 6; i++)
            {
                var m = st.GetFrame(i).GetMethod();
                if (m != null) calls.Append(m.DeclaringType?.Name).Append('.').Append(m.Name).Append(" < ");
            }
            Last = kind + " " + __instance.pickedUp.name + " via " + calls + " dist " + Vector3.Distance(__instance.transform.position, __instance.pickedUp.transform.position).ToString("F2") +
                   " from hold point " + (__instance.TpickedUpParent != null ? Vector3.Distance(__instance.TpickedUpParent.position, __instance.pickedUp.transform.position).ToString("F2") : "?") +
                   " (dropDist " + __instance.dropDist.ToString("F2") + ", body " + (__instance.pickedUp.RB != null ? "yes" : "NONE") + ")" + " t " + Time.time.ToString("F1");
            Plugin.Log.LogWarning("drop #" + Drops + ": " + Last);
        }
    }
}
