using UnityEngine;

namespace TLDRevamp.Net
{
    // All RidingRender instances finish at -10000; all game Updates have already played back walking targets.
    // Run before IKscript.LateUpdate consumes those targets, including when the holder model disappeared.
    [DefaultExecutionOrder(-9999)]
    internal sealed class HandItemRender : MonoBehaviour
    {
        private void LateUpdate() => Entities.HandItemsLate();
    }

    public static partial class Entities
    {
        private static bool LocalHandPose(Ent e, bool held, bool stored, bool locked, out Vector3 lp, out Quaternion lq)
        {
            lp = default; lq = Quaternion.identity;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var a = pl != null ? pl.anim : null;
            var p = e.Root.P;
            if (!held || stored || locked || p == null || pl == null || a == null || a.RHTarget == null ||
                (pl.inHandP != p && pl.pickedUp != p)) return false;
            HandItemPose.Relative(a.RHTarget, e.Root.transform.position, e.Root.transform.rotation, out lp, out lq);
            return Finite(lp) && Finite(lq) && Mathf.Abs(lp.x) <= 2f && Mathf.Abs(lp.y) <= 2f && Mathf.Abs(lp.z) <= 2f;
        }

        private static Transform ItemHand(Ent e) => e.Proxy && e.HandPose && e.HandEpoch == e.Epoch && e.HandOwner == e.OwnerId &&
            e.Held && !e.Stored && !e.PoseLocked && e.WornBy < 0 ? RemotePlayers.ItemHand(e.OwnerId) : null;

        // The ordinary transform/body movers yield until the release glide ends. Never put a hand pose into Ip:
        // its world/car samples keep running for drops, server records and machines without the holder model.
        private static bool HandManaged(Ent e) => e.Proxy && (ItemHand(e) != null || e.HandDraw.Source != null || e.HandDraw.Blending(Time.timeAsDouble));

        private static void TakeHandPose(Ent e, Vector3d global, Quaternion globalRot)
        {
            e.HandWorld = global; e.HandWorldRot = globalRot;
            if (!e.HandDraw.HasShown && e.Root != null)
                e.HandDraw.Seed(mainscript.GlobalFromUnityPos(e.Root.transform.position), e.Root.transform.rotation);
            double now = Time.timeAsDouble;
            if (ReadHandPose)
            {
                SampleHandOffset(e, now, out var from, out var rot);
                if (!e.HandPose || e.HandEpoch != e.Epoch || e.HandOwner != e.OwnerId)
                { from = ReadHandPos; rot = ReadHandRot; }
                if (!e.HandPose || (e.HandPos - ReadHandPos).sqrMagnitude > 1e-10f || Quaternion.Angle(e.HandRot, ReadHandRot) > .01f)
                { e.HandFrom = from; e.HandFromRot = rot; e.HandSince = now; }
                e.HandPos = ReadHandPos; e.HandRot = ReadHandRot;
            }
            e.HandPose = ReadHandPose; e.HandEpoch = e.Epoch; e.HandOwner = e.OwnerId;
        }

        private static void SampleHandOffset(Ent e, double now, out Vector3 lp, out Quaternion lq)
        {
            float k = Mathf.Clamp01((float)((now - e.HandSince) / .08));
            lp = Vector3.Lerp(e.HandFrom, e.HandPos, k); lq = Quaternion.Slerp(e.HandFromRot, e.HandRot, k);
        }

        internal static void HandItemsLate()
        {
            if (!InSession) return;
            double now = Time.timeAsDouble;
            foreach (var e in ByNet.Values)
            {
                if (e.Root == null || !HandManaged(e)) continue;
                var hand = ItemHand(e);
                Vector3d pos; Quaternion rot;
                if (hand != null)
                {
                    SampleHandOffset(e, now, out var lp, out var lq);
                    HandItemPose.World(hand, lp, lq, out var world, out rot);
                    pos = mainscript.GlobalFromUnityPos(world);
                }
                else if (e.Ip != null && e.Ip.Ready && !WornRides(e))
                    EvalShown(e, Time.realtimeSinceStartupAsDouble, Time.deltaTime, out pos, out rot);
                else { pos = e.HandWorld; rot = e.HandWorldRot; }
                // First pickup glides from the current normal draw; subsequent switches use the previous final draw.
                if (e.HandDraw.Source == null && !e.HandDraw.Blending(now))
                    e.HandDraw.Seed(mainscript.GlobalFromUnityPos(e.Root.transform.position), e.Root.transform.rotation);
                e.HandDraw.Apply(hand, now, pos, rot, out pos, out rot);
                if (e.MovedByBody) StopBodyMotion(e);
                var rb = e.Root.GetComponent<Rigidbody>();
                var up = mainscript.UnityPosFromGlobal(pos);
                if (rb != null)
                {
                    if (e.Physical) ExitPhysical(e, rb);
                    rb.isKinematic = true; rb.interpolation = RigidbodyInterpolation.None;
                    rb.position = up; rb.rotation = rot;
                }
                e.Root.transform.SetPositionAndRotation(up, rot);
                e.ShownPos = pos;
            }
        }

        internal static string HandItemsJson(int owner, Transform hand)
        {
            var rows = new System.Collections.Generic.List<string>();
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string F(float f) => f.ToString("F4", ic);
            string V(Vector3 v) => "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]";
            string Q(Quaternion q) => "[" + F(q.x) + "," + F(q.y) + "," + F(q.z) + "," + F(q.w) + "]";
            foreach (var e in ByNet.Values)
            {
                if (!e.Proxy || e.OwnerId != owner || !e.Held) continue;
                string distance = hand != null && e.Root != null && e.HandPose ? F((e.Root.transform.position - hand.TransformPoint(e.HandPos)).magnitude) : "null";
                rows.Add("{\"net\":" + e.NetId + ",\"active\":" + (ItemHand(e) != null && ReferenceEquals(e.HandDraw.Source, hand) ? "true" : "false") +
                    ",\"distance\":" + distance + ",\"lp\":" + (e.HandPose ? V(e.HandPos) : "null") + ",\"lq\":" + (e.HandPose ? Q(e.HandRot) : "null") + "}");
            }
            return "[" + string.Join(",", rows) + "]";
        }
    }

    public static partial class RemotePlayers
    {
        internal static Transform ItemHand(int id) => All.TryGetValue(id, out var rp) && rp.Has && rp.Go != null && rp.Mp != null && rp.Mp.anim != null ? rp.Mp.anim.RHTarget : null;
    }
}
