using UnityEngine;

namespace TLDRevamp.Net
{
    /// After every Update has moved the cars and animated the targets, before the game's IKscript.LateUpdate.
    [DefaultExecutionOrder(-10000)]
    internal sealed class RidingRender : MonoBehaviour
    {
        internal int Player;
        private void LateUpdate() => RemotePlayers.RidingTick(Player);
    }

    public static partial class RemotePlayers
    {
        public static bool RidingFrames = true;   // A/B: false = body and limbs use their separate playback paths
        public static bool DriverHandsOnWheel = true;   // A/B: false = a driver's hands follow only their own pose stream
        internal static void RidingTick(int id)
        {
            if (!All.TryGetValue(id, out var rp) || !rp.Has || rp.Go == null || rp.Mp == null || rp.Mp.anim == null) return;
            RidingFrame(rp);
            // also when no riding sample is ready this frame: the hands then come from the pose stream's own playback
            var latest = rp.Riding.Latest;
            if (DriverHandsOnWheel && latest != null && latest.WheelHands != 0 && rp.SeatNet != 0 && rp.SeatIdx >= 0)
            {
                var seat = Entities.SeatTransformCached(rp.SeatNet, rp.SeatIdx, ref rp.CachedCarRoot, ref rp.CachedSeat);
                if (seat != null) DriverHands(rp, seat, latest.WheelHands);
            }
            else rp.HandTarget[0] = rp.HandTarget[1] = null;
        }
        private static void RidingFrame(Rp rp)
        {
            var last = rp.Riding.Latest;
            if (last == null || last.Net == 0) return;
            // State and pose packets may arrive in either order. Never put an old seat/footing pose onto a new frame.
            if (last.Seat >= 0 ? last.Net != rp.SeatNet || last.Seat != rp.SeatIdx : rp.SeatNet != 0 || last.Net != rp.Foot.Net) return;
            if (!Entities.RidingClock(last.Net, out uint epoch, out double clock) || epoch != last.Epoch) return;
            var frame = last.Seat >= 0 ? Entities.SeatTransformCached(last.Net, last.Seat, ref rp.CachedCarRoot, ref rp.CachedSeat) : Entities.FootRootOf(last.Net);
            if (frame == null || !rp.Riding.Sample(clock, rp.RidingSample)) return;
            var p = rp.RidingSample; var t = rp.Go.transform;
            if (RidingFrames)
            {
                t.SetPositionAndRotation(frame.TransformPoint(p.Body), frame.rotation * p.Facing);
                PlayerLook.ApplyRiding(rp.Mp, p, frame);
            }
            rp.RidingShownFrame = Time.frameCount; rp.RidingShownClock = RidingFrames ? clock : double.NaN;
        }

        /// A driver's hands, placed by this copy as fpscontroller.UpdHandPos places a local driver's: on its own steering
        /// wheel's grips, and on its own gear stick, horn or handbrake lever for the game's 0.25 s when this copy applies
        /// that change (DriverReach, from the car's signals). They used to come from the driver's pose stream while wheel
        /// and stick came from the car's state: two delays, so the hands trailed both. Such hands are no longer sent
        /// (PlayerLook.WriteLocalPose); a hand doing anything else (radio, window, a held item) still is.
        public const float ReachS = 0.25f, HandBlendS = 0.12f;
        private static void DriverHands(Rp rp, Transform frame, byte hands)
        {
            var seat = frame.GetComponent<seatscript>();
            var car = seat != null && seat.driverSeat0 ? seat.Car : null;
            var a = rp.Mp.anim;
            if (car == null) return;
            for (int h = 0; h < 2; h++)
            {
                var target = h == 0 ? a.LHTarget : a.RHTarget;
                if ((hands & (1 << h)) == 0 || target == null) { rp.HandTarget[h] = null; continue; }
                var to = rp.ReachHand == h && Time.time < rp.ReachUntil && rp.ReachTo != null ? rp.ReachTo : h == 0 ? car.LHDrivePos : car.RHDrivePos;
                if (to == null) continue;
                if (to != rp.HandTarget[h])
                {
                    // a switch (wheel, stick, back): glide from where the hand is drawn now, onto the moving target
                    rp.HandFromPos[h] = rp.HandTarget[h] != null ? rp.HandShownPos[h] : target.position;
                    rp.HandFromRot[h] = rp.HandTarget[h] != null ? rp.HandShownRot[h] : target.rotation;
                    rp.HandSince[h] = Time.time; rp.HandTarget[h] = to;
                }
                float u = Mathf.Clamp01((Time.time - rp.HandSince[h]) / HandBlendS);
                var pos = Vector3.Lerp(rp.HandFromPos[h], to.position, u); var rot = Quaternion.Slerp(rp.HandFromRot[h], to.rotation, u);
                target.SetPositionAndRotation(pos, rot);
                rp.HandShownPos[h] = pos; rp.HandShownRot[h] = rot;
            }
        }

