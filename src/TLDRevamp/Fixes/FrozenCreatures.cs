using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// movecreaturescript (the rabbits Fixes.Rabbits brings back: itemdatabase "Nyul") moves the body every frame in Move():
    ///     RB.velocity = Vector3.Lerp(...); RB.velocity += verticalVelocity;
    /// and Jump() (the AI's hop) adds to RB.velocity - neither with a kinematic test. A rabbit is a saved item, and the
    /// game's item streaming keeps it kinematic while the player is 200-300 m from it (itemPlaceRemoveScript.itemFreezeDist
    /// to itemRemoveDist: tosaveitemscript.DistFreezeRB). On a kinematic body the writes do nothing but log Unity's
    /// "Setting linear velocity of a kinematic body is not supported" - every frame, for every frozen rabbit, and each
    /// line also goes through the managed log handlers (BepInEx's log listener).
    /// Measured: rabbits spawned with the bridge, the player moved 250 m away: 13800-16500 lines per 20 s, all from
    /// movecreaturescript.Move (84%) and .Jump (16%) on Nyul(Clone). Same pattern as the frozen bats (Fixes.FrozenFlyers).
    ///
    /// While its body is kinematic, a creature's Move() and Jump() are skipped; Move's input is cleared as Move would have.
    /// The turn (Rot), the AI and the sounds are unchanged; when it is unfrozen it walks on from where it is.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.FrozenCreatures.Enabled
    public static class FrozenCreatures
    {
        public static bool Enabled = true;
        public static long Skipped;

        private static bool Frozen(movecreaturescript m)
        {
            if (!Enabled) return false;
            var rb = m.RB;
            return rb != null && rb.isKinematic;
        }

        [HarmonyPatch(typeof(movecreaturescript), nameof(movecreaturescript.Move))]
        private static class MovePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(movecreaturescript __instance)
            {
                if (!Frozen(__instance)) return true;
                __instance.moveDir = Vector3.zero;
                Skipped++;
                return false;
            }
        }

        [HarmonyPatch(typeof(movecreaturescript), nameof(movecreaturescript.Jump))]
        private static class JumpPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(movecreaturescript __instance)
            {
                if (!Frozen(__instance)) return true;
                Skipped++;
                return false;
            }
        }
    }
}
