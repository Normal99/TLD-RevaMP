using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private static bool ReadCompactMoving;

        private static bool FinalState(bool locked, bool wasteFixes, BandwidthRules.Quiet quiet, bool moving, bool driven) =>
            locked || wasteFixes && (quiet == BandwidthRules.Quiet.Final || !moving && !driven);

        private static double PoseStamp(double physics, double previous, bool first) =>
            first ? physics : System.Math.Max(physics, System.Math.Round(previous * 1000.0) / 1000.0 + 0.001);

        private static bool LooseRelease(uint previousCar, uint car, bool held, bool stored) =>
            previousCar != 0 && car == 0 && !held && !stored;

        private static bool CargoBody(Ent e) => e.Root.P != null || Limbs(e) != null && Limp(e);

        private static bool StateMoving(Ent e, bool pose, bool limbs, bool awake, ref bool velocityMoving, bool ground = false)
        {
            // Joint solver velocities can cross RestSpeed while every limb stays within the visible thresholds.
            // Such a body, or an upright animal animating in place, needs its final pose rather than solver keepalives.
            if (ground || e.Root.car == null && Limbs(e) != null && Limp(e))
            { velocityMoving = false; return pose || limbs; }
            return awake || pose || limbs;
        }

        // A release can precede the first falling physics step. Keep sending its motion until the real rest state.
        private static bool ReleaseMotion(ref bool active, bool released, bool held, bool stored, ref bool moving,
                                          bool first = false, bool unlocked = false)
        {
            if (held || stored) active = false;
            else if (released || first || unlocked) { active = true; moving = true; }
            else if (!moving) active = false;
            return active;
        }

        private static bool NewerState(double stamp, uint epoch, ref double previous, ref uint previousEpoch)
        {
            if (epoch == previousEpoch && stamp <= previous) return false;
            previous = stamp; previousEpoch = epoch; return true;
        }

        private static byte[] FinalSnapshot(NetReader r, bool final)
        {
            if (!final) return null;
            var bytes = new byte[r.End]; System.Buffer.BlockCopy(r.Buf, 0, bytes, 0, r.End); return bytes;
        }

        // A quiet child's world fallback must follow its parent too: late join/interest may load the child first.
        private static void ReplayLockedState(NetWriter w, byte[] bytes, Vector3d position, Quaternion rotation)
        {
            var r = new NetReader { Buf = bytes, Pos = 1, End = bytes.Length };
            ReadEntityHead(r, false, out uint net, out uint epoch, out double stamp, out _, out _, out var v,
                           out bool driven, out bool stored, out bool held, out bool spraying);
            w.Reset();
            WriteStateHead(w, net, epoch, 0, stamp, position, rotation, v, driven, stored, held, spraying,
                           ReadCarriedBy, ReadLocalPos, ReadLocalRot, ReadFinalState, ReadLockedState, ReadLockItem, ReadLockMount);
            w.Bytes(bytes, r.Pos, r.Remaining);
        }

        private static void ReadEntityHead(NetReader r, bool compact, out uint net, out uint epoch, out double stamp,
                                           out Vector3d g, out Quaternion q, out Vector3 v, out bool driven, out bool stored, out bool held, out bool spraying)
        {
            ReadCompactMoving = false;
            if (!compact) { ReadStateHead(r, out net, out epoch, out stamp, out g, out q, out v, out driven, out stored, out held, out spraying); return; }
            CompactPoseCodec.Read(r, out net, out epoch, out stamp, out double x, out double y, out double z, out float yaw, out ReadCompactMoving, out ReadFinalState);
            g = new Vector3d(x, y, z); q = Quaternion.Euler(0, yaw, 0); v = Vector3.zero;
            driven = stored = held = spraying = false; ReadCarriedBy = 0; ReadLockedState = false; ReadLockItem = ReadLockMount = -1;
        }

        private static Vector3 CompactVelocity(bool moving, Vector3d at, double stamp, Vector3d previous, double previousStamp) =>
            moving && previousStamp > 0 && stamp > previousStamp && stamp - previousStamp < 0.5
                ? (Vector3)(at - previous) / (float)(stamp - previousStamp) : Vector3.zero;

        // Slot parts may use a joint instead of transform parenting; trailers and articulated vehicles keep their pose stream.
        private static bool AttachedCarPart(Ent e)
        {
            var at = e.Root != null && e.Root.car == null ? e.Root.attachable : null;
            return at != null && BandwidthRules.AttachedPart(false, at.attached, at.isPart, at.slot != null,
                   at.point != null && at.point.GetComponentInParent<carscript>() != null);
        }

        private static bool CompactFlyer(Ent e) => e.Root.car == null && e.Root.P == null && e.Root.nyul == null &&
            e.Root.npc == null && e.Root.ragdollPos == null && e.Root.GetComponent<randomFlyer>() != null;

        // nyulscript animals share the upright ground path. Locked, dead or ragdolled rabbits keep full poses.
        private static bool CompactGround(Ent e) => e.PartIndex < 0 && e.Root.car == null && e.Root.npc == null &&
            (e.Root.P == null || e.Root.P.physlock == null) &&
            e.Root.nyul != null && e.Root.nyul.AI != null && e.Root.nyul.AI.alive && (Limbs(e) == null || !Limp(e));
    }
}
