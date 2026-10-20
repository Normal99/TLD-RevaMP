using UnityEngine;

namespace TLDRevamp.Net
{
    // Baselines advance only after a send. A lock is quiet in its transmitted frame, never merely because it has no body.
    internal struct PhysLockPose
    {
        internal bool Locked;
        internal object Mount;
        internal uint Frame, Epoch;
        internal Vector3d Position;
        internal Quaternion Rotation;

        internal bool Transition(bool locked, object mount, uint frame, uint epoch) =>
            Epoch != epoch || Locked != locked || locked && (!ReferenceEquals(Mount, mount) || Frame != frame);

        internal bool Moved(Vector3d position, Quaternion rotation) =>
            BandwidthRules.PoseChanged((position - Position).sqrMagnitude, Quaternion.Angle(rotation, Rotation));

        internal void Sent(bool locked, object mount, uint frame, uint epoch, Vector3d position, Quaternion rotation)
        {
            Locked = locked; Mount = mount; Frame = frame; Epoch = epoch; Position = position; Rotation = rotation;
        }
    }
}
