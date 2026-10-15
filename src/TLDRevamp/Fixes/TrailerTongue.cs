using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The game's car trailer (item 53) "pulls its hitch down" (playtest 2026-10-15). When a trailer is hitched,
    /// utanfutoscript.RefreshCenterOffMass moves its centre of mass to CenterOfMassAttached: on this trailer 0.75 m ahead
    /// of the axle (standing alone 0.30 m), with the coupler 1.65 m ahead. Measured (tools/ifatow.py, behind a Car01):
    /// 924 N on the tow ball, 45% of the trailer's 203 kg; the car's rear wheels +870 N, its front −450 N (a fifth of
    /// what steers it). A trailer is towed with about a tenth of its weight on the ball. Hitching couldn't work at all in
    /// this beta before Fixes.TrailerGrip, so the hitched centre of mass was never in play.
    ///
    /// Here a hitched single-axle trailer keeps its centre of mass where at most MaxShare of its weight rests on the
    /// coupler (height and side as the game set them). A trailer with two axles apart (the IFA's, the car trailer example
    /// mod) carries its own weight and is left as it is; so is one whose hitched centre is already within the share.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.TrailerTongue.Enabled / .MaxShare (applies at the next hitch)
    [HarmonyPatch(typeof(utanfutoscript), nameof(utanfutoscript.RefreshCenterOffMass))]
    public static class TrailerTongue
    {
        public static bool Enabled = true;
        public static float MaxShare = 0.12f;
        public static float SingleAxleSpread = 0.6f;   // m: wheels this close along the trailer count as one axle
        public static long Moved;
        public static string Last = "";

        [HarmonyPostfix]
        private static void Postfix(utanfutoscript __instance, bool _attached)
        {
            if (!Enabled || !_attached || __instance.RB == null || __instance.wheels == null) return;
            var t = __instance.transform;
            var grip = __instance.GetComponent<attachgripscript>();
            if (grip == null || grip.GripPos == null) return;
            float zMin = float.MaxValue, zMax = float.MinValue, zSum = 0f; int n = 0;
            foreach (var w in __instance.wheels)
            {
                if (w == null) continue;
                float z = t.InverseTransformPoint(w.transform.TransformPoint(w.center)).z;
                zMin = Mathf.Min(zMin, z); zMax = Mathf.Max(zMax, z); zSum += z; n++;
            }
            if (n == 0 || zMax - zMin > SingleAxleSpread) return;
            float axle = zSum / n;
            float coupler = t.InverseTransformPoint(grip.GripPos.position).z;
            if (coupler - axle < 0.5f) return;
            var com = __instance.RB.centerOfMass;
            float share = (com.z - axle) / (coupler - axle);
            if (share <= MaxShare) return;
            float z0 = com.z;
            com.z = axle + MaxShare * (coupler - axle);
            __instance.RB.centerOfMass = com;
            Moved++;
            Last = __instance.name + ": coupler share " + share.ToString("F2") + " → " + MaxShare.ToString("F2") + " (centre z " + z0.ToString("F2") + " → " + com.z.ToString("F2") + ")";
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"moved\":" + Moved + ",\"last\":" + Json.Str(Last) + "}";
    }
}