        /// This copy applied a driver's control change (gear, horn, handbrake): move that driver's nearest hand there,
        /// as carscript does with player.MoveNearestHand for a local driver.
        internal static void DriverReach(carscript car, Transform to, float seconds)
        {
            if (!DriverHandsOnWheel || car == null || to == null) return;
            foreach (var rp in All.Values)
            {
                if (rp.SeatNet == 0 || rp.CachedSeat == null || rp.Mp == null || rp.Mp.anim == null) continue;
                var seat = rp.CachedSeat.GetComponent<seatscript>();
                if (seat == null || !seat.driverSeat0 || seat.Car != car) continue;
                var a = rp.Mp.anim;
                float l = a.LHTarget != null ? (a.LHTarget.position - to.position).sqrMagnitude : float.MaxValue;
                float r = a.RHTarget != null ? (a.RHTarget.position - to.position).sqrMagnitude : float.MaxValue;
                rp.ReachHand = l < r ? 0 : 1; rp.ReachTo = to; rp.ReachUntil = Mathf.Max(rp.ReachUntil, Time.time + seconds);
            }
        }

        /// The local driver's hands the copies place (PlayerLook.WriteLocalPose): in the driver's seat, a hand at the wheel's
        /// grip, at a car control, or on its way between them (the game's reach timer, or within reach of the wheel).
        private static readonly HarmonyLib.AccessTools.FieldRef<fpscontroller, float> Reaching = HarmonyLib.AccessTools.FieldRefAccess<fpscontroller, float>("VmovedNearestHand");
        internal static byte LocalDriverHands(fpscontroller pl, playeranimationscript a)
        {
            if (!DriverHandsOnWheel || !pl.Bsitting || pl.seat == null || !pl.seat.driverSeat0 || pl.seat.Car == null || pl.inHandP != null) return 0;
            var c = pl.seat.Car; bool reaching = Reaching(pl) > 0f;
            var stick = c.TransmissionHandPos != null ? c.TransmissionHandPos : c.Engine != null && c.Engine.gearstick != null ? c.Engine.gearstick.TransmissionHandPos : null;
            bool Placed(Transform hand, Transform grip)
            {
                if (hand == null || grip == null) return false;
                if (reaching || (hand.position - grip.position).sqrMagnitude < .3f * .3f) return true;   // on the wheel or returning to it
                return Near(hand, stick) || Near(hand, c.HornHandPos) || Near(hand, c.HandBrakeHandPos) || Near(hand, c.IgnitionHandPos) || Near(hand, c.LightHandPos);
            }
            return (byte)((Placed(a.LHTarget, c.LHDrivePos) ? 1 : 0) | (Placed(a.RHTarget, c.RHDrivePos) ? 2 : 0));
        }
        private static bool Near(Transform hand, Transform control) => control != null && (hand.position - control.position).sqrMagnitude < .12f * .12f;

        internal static void TelemetryRiders(uint net)
        {
            foreach (var rp in All.Values)
            {
                var last = rp.Riding.Latest;
                if (rp.RidingShownFrame != Time.frameCount || last == null || last.Net != net || rp.Go == null || rp.Mp == null || rp.Mp.anim == null || rp.Mp.anim.Pelvis == null) continue;
                Entities.TelemetryRider(net, rp.Id, last.Seat, rp.RidingShownClock, rp.Go.transform.position, rp.Mp.anim.Pelvis.position);
            }
        }
    }
}
