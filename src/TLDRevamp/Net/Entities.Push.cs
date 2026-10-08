using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Pushing something another player owns. The game's push (fpscontroller: hold the push key on a pushablescript)
    /// puts a force on the body here, every frame: RB.AddForceAtPosition(dir · mass · pushForce · Time.deltaTime ·
    /// modifier, point). On a copy that does nothing — a copy is kinematic, shown where its owner has it — so a friend's
    /// stuck car could not be pushed. The game's own MP sends the push to the other side (syncScript.SendPush: item,
    /// direction, point in the pushable's space). Here it goes to the object's owner, who puts the same force on the
    /// real body; the copy then follows the owner's states like any other motion.
    public static partial class Entities
    {
        public const byte PushIn = 53;
        public static long PushesSent, PushesApplied, PushesRejected, PushFrames;

        // ---- the pusher's side: frames of one pushable add up between sends (20 Hz)
        private sealed class PushAcc { public uint Net; public int Item, Index; public Vector3 Dir, LPos; public float Dt; }
        private static readonly Dictionary<pushablescript, PushAcc> PushOut = new Dictionary<pushablescript, PushAcc>();
        private static float _pushSendAcc;

        [HarmonyLib.HarmonyPatch(typeof(pushablescript), nameof(pushablescript.Push))]
        private static class PushToOwner
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(pushablescript __instance, Vector3 _dir, Vector3 _pos, bool _sendMulti)
            {
                if (!InSession || !_sendMulti || !IsProxy(__instance)) return true;   // ours, or not shared: the game's push
                if (!PushOut.TryGetValue(__instance, out var a))
                {
                    var it = __instance.GetComponentInParent<tosaveitemscript>();
                    if (it == null) return false;
                    a = null;
                    foreach (var e in ByNet.Values)
                    {
                        if (!e.Proxy || e.Items == null) continue;
                        int i = e.Items.IndexOf(it);
                        if (i < 0) continue;
                        a = new PushAcc { Net = e.NetId, Item = i, Index = System.Array.IndexOf(it.GetComponentsInChildren<pushablescript>(true), __instance) };
                        break;
                    }
                    if (a == null || a.Index < 0) return false;
                    PushOut[__instance] = a;
                }
                a.Dir = _dir; a.LPos = __instance.transform.InverseTransformPoint(_pos); a.Dt += Time.deltaTime;
                PushFrames++;
                return false;   // the copy is not moved here: the owner pushes the real one
            }
        }

        /// Per frame from Tick: what was pushed since the last send goes to the owners.
        private static void PushTick(float dt)
        {
            TestPushTick();
            if (PushOut.Count == 0) return;
            _pushSendAcc += dt;
            if (_pushSendAcc < 0.05f) return;
            _pushSendAcc = 0f;
            foreach (var a in PushOut.Values)
            {
                if (a.Dt <= 0f) continue;
                W.Reset(); W.U8(PushIn); W.U32(a.Net); W.VarU32((uint)a.Item); W.U8((byte)a.Index);
                W.F32(a.Dir.x); W.F32(a.Dir.y); W.F32(a.Dir.z);
                W.F32(a.LPos.x); W.F32(a.LPos.y); W.F32(a.LPos.z);
                W.F32(Mathf.Min(a.Dt, 0.5f));
                ToServer(W, true);
                PushesSent++;
            }
            PushOut.Clear();   // copies come and go (handoff, far store): looked up again on the next push
        }

        /// Server: to the object's owner.
        private static void ServerPush(int from, NetReader r)
        {
            uint net = r.U32();
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId == from) return;
            r.Pos = 0;
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            ServerSendTo(se.OwnerId, WS, true);
        }

        /// Received numbers that go into physics, clocks or health: a NaN/infinity there doesn't fail, it spreads.
        internal static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        internal static bool Finite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
        internal static bool Finite(Vector3d v) => !(double.IsNaN(v.x) || double.IsNaN(v.y) || double.IsNaN(v.z) || double.IsInfinity(v.x) || double.IsInfinity(v.y) || double.IsInfinity(v.z));
        internal static bool Finite(Quaternion q) => Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w);
        internal static bool Finite(Vector3 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        // ---- the owner's side: the game's Push, with the pusher's frame time
        private static void ApplyPush(NetReader r)
        {
            uint net = r.U32(); int item = (int)r.VarU32(), index = r.U8();
            var dir = new Vector3(r.F32(), r.F32(), r.F32());
            var lpos = new Vector3(r.F32(), r.F32(), r.F32());
            float pdt = r.F32();
            if (r.Bad || !ByNet.TryGetValue(net, out var e) || e.Proxy || e.Items == null || item < 0 || item >= e.Items.Count || e.Items[item] == null) return;
            // a NaN passed `pdt <= 0` and went into the body (a car the physics engine can't place any more); legit: a
            // direction of about unit length, a few frames of the pusher's time, a point on the pushable
            if (!Finite(dir) || !Finite(lpos) || float.IsNaN(pdt) || pdt > 1f || dir.sqrMagnitude > 4f || lpos.sqrMagnitude > 1e4f) { PushesRejected++; return; }
            var ps = e.Items[item].GetComponentsInChildren<pushablescript>(true);
            if (index >= ps.Length || ps[index] == null) return;
            var p = ps[index];
            var rb = p.GetComponent<Rigidbody>();
            if (rb == null || pdt <= 0f) return;
            // pushablescript.Push, body for body (its Time.deltaTime is the pusher's frames, summed)
            dir *= rb.mass * mainscript.s.pushForce;
            if (p.both)
            {
                var flat = Vector3.ProjectOnPlane(dir, Vector3.up);
                if (Vector3.Angle(flat, dir) < 45f) dir = flat;
            }
            else if (p.onlyProjected) dir = Vector3.ProjectOnPlane(dir, Vector3.up);
            rb.AddForceAtPosition(dir * pdt * p.modifier, p.transform.TransformPoint(lpos));
            PushesApplied++;
        }

        // ---- test hook (bridge): this player pushes the nearest pushable of an object for a while, through the game's
        // own Push, every frame — what holding the push key on it does
        private static uint _testPushNet; private static float _testPushUntil;
        public static string TestPush(uint net, float secs)
        {
            if (!ByNet.TryGetValue(net, out var e) || e.Root == null) return "{\"error\":\"no such object\"}";
            _testPushNet = net; _testPushUntil = Time.time + secs;
            return "{\"pushing\":" + net + ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"pushables\":" + e.Root.GetComponentsInChildren<pushablescript>(true).Length + "}";
        }

        private static void TestPushTick()
        {
            if (_testPushNet == 0) return;
            if (Time.time > _testPushUntil || !ByNet.TryGetValue(_testPushNet, out var e) || e.Root == null) { _testPushNet = 0; return; }
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return;
            var p = e.Root.GetComponentInChildren<pushablescript>();
            if (p == null) return;
            var from = pl.transform.position;
            var col = p.GetComponentInChildren<Collider>();
            var at = col != null ? col.ClosestPoint(from) : p.transform.position;
            var d = at - from; d.y = 0f;
            if (d.sqrMagnitude < 1e-4f) return;
            p.Push(d.normalized, at, true);
        }
    }
}
