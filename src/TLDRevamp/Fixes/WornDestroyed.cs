using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Something worn (a hat, glasses) destroyed while on the player: the game takes its renderers out of the camera's
    /// hide list (fpscontroller.wearedRenderer, a makeinvisible) only in TakeOff — a destroyed one stayed, and
    /// makeinvisible.PreRender/PostRender threw a NullReferenceException on it twice every frame until the game was
    /// restarted (playtest 2026-10-09/10, client 1: 16,840 pairs in six minutes). A held item has its own destroy
    /// handling (pickupable.Drop2); a worn one now has too: off the player as TakeOff would, logged with its name. And the
    /// hide list drops a renderer that is gone before it is used, whatever destroyed it.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.WornDestroyed.Enabled
    [HarmonyPatch]
    public static class WornDestroyed
    {
        public static bool Enabled = true;
        public static long WornLost, RenderersDropped;

        [HarmonyPatch(typeof(pickupable), "OnDestroy")]
        [HarmonyPrefix]
        private static void Destroyed(pickupable __instance)
        {
            if (!Enabled || __instance == null || !__instance.gameObject.scene.isLoaded || __instance.wear == null) return;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || pl.weared == null || pl.wearedRenderer == null) return;
            int i = (int)__instance.wear.tipus;
            if (i < 0 || i >= pl.weared.Length || pl.weared[i] != __instance) return;
            if (__instance.wear.Renderers != null)
                foreach (var r in __instance.wear.Renderers) pl.wearedRenderer.R.Remove(r);
            pl.weared[i] = null;
            WornLost++;
            if (WornLost <= 20) Plugin.Log.LogWarning($"worn item destroyed on the player: {__instance.name} ({__instance.wear.tipus}) — taken off\n{System.Environment.StackTrace}");
        }

        [HarmonyPatch(typeof(makeinvisible), nameof(makeinvisible.PreRender))]
        [HarmonyPatch(typeof(makeinvisible), nameof(makeinvisible.PostRender))]
        [HarmonyPrefix]
        private static void DropGone(makeinvisible __instance)
        {
            if (!Enabled || __instance.R == null) return;
            for (int k = 0; k < __instance.R.Count; k++)
                if (__instance.R[k] == null) { RenderersDropped += __instance.R.RemoveAll(r => r == null); return; }
        }

        /// Test (bridge `guardlab worn`): the nearest wearable within 10 m put on and destroyed while worn (what the
        /// playtest's hat met); the frames after it show in `errors` (makeinvisible's NullReferenceException or none).
        internal static string Test()
        {
            var pl = mainscript.s.player;
            pickupable w = null; float best = 100f;
            foreach (var p in Object.FindObjectsOfType<pickupable>())
            {
                if (p.wear == null || p.wear.wearing) continue;
                int i = (int)p.wear.tipus;
                if (i < 0 || i >= pl.weared.Length || pl.weared[i] != null) continue;
                float d = (p.transform.position - pl.transform.position).sqrMagnitude;
                if (d < best) { best = d; w = p; }
            }
            if (w == null) return "{\"error\":\"no wearable within 10 m with a free slot\"}";
            string name = w.name; int n0 = pl.wearedRenderer.R.Count;
            pl.Wear(w);
            int n1 = pl.wearedRenderer.R.Count;
            Object.Destroy(w.gameObject);
            return "{\"worn\":" + Json.Str(name) + ",\"tipus\":" + Json.Str(w.wear.tipus.ToString()) + ",\"enabled\":" + (Enabled ? "true" : "false") +
                   ",\"hideListBefore\":" + n0 + ",\"hideListWorn\":" + n1 + "}";
        }

        internal static string Status()
        {
            var pl = mainscript.s.player; int gone = 0;
            foreach (var r in pl.wearedRenderer.R) if (r == null) gone++;
            int slots = 0; foreach (var p in pl.weared) if ((object)p != null && p == null) slots++;
            return "{\"hideList\":" + pl.wearedRenderer.R.Count + ",\"hideListGone\":" + gone + ",\"slotsHoldingDestroyed\":" + slots +
                   ",\"wornLost\":" + WornLost + ",\"renderersDropped\":" + RenderersDropped + "}";
        }
    }
}
