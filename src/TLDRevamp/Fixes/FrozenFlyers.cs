using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// randomFlyer (the bats tempRandomFlyerSpawnScript puts at some POIs: RandomBat) steers every frame in Upd():
    ///     rb.velocity = Vector3.Lerp(rb.velocity, dir, Time.deltaTime * velocityLerp);
    /// with no kinematic test. A bat is a saved item, and the game's item streaming keeps it kinematic until the player is
    /// within itemUnFreezeDist (UnfreezeOnGround can hold it longer); in a session every copy is kinematic too. On a
    /// kinematic body the write does nothing but log Unity's "Setting linear velocity of a kinematic body is not
    /// supported" - every frame, for every frozen bat. Measured: ~160-1850 lines a second while playing, 1.6 million
    /// (99.8% of Player.log) in a two-hour session; also without TLD Revamp loaded (108 in 15 s next to one bat).
    ///
    /// While its body is kinematic, a bat's Upd() (the steer and the turn toward its own target) is skipped. Picking
    /// targets and its sounds (Update) are unchanged; when it is unfrozen it flies on from where it is.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.FrozenFlyers.Enabled
    [HarmonyPatch(typeof(randomFlyer), "Upd")]
    public static class FrozenFlyers
    {
        public static bool Enabled = true;
        public static long Skipped;
        private static readonly AccessTools.FieldRef<randomFlyer, Rigidbody> Rb = AccessTools.FieldRefAccess<randomFlyer, Rigidbody>("rb");

        [HarmonyPrefix]
        private static bool Prefix(randomFlyer __instance)
        {
            if (!Enabled) return true;
            var rb = Rb(__instance);
            if (rb == null || !rb.isKinematic) return true;
            Skipped++;
            return false;
        }
    }
}
