using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        public static bool TelemetryAllOwnedPhysics;
        internal static long TelemetryPhysicsSkipped;
        private const int TelemetryPhysicsLimit = 4096;
        private static readonly uint[] TelemetryFocus = new uint[9], TelemetryRecent = new uint[4];
        private static Transform _telemetryLook;
        private static double _telemetryFocusAt;
        private static uint _telemetryGround;
        internal static void ResetTelemetry()
        {
            Array.Clear(TelemetryRecent, 0, 4); Array.Clear(TelemetryFocus, 0, TelemetryFocus.Length); _telemetryLook = null; _telemetryFocusAt = 0;
            _telemetryFooting = 0;
            _telemetryGround = 0;
            TelemetryPhysicsSkipped = 0;
            foreach (var e in ByNet.Values) { e.TelemetryPhysics = false; e.TelemetryReceived = e.TelemetryReceiptAt = e.TelemetrySentAt = 0; }
            Tpa.ResetTelemetry();
        }
        internal static void TelemetryTouch(uint net)
        {
            if (net == 0) return;
            int at = Array.IndexOf(TelemetryRecent, net); if (at < 0) at = 3;
            for (int i = at; i > 0; i--) TelemetryRecent[i] = TelemetryRecent[i - 1];
            TelemetryRecent[0] = net;
        }
        private static uint TelemetryNet(Component c)
        {
            if (c == null) return 0;
            var it = c as tosaveitemscript ?? c.GetComponentInParent<tosaveitemscript>();
            var e = it != null ? SharedGroupOf(it) : null;
            if (e != null) return e.NetId;
            var root = c.transform.root;
            NetOf(root, out uint net, out _);
            if (net == 0 && root.GetComponent<Rigidbody>() != null)
                foreach (var group in ByNet.Values) if (group.Root != null && group.Root.partslotscripts != null)
                    foreach (var slot in group.Root.partslotscripts) if (slot != null && slot.transform.root == root) return group.NetId;
            return net;
        }
        private static void TelemetryFocusTick(double now)
        {
            if (now < _telemetryFocusAt) return;
            _telemetryFocusAt = now + .1;
            var p = mainscript.s != null ? mainscript.s.player : null;
            LocalSeat(out uint seated, out _);
            TelemetryFocus[0] = seated;
            TelemetryFocus[1] = p != null && !p.Bsitting && p.grounded != null && p.grounded.sensed ? TelemetryNet(p.grounded.T) : 0;
            if (TelemetryFocus[1] == 0 && p != null && !p.Bsitting) TelemetryFocus[1] = _telemetryFooting;
            if (TelemetryFocus[1] != _telemetryGround) { _telemetryGround = TelemetryFocus[1]; TelemetryTouch(_telemetryGround); }
            TelemetryFocus[2] = TelemetryNet(_telemetryLook);
            TelemetryFocus[3] = p != null ? TelemetryNet(p.pickedUp) : 0;
            TelemetryFocus[4] = p != null ? TelemetryNet(p.inHandP) : 0;
            Array.Copy(TelemetryRecent, 0, TelemetryFocus, 5, 4);
            SessionLog.History.Trace.SetFocus(TelemetryFocus, now, SessionLog.St);
        }
        internal static void TelemetryRender()
        {
            if (!InSession) return;
            double now = Time.realtimeSinceStartupAsDouble;
            TelemetryFocusTick(now);
            for (int i = 0; i < 8; i++)
            {
                uint net = SessionLog.History.Trace.Focus(i);
                if (net == 0 || !ByNet.TryGetValue(net, out var e) || e.Root == null || !e.Proxy) continue;
                var p = mainscript.GlobalFromUnityPos(e.Root.transform.position);
                var ip = e.Ip;
                TelemetryPose(e, 2, ip != null && ip.Ready ? ip.RenderTime : double.NaN, p, e.Root.transform.rotation, e.LastVel,
                    mode: e.TelemetrySnapFrame == Time.frameCount ? 3 : RidesParent(e) || e.CarriedBy != 0 || e.Physical ? 4 : ip != null ? ip.TelemetryMode : 2,
                    correction: e.TelemetrySnapFrame == Time.frameCount ? e.TelemetrySnapDistance : Mathf.Max(e.TelemetryServoCorrection, ip != null ? ip.CorrCm / 100f : 0));
            }
        }
        private static void TelemetryPose(Ent e, int kind, double clock, Vector3d p, Quaternion q, Vector3 velocity, int item = -1, int slot = -1, int mode = 0, float correction = 0)
        {
            if (!SessionLog.History.Trace.Contains(e.NetId)) return;
            double now = Time.realtimeSinceStartupAsDouble;
            SessionLog.History.Trace.Add(new MotionRow { Local = now, St = SessionLog.St, Net = e.NetId, Epoch = e.Epoch, Owner = e.OwnerId,
                Kind = kind, Clock = clock, X = p.x, Y = p.y, Z = p.z, Yaw = q.eulerAngles.y, Speed = velocity.magnitude,
                Interval = e.TelemetryInterval, Age = e.TelemetryReceived > 0 ? (float)(now - e.TelemetryReceived) : float.NaN,
                Mode = mode, Correction = correction, Item = item, Slot = slot });
        }
        internal static void TelemetryUsable(uint net, int item, int usable, bool sent)
        {
            if (!ByNet.TryGetValue(net, out var e)) return;
            TelemetryPose(e, sent ? 3 : 4, double.NaN, default, Quaternion.identity, default, item, usable);
        }
        private static void TelemetryReceived(Ent e, double clock, Vector3d p, Quaternion q, Vector3 velocity)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            e.TelemetryInterval = e.TelemetryReceiptAt > 0 ? (float)(now - e.TelemetryReceiptAt) : float.NaN; e.TelemetryReceiptAt = now;
            var ip = e.Ip;
            if (ip != null && ip.TelemetryAccepted) e.TelemetryReceived = now;
            float correction = ip != null ? ip.TelemetryCorrection : 0;
            TelemetryPose(e, 1, clock, p, q, velocity, mode: ip != null && ip.TelemetrySnap ? 3 : 0, correction: correction);
            if (correction > 2) SessionLog.Event(ip.TelemetrySnap ? "proxy-snap" : "proxy-correction", e.NetId, epoch: e.Epoch, value: correction, sample: true);
        }
        private static void TelemetryPhysicsTick()
        {
            double time = Time.fixedTimeAsDouble;
            if (TelemetryAllOwnedPhysics)
            {
                int n = 0;
                foreach (var e in ByNet.Values)
                {
                    if (e.Proxy || e.Root == null || e.Root.RB == null) continue;
                    if (n++ >= TelemetryPhysicsLimit) { TelemetryPhysicsSkipped++; continue; }
                    TelemetryPhysics(e, time);
                }
            }
            else for (int i = 0; i < 8; i++)
                if (ByNet.TryGetValue(SessionLog.History.Trace.Focus(i), out var e)) TelemetryPhysics(e, time);
        }
        private static void TelemetryPhysics(Ent e, double time)
        {
            if (e.Proxy || e.Root == null || e.Root.RB == null) { e.TelemetryPhysics = false; return; }
            var rb = e.Root.RB; var v = rb.velocity; var p = mainscript.GlobalFromUnityPos(rb.position);
            if (e.Root.car == null && v.sqrMagnitude > 6400) SessionLog.Event("speed-high", e.NetId, epoch: e.Epoch, value: v.magnitude, sample: true);
            if (e.TelemetryPhysics && e.TelemetryEpoch == e.Epoch && time - e.TelemetryPhysicsAt <= Time.fixedDeltaTime * 1.5)
            {
                float change = (v - e.TelemetryVelocity).magnitude;
                if (change > 25) SessionLog.Event("speed-jump", e.NetId, epoch: e.Epoch, value: change, sample: true);
                double distance = (p - e.TelemetryPosition).magnitude;
                if (distance > 25 + Math.Max(v.magnitude, e.TelemetryVelocity.magnitude) * Time.fixedDeltaTime)
                    SessionLog.Event("owner-teleport", e.NetId, epoch: e.Epoch, value: (float)distance, sample: true);
            }
            e.TelemetryPhysics = true; e.TelemetryPhysicsAt = time; e.TelemetryEpoch = e.Epoch; e.TelemetryVelocity = v; e.TelemetryPosition = p;
        }
        private static void TelemetryProxySnap(Ent e, Vector3 target, Vector3 from)
        {
            float distance = (target - from).magnitude;
            e.TelemetrySnapFrame = Time.frameCount; e.TelemetrySnapDistance = distance;
            if (distance > 2) SessionLog.Event("proxy-snap", e.NetId, epoch: e.Epoch, value: distance, sample: true);
        }
        private static void TelemetrySentTeleport(Ent e, Vector3d pos, Vector3 velocity)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (e.TelemetrySentAt > 0 && e.TelemetrySentEpoch == e.Epoch)
            {
                double distance = (pos - e.LastSentPos).magnitude;
                if (distance > 25 + velocity.magnitude * (now - e.TelemetrySentAt))
                    SessionLog.Event("owner-teleport", e.NetId, epoch: e.Epoch, value: (float)distance, result: "between sent states", sample: true);
            }
            e.TelemetrySentAt = now; e.TelemetrySentEpoch = e.Epoch;
        }
        private static int TelemetryCategory(Ent e)
        {
            var r = e.Root;
            if (r == null) return e.TelemetryCategory;
            if (!ReferenceEquals(r, e.TelemetryRoot))
            {
                e.TelemetryRoot = r;
                e.TelemetryCategory = TelemetryRootCategory(r);
            }
            if (r.npc != null && r.npc.died || r.nyul != null && r.nyul.AI != null && !r.nyul.AI.alive || e.Rag != null && e.Rag.Act != null && e.Rag.Act.ragdoll) return e.TelemetryCategory = 5;
            return e.TelemetryCategory;
        }
        private static int TelemetryRootCategory(tosaveitemscript r) => r == null ? 7 : r.GetComponentInChildren<utanfutoscript>(true) != null ? 1 : r.car != null ? 0 : r.nyul != null ? 3 : r.npc != null ? 4 : r.ragdollPos != null ? 5 : r.P != null ? 2 : 7;
        private static int TelemetryPrefabCategory(int prefab)
        {
            var go = itemdatabase.s != null ? itemdatabase.s.Item(prefab) : null;
            return go != null ? TelemetryRootCategory(go.GetComponent<tosaveitemscript>()) : 7;
        }
        internal static int TelemetryPacketCategory(byte type, byte[] bytes, int at, int length)
        {
            if (type == Protocol.State || type == Protocol.OtherState || type == Protocol.Pose || type == Protocol.OtherPose) return 6;
            if (type != State && type != AiState && type != RabbitState) return -1;
            uint net = 0;
            if (bytes != null && length > 1 && at >= 0 && at + length <= bytes.Length)
            {
                if (type == State)
                {
                    for (int j = 1, shift = 0; j < length && shift < 35; j++, shift += 7)
                    { byte b = bytes[at + j]; net |= (uint)(b & 127) << shift; if (b < 128) break; }
                }
                else if (length >= 5) net = (uint)(bytes[at + 1] | bytes[at + 2] << 8 | bytes[at + 3] << 16 | bytes[at + 4] << 24);
            }
            if (ByNet.TryGetValue(net, out var e)) return TelemetryCategory(e);
            if (Server.TryGetValue(net, out var se)) return se.TelemetryCategory;
            return type == RabbitState ? 3 : type == AiState ? 4 : 7;
        }
        internal static void TelemetryOwned(int[] into)
        {
            Array.Clear(into, 0, into.Length);
            foreach (var e in ByNet.Values) if (!e.Proxy && e.OwnerId == MyId) into[TelemetryCategory(e)]++;
            if (InSession && !DedicatedServer.Enabled && mainscript.s != null && mainscript.s.player != null) into[6] = 1;
        }
        public static string TelemetryTrace()
        {
            using (var w = new System.IO.StringWriter(System.Globalization.CultureInfo.InvariantCulture))
            { SessionLog.History.Trace.Write(w, Time.realtimeSinceStartupAsDouble, false); return w.ToString(); }
        }

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.RayStuff))]
        private static class TelemetryCrosshair
        {
            private static void Prefix() { _telemetryLook = null; }
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var ray = AccessTools.Method(typeof(Physics), nameof(Physics.Raycast), new[] { typeof(Vector3), typeof(Vector3), typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int) });
                bool replaced = false;
                foreach (var instruction in instructions)
                {
                    if (!replaced && instruction.Calls(ray)) { instruction.operand = AccessTools.Method(typeof(TelemetryCrosshair), nameof(Raycast)); replaced = true; }
                    yield return instruction;
                }
                if (!replaced) Plugin.Log.LogWarning("Telemetry: interaction ray hook missing");
            }
            private static bool Raycast(Vector3 from, Vector3 dir, out RaycastHit hit, float distance, int mask)
            {
                bool found = Physics.Raycast(from, dir, out hit, distance, mask);
                _telemetryLook = found ? hit.transform : null; return found;
            }
        }
        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Pickup), new[] { typeof(pickupable) })]
        private static class TelemetryPickup
        {
            private static void Postfix(fpscontroller __instance)
            { if (mainscript.s != null && mainscript.s.player == __instance && __instance.pickedUp != null) TelemetryTouch(TelemetryNet(__instance.pickedUp)); }
        }
        [HarmonyPatch(typeof(usablescript), nameof(usablescript.SyncMulti), new[] { typeof(bool) })]
        private static class TelemetryUsed
        {
            private static void Postfix(usablescript __instance, bool syncinmulti)
            { if (syncinmulti && PlayerActing && !_applyingState && !_applyingLive) TelemetryTouch(TelemetryNet(__instance)); }
        }
        [HarmonyPatch(typeof(pushablescript), nameof(pushablescript.Push))]
        private static class TelemetryPushed
        {
            private static void Prefix(pushablescript __instance, bool _sendMulti)
            { if (_sendMulti && PlayerActing) TelemetryTouch(TelemetryNet(__instance)); }
        }
        [HarmonyPatch(typeof(attachablescript), nameof(attachablescript.Attach), new[] { typeof(Transform) })]
        private static class TelemetryJointCreated
        {
            private static void Prefix(attachablescript __instance, out Joint __state) { __state = __instance.J; }
            private static void Postfix(attachablescript __instance, Joint __state)
            {
                if (__instance.J == null || __instance.J == __state || !__instance.attached) return;
                SessionLog.Event("joint-created", TelemetryNet(__instance), TelemetryNet(__instance.J.connectedBody), sample: true);
            }
        }
        [HarmonyPatch(typeof(attachablescript), nameof(attachablescript.Detach))]
        private static class TelemetryJointBroken
        {
            private static void Prefix(attachablescript __instance, out (bool attached, uint net, uint parent, bool joint) __state)
            {
                __state = default; if (!__instance.attached) return;
                var parent = __instance.J != null && __instance.J.connectedBody != null ? (Component)__instance.J.connectedBody : __instance.AttachedToTosave();
                __state = (true, TelemetryNet(__instance), TelemetryNet(parent), __instance.J != null);
            }
            private static void Postfix(attachablescript __instance, (bool attached, uint net, uint parent, bool joint) __state)
            { if (__state.attached && !__instance.attached) SessionLog.Event("joint-broken", __state.net, __state.parent, result: __state.joint ? "detach" : "joint absent or parented", sample: true); }
        }
    }
}
