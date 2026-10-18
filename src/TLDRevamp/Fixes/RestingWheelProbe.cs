using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// talajnezo.FixedUpdate (one per wheel, every physics step): a ground raycast plus its own wheel effects only: dust
    /// and grass particles (emission rate from the car's speed), the rolling, spring and skid sounds. Nothing else reads
    /// its results (checked: no other script uses talajnezo). Measured in the car park (16 parked cars): 36 calls a frame,
    /// 0.30-0.42 ms/frame, the costliest FixedUpdate script.
    ///
    /// For a car at rest those effects are idle: speed 0 means no particles and the rolling/skid sounds stopped, the spring
    /// sound's volume follows the suspension movement (~0); skipping starts only once it is below 0.001. Resting-wheel skip (bounded, like SleepingWheelSkip): when the
    /// car is parked (nobody driving, ignition off) and its body moves slower than RestSpeed / RestSpin, the wheel runs one
    /// normal step at rest and then every RefreshSteps-th step; any movement, a driver or the ignition makes it run every
    /// step again at once. What can lag by up to RefreshSteps steps (0.5 s at 50 Hz) on a resting car: a smoke/grass
    /// setting change.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.RestingWheelProbe.Enabled / .RestSpeed / .RestSpin / .RefreshSteps
    [HarmonyPatch(typeof(talajnezo), "FixedUpdate")]
    public static class RestingWheelProbe
    {
        public static bool Enabled = true;
        public static float RestSpeed = 0.005f;   // m/s, as SleepingWheelSkip
        public static float RestSpin = 0.005f;    // rad/s
        public static int RefreshSteps = 25;
        public static long Calls, Skipped;

        private sealed class State { public int AtRest; }
        private static readonly ConditionalWeakTable<talajnezo, State> States = new ConditionalWeakTable<talajnezo, State>();

        [HarmonyPrefix]
        private static bool Prefix(talajnezo __instance)
        {
            if (!Enabled) return true;
            Calls++;
            var t = __instance;
            var car = t.Car;
            var rb = t.RB;
            if (car == null || rb == null || rb.isKinematic) return true; // only car wheels; a kinematic body (multiplayer copy) moves without velocity
            var s = States.GetValue(t, _ => new State());
            bool parked = !car.driving && !car.driving2 && !car.isPlayerDriving && !car.ignition && !car.multiControlling;
            bool resting = rb.IsSleeping() || (rb.velocity.sqrMagnitude < RestSpeed * RestSpeed && rb.angularVelocity.sqrMagnitude < RestSpin * RestSpin);
            // the spring sound fades by one lerp step per call: keep running until it is silent (else it would linger)
            bool silent = t.SpringSource == null || !t.SpringSource.isPlaying || t.SpringSource.volume < 0.001f;
            if (!parked || !resting || !silent) { s.AtRest = 0; return true; }
            // the first step at rest runs normally (sounds stop, emission settles), then one in RefreshSteps
            if (s.AtRest++ == 0 || s.AtRest % RefreshSteps == 0) return true;
            Skipped++;
            return false;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"calls\":" + Calls + ",\"skipped\":" + Skipped + "}";
    }
}
