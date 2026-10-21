using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.VR.Native
{
    /// Your height in VR, and the body others see.
    ///
    /// The game places the headset once: its recenter (at the main menu, and on B) moves the camera rig so the camera
    /// sits on the head-lean transform, and from then on the camera is wherever your real head is relative to that
    /// moment. The body others see takes its head from the camera (fpscontroller.Update: Th = the camera, the head IK
    /// target = Th), but the body itself stays standing. So when the recenter happened with your head higher than you
    /// play (the headset still on the desk, or standing up from the PC), or you bend down, the network gets a head far
    /// below a standing body, and the model folds into a short player. The camera rig is also a child of the head-lean
    /// transform, so the game's own crouch (the left stick click) lowers your view on top of your real head.
    ///
    /// Here, while the headset is on and you stand:
    ///   - your standing height is learned: each recenter takes it, and it rises on its own when your head stays
    ///     above it (the headset was put on after the recenter), so standing up straight is always full height;
    ///   - the camera height is your real head relative to that standing height, on top of the body's standing eye
    ///     height. The game's crouch and lean no longer move it up and down;
    ///   - the body crouches as far as your head is below your standing height (the game's own crouch, with its
    ///     ceiling check), so others see a player crouching where you crouch, and you fit where you crouch to;
    ///   - the stick-click crouch still crouches the body, as it does in the game.
    /// Seated in a vehicle, the game's own placement stays as it is.
    internal static class VrHeight
    {
        /// Settings → Revamp → VR.
        internal static bool Enabled = true;

        internal const float DeadZone = 0.06f;      // a slight bend or a nod does not crouch the body
        internal const float RiseAfterS = 1.0f;     // a head this long above the standing height raises it
        internal const float RiseMargin = 0.04f;

        internal static float Standing = float.NaN; // your standing eye height in tracking space (m)
        internal static float HeadDuck = 1f;
        internal static int Recenters, Rises;
        internal static bool Applied;
        private static float _aboveSince = -1f;

        private static bool Live(fpscontroller pl) =>
            Enabled && VrNative.Headset && pl != null && pl.mainCam != null && pl.CamParent != null && pl.THeadLean != null &&
            pl.TdefStandPos != null && pl.TProneHeadPos != null && !pl.Bsitting && !pl.died && !pl.otherView() &&
            pl.CamParent.parent == pl.THeadLean;

        /// The real head height in the camera rig (tracking space, the runtime's floor or the recenter's origin).
        private static bool Head(fpscontroller pl, out float y)
        {
            var local = pl.CamParent.InverseTransformPoint(pl.mainCam.transform.position);
            y = local.y;
            return local.sqrMagnitude > 1e-6f && !float.IsNaN(y) && Mathf.Abs(y) < 5f;   // a pose of zero: no tracking yet
        }

        /// The game recentered: your head now is your standing height.
        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.VRRecenterTo))]
        private static class Recenter
        {
            private static void Postfix(fpscontroller __instance)
            {
                if (!Enabled || !VrNative.Headset || __instance == null || __instance.CamParent == null || __instance.mainCam == null) return;
                if (Head(__instance, out float y)) { Standing = y; Recenters++; _aboveSince = -1f; }
            }
        }

        /// Before the game's Update reads the crouch: the body crouches as far as your head is down.
        [HarmonyPatch(typeof(fpscontroller), "Update")]
        private static class Crouch
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(fpscontroller __instance)
            {
                var pl = __instance;
                if (!Live(pl) || float.IsNaN(Standing) || !Head(pl, out float y)) { HeadDuck = 1f; return; }
                float range = pl.TdefStandPos.position.y - pl.TProneHeadPos.position.y;
                if (range < 0.2f) return;
                float drop = Mathf.Max(0f, Standing - y - DeadZone);
                HeadDuck = Mathf.Clamp01(1f - drop / range);
                float target = HeadDuck;
                if (pl.input != null && pl.input.vrDuckToggle) target = Mathf.Min(target, pl.ctrlDuckValue);
                if (target < pl.duckValue) pl.duckValue = target;
                else if (target > pl.duckValue) pl.duckValue = Mathf.Clamp01(pl.duckValue + pl.CanStandUp(Mathf.Min(target - pl.duckValue, Time.deltaTime * 4f)));
            }
        }

        /// After the game moved the head-lean transform: the camera height comes from your head alone.
        internal static void LateTick()
        {
            Applied = false;
            var pl = VrNative.Player;
            if (!Live(pl) || !Head(pl, out float y)) { _aboveSince = -1f; return; }
            if (float.IsNaN(Standing)) { Standing = y; Recenters++; }
            if (y > Standing + RiseMargin)
            {
                if (_aboveSince < 0f) _aboveSince = Time.unscaledTime;
                else if (Time.unscaledTime - _aboveSince > RiseAfterS) { Standing = y; Rises++; _aboveSince = -1f; }
            }
            else _aboveSince = -1f;
            // the rig's floor sits one standing height below the body's standing eye, whatever the crouch or the lean did
            float want = pl.TdefStandPos.position.y - Standing;
            var cp = pl.CamParent;
            float have = cp.position.y;
            if (Mathf.Abs(want - have) > 1e-4f) cp.position += Vector3.up * (want - have);
            Applied = true;
        }

        internal static void AppendJson(System.Text.StringBuilder b)
        {
            b.Append("\"height\":{\"enabled\":").Append(Enabled ? "true" : "false");
            b.Append(",\"standing\":").Append(float.IsNaN(Standing) ? "null" : VrNative.F(Standing));
            b.Append(",\"headDuck\":").Append(VrNative.F(HeadDuck));
            b.Append(",\"recenters\":").Append(Recenters).Append(",\"rises\":").Append(Rises);
            b.Append(",\"applied\":").Append(Applied ? "true" : "false").Append('}');
        }
    }
}
