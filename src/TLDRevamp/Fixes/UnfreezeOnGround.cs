using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The game's item streaming (itemPlaceRemoveScript, once a second) places stored items within itemSpawnDist and
    /// unfreezes every item within itemUnFreezeDist (tosaveitemscript.DistUnFreezeRB: isKinematic = false) — with no
    /// check that anything is under it yet. Arriving faster than the world generates (a teleport, a join at another
    /// player, a slow machine), items are dropped before the building's floor or the terrain collider exists: they fall
    /// through and are gone (farworld run 4: 242 m; run 10: a building's items 245 m down, on the host arriving).
    ///
    /// An item about to be unfrozen first looks straight down (1 km, solid colliders only, not its own): nothing there →
    /// it stays frozen this second and is asked again the next. Already unfrozen items are not checked again.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.UnfreezeOnGround.Enabled
    [HarmonyPatch(typeof(tosaveitemscript), nameof(tosaveitemscript.DistUnFreezeRB))]
    public static class UnfreezeOnGround
    {
        public static bool Enabled = true;
        public static float RayM = 1000f;
        public static long Held, Released;
        private static readonly RaycastHit[] _hits = new RaycastHit[16];
        private static readonly System.Collections.Generic.HashSet<int> _held = new System.Collections.Generic.HashSet<int>();

        [HarmonyPrefix, HarmonyPriority(Priority.Low)]
        private static bool Prefix(tosaveitemscript __instance)
        {
            if (!Enabled || __instance.RB == null || !__instance.RB.isKinematic || __instance.buried) return true;
            var root = __instance.transform.root;
            var p = __instance.transform.position;
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 0.5f, Vector3.down, _hits, RayM, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _hits[i].collider;
                if (c == null || c.transform.root == root) continue;
                if (_held.Remove(__instance.GetInstanceID())) Released++;
                return true;
            }
            if (_held.Count > 4096) _held.Clear();   // ids of items long gone
            if (_held.Add(__instance.GetInstanceID()))
            {
                Held++;
                if (Held <= 10) Plugin.Log.LogWarning($"unfreeze held (nothing under it yet): {__instance.name} at global {mainscript.GlobalFromUnityPos(p).ToString()}");
            }
            return false;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"held\":" + Held + ",\"released\":" + Released + ",\"waiting\":" + _held.Count + "}";
    }
}
