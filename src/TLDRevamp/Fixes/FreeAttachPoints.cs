using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// An item attached without parenting (attachablescript, parentAtAttach false) is put on its attach point by the
    /// game every frame (attachablescript.Update → SetOwnPosition(point.position)). Two kinds of point hang from a ROOT
    /// object the game creates without a visszarako, so the floating-origin shift never moves them and after every
    /// shift the item is put back at the old coordinates (2–4.5 km off per normal shift, the whole jump after a
    /// teleport):
    ///  - "attach at start in place" items (attachablescript.FStart: a bare "<item>_AttachAtStartInPlace" object holds
    ///    the point) — the spawn house's air fresheners (Wunderbaum, Doggyandi, …) and its Kolbasz, in every new game
    ///    (tools/attachprobe.py: single player, both machines, a 200 km teleport left all six at home)
    ///  - an item loaded attached to a POI with no collider index (save_attachable.Load, attach type 2): the point
    ///    itself is a root (NewPoint with no parent)
    ///
    /// Found by tools/tpprobe.py (v0.57.97): the far player's copies of items attached at the spawn building (Attachable01–06,
    /// a Kolbasz) were held at the old coordinates while the copy code moved their kinematic bodies to the new ones —
    /// 201 km per physics step, which flung the player 200 km (a fall-damage death) and the siphon hoses nearby.
    /// Here those two roots get the game's own visszarako. Any other root without one is only logged (it may be moved
    /// by something else — never moved twice).
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.FreeAttachPoints.Enabled
    [HarmonyPatch(typeof(attachablescript), nameof(attachablescript.NewPoint), new[] { typeof(Transform), typeof(Vector3), typeof(Quaternion) })]
    public static class FreeAttachPoints
    {
        public static bool Enabled = true;
        public static long Given, OtherRoots;

        [HarmonyPostfix]
        private static void Postfix(Transform __result)
        {
            if (!Enabled || __result == null) return;
            var root = __result.root;
            if (root.GetComponent<visszarako>() != null) return;
            if (root == __result || root.name.EndsWith("_AttachAtStartInPlace", System.StringComparison.Ordinal))
            {
                root.gameObject.AddComponent<visszarako>().FStart(_dontlookUnderMap: true, _importantUnderMapLook: false, _moveWhenParented: false);
                Given++;
            }
            else if (OtherRoots++ < 20) Plugin.Log.LogWarning($"attach point under a root the shift may not move: {root.name} (point {__result.name})");
        }
    }
}
