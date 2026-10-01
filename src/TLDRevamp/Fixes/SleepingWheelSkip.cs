using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// wheelgraphicsscript.Graphics runs every frame for every car wheel in range: WheelCollider.GetWorldPose plus writing
    /// the visual wheel's transform. Measured (car park, 16 parked cars, no profiler): ~3.8 ms/frame, ~60 µs per wheel.
    /// Parked cars never sleep (cause unknown), so a "sleeping body" condition never triggers.
    ///
    /// Resting-wheel skip (NOT bit-exact, bounded): once a car is effectively at rest (speed < RestSpeed, spin < RestSpin)
    /// and its steer angle hasn't changed, and one normal update happened at rest, further updates are skipped, with a
    /// full refresh every RefreshFrames frames. The visual wheel may lag the true pose by the micro-jitter of a resting car.
    /// Verify mode measures exactly that lag (max position mm / rotation degrees).
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.SleepingWheelSkip.Enabled / .Verify / .RestSpeed / .RestSpin /
    /// .RefreshFrames / .ExperimentSkipAll (experiment only: freeze all wheels)
    [HarmonyPatch(typeof(wheelgraphicsscript), nameof(wheelgraphicsscript.Graphics))]
    public static class SleepingWheelSkip
    {
        /// On by default since 2026-09-25: verify max visual error 1.9 mm, car park +25% FPS, user playtest (on/off toggled
        /// live around parked cars, steering, driving off): "seems the same, feels normal".
        public static bool Enabled = true;
        public static bool Verify = false;
        public static bool ExperimentSkipAll = false;
        public static float RestSpeed = 0.005f;   // m/s
        public static float RestSpin = 0.005f;    // rad/s
        public static int RefreshFrames = 30;

        public static long Calls, Skipped, VerifyChecks;
        public static float MaxPosErrorMm, MaxRotErrorDeg;

        private sealed class State { public bool AtRestUpdated; public float Steer, Steer2; public int SkippedInRow; }
        private static readonly ConditionalWeakTable<wheelgraphicsscript, State> States = new ConditionalWeakTable<wheelgraphicsscript, State>();

        [HarmonyPrefix]
        private static bool Prefix(wheelgraphicsscript __instance)
        {
            Calls++;
            if (ExperimentSkipAll) { Skipped++; return false; }
            if (!Enabled) return true;
            var w = __instance;
            // Mirror the original's early exit so we only reason about frames where it does work
            if (!mainscript.s.crsrLocked || w.W == null || !w.W.enabled) return true;
            var st = States.GetValue(w, _ => new State());
            var rb = w.W.attachedRigidbody;
            bool atRest = rb != null && Resting(rb) && (w.W2 == null || w.W2.attachedRigidbody == null || Resting(w.W2.attachedRigidbody));
            float steer = w.W.steerAngle, steer2 = w.W2 != null ? w.W2.steerAngle : 0f;
            bool steerSame = steer == st.Steer && steer2 == st.Steer2;
            st.Steer = steer;
            st.Steer2 = steer2;
            if (!atRest || !steerSame || !st.AtRestUpdated || st.SkippedInRow >= RefreshFrames)
            {
                st.AtRestUpdated = atRest; // a normal update at rest: the visual wheel now matches the resting pose
                st.SkippedInRow = 0;
                return true;
            }
            st.SkippedInRow++;
            Skipped++;
            if (Verify) Measure(w);
            return false;
        }

        private static bool Resting(Rigidbody rb) =>
            rb.velocity.sqrMagnitude < RestSpeed * RestSpeed && rb.angularVelocity.sqrMagnitude < RestSpin * RestSpin;

        /// How far the visual wheel is from where the original would have put it this frame.
        private static void Measure(wheelgraphicsscript w)
        {
            VerifyChecks++;
            if (w.T == null) return;
            w.W.GetWorldPose(out Vector3 v, out Quaternion q);
            if (w.W2 != null) { w.W2.GetWorldPose(out var p2, out _); v = (v + p2) * 0.5f; }
            float mm = (w.T.position - v).magnitude * 1000f;
            if (mm > MaxPosErrorMm) MaxPosErrorMm = mm;
            if (!w.keepUpright)
            {
                float deg = Quaternion.Angle(w.T.rotation, q);
                if (deg > MaxRotErrorDeg) MaxRotErrorDeg = deg;
            }
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"calls\":" + Calls + ",\"skipped\":" + Skipped +
            ",\"verifyChecks\":" + VerifyChecks + ",\"maxPosErrorMm\":" + MaxPosErrorMm.ToString("F3") +
            ",\"maxRotErrorDeg\":" + MaxRotErrorDeg.ToString("F3") + "}";
    }
}
