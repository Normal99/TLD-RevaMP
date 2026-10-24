using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Our player seated in another player's vehicle sees it from its DRAWN pose: the seat's camera rides the copy, drawn
    /// interpolated, up to a physics step behind its colliders (BodyMotionTick; BusBack's seats ride BusBack, MoveLimbs).
    /// The game's use, pick and push rays (fpscontroller.RayStuff, from Cam) hit those colliders, a step ahead at speed: the
    /// radio's tuning dial was missed (dialjitter at 60 km/h: the look ray hit it in 9 of 166 frames; in the owner's own
    /// car, which is drawn at its physics pose, 230 of 230). While RayStuff runs, the camera is put where it is in the
    /// vehicle's physics frame, where the owner's camera is, and back afterwards. Everything RayStuff does with the camera
    /// (rays, forces, push points) acts on physics bodies, so all of it is in that frame.
    public static partial class Entities
    {
        public static bool SeatAimAtPhysics = true;   // A/B: false = the rays from the drawn camera (before v0.69.2)
        public static long SeatAimFrames;

        /// The body our seat rides, when it is drawn interpolated (one of our copies: the game interpolates no body,
        /// mainscript.RBI()), so that its drawn pose and its physics pose differ.
        internal static bool SeatAimFrame(out Transform drawn, out Rigidbody body)
        {
            drawn = null; body = null;
            if (!SeatAimAtPhysics || !InSession) return false;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !pl.Bsitting || pl.seat == null) return false;
            body = pl.seat.GetComponentInParent<Rigidbody>();
            if (body == null || !body.isKinematic || body.interpolation != RigidbodyInterpolation.Interpolate) return false;
            drawn = body.transform;
            return true;
        }

        /// A point seen in the body's drawn frame, in its physics frame.
        internal static Vector3 SeatAimPoint(Transform drawn, Rigidbody body, Vector3 p) =>
            body.position + body.rotation * Quaternion.Inverse(drawn.rotation) * (p - drawn.position);

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.RayStuff))]
        private static class SeatAimRays
        {
            [HarmonyPrefix]
            private static void Pre(fpscontroller __instance, out Pose? __state)
            {
                __state = null;
                if (__instance != mainscript.s?.player || __instance.Cam == null || !SeatAimFrame(out var drawn, out var body)) return;
                var t = __instance.Cam.transform;
                __state = new Pose(t.localPosition, t.localRotation);
                t.SetPositionAndRotation(SeatAimPoint(drawn, body, t.position), body.rotation * Quaternion.Inverse(drawn.rotation) * t.rotation);
                SeatAimFrames++;
            }

            [HarmonyFinalizer]
            private static void After(fpscontroller __instance, Pose? __state)
            {
                if (__state == null || __instance.Cam == null) return;
                var t = __instance.Cam.transform;
                t.localPosition = __state.Value.position; t.localRotation = __state.Value.rotation;
            }
        }
    }
}
