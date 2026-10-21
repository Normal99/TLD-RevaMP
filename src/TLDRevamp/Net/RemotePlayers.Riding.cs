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
        public static float DriverHandSnapM = 0.25f;
        internal static void RidingTick(int id)
        {
            if (!All.TryGetValue(id, out var rp) || !rp.Has || rp.Go == null || rp.Mp == null || rp.Mp.anim == null) return;
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
                if (DriverHandsOnWheel && last.Seat >= 0) HandsOnWheel(rp.Mp.anim, frame);
            }
            rp.RidingShownFrame = Time.frameCount; rp.RidingShownClock = RidingFrames ? clock : double.NaN;
        }

        /// The hands came from the driver's own pose stream, the wheel from the car's: two delays, so they only sometimes
        /// lined up. A hand that is on the wheel goes onto this copy's grip points (as fpscontroller.UpdHandPos does for a
        /// local driver); one clearly elsewhere (key, horn, gear stick, a held item) keeps its sent place.
        private static void HandsOnWheel(playeranimationscript a, Transform frame)
        {
            var seat = frame.GetComponent<seatscript>();
            var car = seat != null && seat.driverSeat0 ? seat.Car : null;
            if (car == null) return;
            Snap(a.LHTarget, car.LHDrivePos); Snap(a.RHTarget, car.RHDrivePos);
        }
        private static void Snap(Transform target, Transform grip)
        {
            if (target == null || grip == null || (target.position - grip.position).sqrMagnitude > DriverHandSnapM * DriverHandSnapM) return;
            target.SetPositionAndRotation(grip.position, grip.rotation);
        }

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
