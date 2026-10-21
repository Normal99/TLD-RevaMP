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
            // the hands the copy places: the newest pose's (where the driver is now) and those the moment drawn here has no
            // data for (it plays ~0.1 s behind): a hand switching to the key was left at the body for that tenth otherwise
            var latest = rp.Riding.Latest; var sample = rp.RidingShownFrame == Time.frameCount ? rp.RidingSample : null;
            byte hands = (byte)((latest != null ? latest.WheelHands : 0) | (sample != null ? sample.WheelHands : 0));
            if (DriverHandsOnWheel && rp.SeatNet != 0 && rp.SeatIdx >= 0) DriverHands(rp, hands, latest, sample);
            else rp.HandId[0] = rp.HandId[1] = null;
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
        /// wheel's grips; on its own gear stick, horn, handbrake lever or key for the game's 0.25 s when this copy applies that
        /// change (DriverReach, from the car's signals); on the spot of its own copy of a usable the driver holds (a lever,
        /// a knob, a door handle: PlayerLook.HandControl), so the hand moves with that control as this copy shows it. They
        /// used to come from the driver's pose stream while wheel and controls came from the car's state: two delays, so the
        /// hands trailed both. Any other hand (a held item) is still sent. Every switch, to the stream and back too, glides.
        public const float ReachS = 0.25f, HandBlendS = 0.12f;
        private static void DriverHands(Rp rp, byte hands, PlayerLook.Pose latest, PlayerLook.Pose sample)
        {
            var seat = Entities.SeatScript(rp.SeatNet, rp.SeatIdx, out var car);
            var a = rp.Mp.anim;
            if (seat == null || !seat.driverSeat0 || car == null) { rp.HandId[0] = rp.HandId[1] = null; return; }
            for (int h = 0; h < 2; h++)
            {
                var target = h == 0 ? a.LHTarget : a.RHTarget;
                if (target == null) continue;
                object id = null; Vector3 pos = target.position; Quaternion rot = target.rotation;   // not placed: the sent pose
                if ((hands & (1 << h)) != 0)
                {
                    var on = latest != null && (latest.WheelHands & (4 << h)) != 0 ? latest : sample != null && (sample.WheelHands & (4 << h)) != 0 ? sample : null;
                    usablescript u = on != null ? Entities.UsableAt(on.Controls[h].Net, on.Controls[h].Item, on.Controls[h].Usable) : null;
                    if (u != null)
                    {
                        var f = UsableFrame(u);
                        id = u; pos = f.TransformPoint(on.Controls[h].Pos); rot = f.rotation * Quaternion.Euler(on.Controls[h].Rot);
                    }
                    else
                    {
                        var to = rp.ReachHand == h && Time.time < rp.ReachUntil && rp.ReachTo != null ? rp.ReachTo : h == 0 ? car.LHDrivePos : car.RHDrivePos;
                        if (to != null) { id = to; pos = to.position; rot = to.rotation; }
                    }
                }
                if (!ReferenceEquals(id, rp.HandId[h]))
                {
                    // a switch (wheel, stick, a control, the stream): glide from where the hand was drawn last frame
                    bool shown = rp.HandShownFrame[h] >= Time.frameCount - 2;
                    rp.HandFromPos[h] = shown ? rp.HandShownPos[h] : target.position;
                    rp.HandFromRot[h] = shown ? rp.HandShownRot[h] : target.rotation;
                    rp.HandSince[h] = Time.time; rp.HandId[h] = id;
                }
                float k = Mathf.Clamp01((Time.time - rp.HandSince[h]) / HandBlendS);
                pos = Vector3.Lerp(rp.HandFromPos[h], pos, k); rot = Quaternion.Slerp(rp.HandFromRot[h], rot, k);
                target.SetPositionAndRotation(pos, rot);
                rp.HandShownPos[h] = pos; rp.HandShownRot[h] = rot; rp.HandShownFrame[h] = Time.frameCount;
            }
        }
        /// The frame a usable's hand spot rides in (fpscontroller.MovedUsingHandObj parents usingHand to it).
        private static Transform UsableFrame(usablescript u) => u.handTarget != null ? u.handTarget : u.TDoor != null ? u.TDoor : u.transform;

        /// Where carscript puts a hand on the key (its currentIgnitionPos).
        internal static Transform IgnitionPos(carscript c) => c.IgnitionHandPos != null ? c.IgnitionHandPos :
            c.Engine != null && c.Engine.gearstick != null ? c.Engine.gearstick.KickNitionHandPos : null;

        /// This copy applied a driver's control change (gear, horn, handbrake, key): move that driver's nearest hand there,
        /// as carscript does with player.MoveNearestHand for a local driver.
        internal static void DriverReach(carscript car, Transform to, float seconds)
        {
            if (!DriverHandsOnWheel || car == null || to == null) return;
            foreach (var rp in All.Values)
            {
                if (rp.SeatNet == 0 || rp.Mp == null || rp.Mp.anim == null) continue;
                var seat = Entities.SeatScript(rp.SeatNet, rp.SeatIdx, out var seated);
                if (seat == null || !seat.driverSeat0 || seated != car) continue;
                var a = rp.Mp.anim;
                float l = a.LHTarget != null ? (a.LHTarget.position - to.position).sqrMagnitude : float.MaxValue;
                float r = a.RHTarget != null ? (a.RHTarget.position - to.position).sqrMagnitude : float.MaxValue;
                rp.ReachHand = l < r ? 0 : 1; rp.ReachTo = to; rp.ReachUntil = Mathf.Max(rp.ReachUntil, Time.time + seconds);
            }
        }

        /// The local driver's hands the copies place (PlayerLook.WriteLocalPose): those the game itself aims at the wheel's
        /// grip, at a control a copy moves on its own (gear stick, key, horn, handbrake: DriverReach), at its free resting
        /// place (defLHPos/defRHPos: the game steers one-handed and parks the other hand there), or at a usable of a shared
        /// object (the game's usingHand: a lever, a knob, a door handle), sent as that usable's address and the spot on it -
        /// fpscontroller's own per-hand choice (VCurrentLHT/RHT), not a distance. A hand holding an item is sent as it is.
        private static readonly HarmonyLib.AccessTools.FieldRef<fpscontroller, usablescript> Selected = HarmonyLib.AccessTools.FieldRefAccess<fpscontroller, usablescript>("selectedU");
        internal static PlayerLook.DriverHandsOut LocalDriverHands(fpscontroller pl, playeranimationscript a)
        {
            var o = new PlayerLook.DriverHandsOut();
            if (!DriverHandsOnWheel || !pl.Bsitting || pl.seat == null || !pl.seat.driverSeat0 || pl.seat.Car == null) return o;
            var c = pl.seat.Car;
            var stick = c.TransmissionHandPos != null ? c.TransmissionHandPos : c.Engine != null && c.Engine.gearstick != null ? c.Engine.gearstick.TransmissionHandPos : null;
            var key = IgnitionPos(c);
            for (int h = 0; h < 2; h++)
            {
                var aim = h == 0 ? pl.VCurrentLHT : pl.VCurrentRHT;
                if (aim == null) continue;
                if (aim == (h == 0 ? c.LHDrivePos : c.RHDrivePos) || aim == (h == 0 ? pl.defLHPos : pl.defRHPos) || aim == stick || aim == key || aim == c.HornHandPos || aim == c.HandBrakeHandPos)
                    o.Flags |= (byte)(1 << h);
                else if (aim == pl.usingHand)
                {
                    var u = Selected(pl);
                    if (u == null || u.setidPoi || !Entities.UsableAddress(u, out uint net, out int item, out int usable)) continue;
                    var f = UsableFrame(u);
                    var ctl = new PlayerLook.HandControl { Net = net, Item = item, Usable = usable, Pos = f.InverseTransformPoint(aim.position), Rot = (Quaternion.Inverse(f.rotation) * aim.rotation).eulerAngles };
                    if (h == 0) o.Left = ctl; else o.Right = ctl;
                    o.Flags |= (byte)((1 | 4) << h);
                }
            }
            return o;
        }

        private static string Id(object id) => id is Object o && o != null ? (id is usablescript ? "usable " : "") + o.name : "";
        /// Test (bridge `mp hands`): each remote player's seat, hand flags and where its hand targets and wheel grips are.
        internal static string HandsJson()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string V(Vector3 v) => "[" + v.x.ToString("F3", ic) + "," + v.y.ToString("F3", ic) + "," + v.z.ToString("F3", ic) + "]";
            string T(Transform t) => t == null ? "null" : V(t.position);
            var rows = new System.Collections.Generic.List<string>();
            foreach (var rp in All.Values)
            {
                var a = rp.Mp != null ? rp.Mp.anim : null; var latest = rp.Riding.Latest;
                var seat = Entities.SeatScript(rp.SeatNet, rp.SeatIdx, out var car);
                rows.Add("{\"id\":" + rp.Id + ",\"seatNet\":" + rp.SeatNet + ",\"seatIdx\":" + rp.SeatIdx + ",\"driver\":" + (seat != null && seat.driverSeat0 ? "true" : "false") +
                    ",\"car\":" + (car != null ? Json.Str(car.name) : "null") + ",\"wheelHands\":" + (latest != null ? latest.WheelHands : -1) + ",\"latestSeat\":" + (latest != null ? latest.Seat : -9) +
                    ",\"lh\":" + (a != null ? T(a.LHTarget) : "null") + ",\"rh\":" + (a != null ? T(a.RHTarget) : "null") +
                    ",\"lgrip\":" + (car != null ? T(car.LHDrivePos) : "null") + ",\"rgrip\":" + (car != null ? T(car.RHDrivePos) : "null") +
                    ",\"body\":" + (rp.Go != null ? V(rp.Go.transform.position) : "null") + ",\"handTargets\":[" + Json.Str(Id(rp.HandId[0])) + "," + Json.Str(Id(rp.HandId[1])) + "]" +
                    ",\"shownFrame\":" + rp.RidingShownFrame + ",\"frame\":" + Time.frameCount + "}");
            }
            return "{\"players\":[" + string.Join(",", rows) + "]}";
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
