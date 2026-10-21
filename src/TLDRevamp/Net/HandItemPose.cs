using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    internal static class HandItemPose
    {
        internal const float BlendS = 0.12f;
        internal static void Relative(Transform hand, Vector3 pos, Quaternion rot, out Vector3 lp, out Quaternion lq)
        { lp = hand.InverseTransformPoint(pos); lq = Quaternion.Inverse(hand.rotation) * rot; }
        internal static void World(Transform hand, Vector3 lp, Quaternion lq, out Vector3 pos, out Quaternion rot)
        { pos = hand.TransformPoint(lp); rot = hand.rotation * lq; }
    }

    /// Preserve the last drawn pose on a source switch, then remove its error against the moving destination.
    /// Global positions keep a floating-origin shift from looking like a new grip or a world-sized glide.
    internal sealed class HandItemGlide
    {
        internal bool HasShown;
        internal object Source;
        internal Vector3d Shown;
        internal Quaternion Rotation;
        private Vector3d error;
        private Quaternion turn = Quaternion.identity;
        private double since = double.NegativeInfinity;
        internal bool Blending(double now) => now - since < HandItemPose.BlendS;
        internal void Seed(Vector3d pos, Quaternion rot) { HasShown = true; Shown = pos; Rotation = rot; }
        internal void Reset() { HasShown = false; Source = null; since = double.NegativeInfinity; error = default; turn = Quaternion.identity; }
        internal void Apply(object source, double now, Vector3d pos, Quaternion rot, out Vector3d shown, out Quaternion rotation)
        {
            if (!ReferenceEquals(Source, source))
            {
                error = HasShown ? Shown - pos : default;
                turn = HasShown ? Rotation * Quaternion.Inverse(rot) : Quaternion.identity;
                since = now; Source = source;
            }
            float k = (float)Math.Max(0, Math.Min(1, (now - since) / HandItemPose.BlendS));
            shown = pos + error * (1 - k);
            rotation = Quaternion.Slerp(turn, Quaternion.identity, k) * rot;
            Seed(shown, rotation);
        }
    }
}
