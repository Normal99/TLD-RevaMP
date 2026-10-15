using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The IFA (Bus02, 5.8 t, four driven wheels) "doesn't drive well" (playtest 2026-10-15). Measured (tools/ifatow.py,
    /// the desktop alone, autodrive): its four wheels, all given the same torque, turn at very different speeds and swap
    /// from spinning to dragging and even backwards from one sample to the next (wheel rpm over what the speed gives:
    /// median 1.05, spread 0.68, down to −1.4), and the automatic gearbox hunts 4-5 on the averaged wheel speed. A
    /// Car01 at the same speeds: spread 0.10, never below 0.79.
    /// Each IFA wheel carries ~1,450 kg but weighs 30 kg (a Car01's: 306 kg on 21). A tyre's grip turns the wheel
    /// toward the road's speed; on a wheel that light for its load, one physics step turns it past that speed and the
    /// next one back (more wheel sub-steps, ConfigureVehicleSubsteps 5/12/15, did not change it: A/B 0.74 against 0.72).
    /// The game sets every wheel's mass every frame from the wheel part (wheelgraphicsscript.WheelSize).
    ///
    /// Here, after the game sets it, a wheel far lighter than MinLoadShare of what it carries (sprungMass; under Slack
    /// of that) gets that mass. A Car01's wheels (21 kg for 306) stay as they are.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.WheelInertia.Enabled / .MinLoadShare / .Slack (apply the next frame)
    [HarmonyPatch(typeof(wheelgraphicsscript), nameof(wheelgraphicsscript.WheelSize))]
    public static class WheelInertia
    {
        public static bool Enabled = true;
        public static float MinLoadShare = 1f / 15f;   // sweep (ifatow.py): 1/30 spread 0.13, 1/15 0.0004, 1/8 0.0004
        public static float Slack = 0.75f;
        public static long Raised;
        public static string Last = "";

        [HarmonyPostfix]
        private static void Postfix(wheelgraphicsscript __instance)
        {
            if (!Enabled) return;
            Raise(__instance, __instance.W);
            Raise(__instance, __instance.W2);
        }

        private static void Raise(wheelgraphicsscript g, WheelCollider w)
        {
            if (w == null || !w.enabled) return;
            float min = w.sprungMass * MinLoadShare;   // 0 until the physics engine has weighed the car
            if (w.mass >= min * Slack) return;
            if (Raised++ % 500 == 0) Last = g.transform.root.name + "/" + w.name + " " + w.mass.ToString("F0") + " → " + min.ToString("F0") + " kg";
            w.mass = min;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"raised\":" + Raised + ",\"last\":" + Json.Str(Last) + "}";
    }
}
