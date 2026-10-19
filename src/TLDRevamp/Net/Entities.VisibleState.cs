using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private static bool ReadCompactMoving;

        private static bool LooseRelease(uint previousCar, uint car, bool held, bool stored) =>
            previousCar != 0 && car == 0 && !held && !stored;

        private static bool CargoBody(Ent e) => e.Root.P != null || Limbs(e) != null && Limp(e);

        // A release can precede the first falling physics step. Keep sending its motion until the real rest state.
        private static bool ReleaseMotion(ref bool active, bool released, bool held, bool stored, ref bool moving)
        {
            if (held || stored) active = false;
            else if (released) { active = true; moving = true; }
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

        private static void ReadEntityHead(NetReader r, bool compact, out uint net, out uint epoch, out double stamp,
                                           out Vector3d g, out Quaternion q, out Vector3 v, out bool driven, out bool stored, out bool held, out bool spraying)
        {
            ReadCompactMoving = false;
            if (!compact) { ReadStateHead(r, out net, out epoch, out stamp, out g, out q, out v, out driven, out stored, out held, out spraying); return; }
            CompactPoseCodec.Read(r, out net, out epoch, out stamp, out double x, out double y, out double z, out float yaw, out ReadCompactMoving, out ReadFinalState);
            g = new Vector3d(x, y, z); q = Quaternion.Euler(0, yaw, 0); v = Vector3.zero;
            driven = stored = held = spraying = false; ReadCarriedBy = 0;
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
    }
}
