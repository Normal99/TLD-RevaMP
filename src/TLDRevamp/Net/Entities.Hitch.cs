using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        public const byte HitchRequest = 99, HitchCommand = 100, HitchFreeze = 101, HitchFrozen = 102, HitchResult = 103;
        public static bool HitchTransferTimeout = true;   // A/B: false = wait indefinitely for the old simulator/result
        public static bool HitchEnsureKnown = true;       // A/B: false = rely on ordinary distance-based Add delivery
        public static bool HitchReleaseOnRemove = true;   // A/B: false = keep the deleted car's hitch record
        public static bool HitchTransferredGripPose = true;   // A/B: false = place the drawbar through the old root transform
        /// A/B: false = a loose trailer's copy leaves its drawbar where it was. The drawbar is its own root object on a joint
        /// to the trailer; on a copy both bodies are kinematic, so the joint holds nothing: the trailer followed its owner's
        /// states and the drawbar stayed behind (hitchplayer.py J1: 4.3 m from the hand that picked it up, dropped at once).
        public static bool HitchProxyGripFollows = true;
        public static long ProxyGripsMoved;
        private const float HitchTimeoutS = 5f;
        private static bool _hitchApplying;
        private static readonly HashSet<int> _hitchDeparted = new HashSet<int>();
        private static uint _hitchToken;
        private sealed class HitchOp
        {
            internal uint Car, Trailer, CarEpoch, TrailerEpoch;
            internal int Slot, From;
            internal bool Connect, ReleaseBrake;
            internal float Until;
        }
        private static readonly Dictionary<uint, HitchOp> _hitchOps = new Dictionary<uint, HitchOp>();
        private static readonly Dictionary<uint, HitchAuthority.Transfer> _hitchTransfers = new Dictionary<uint, HitchAuthority.Transfer>();
        private static readonly Dictionary<uint, HitchOp> _hitchCommands = new Dictionary<uint, HitchOp>();

        private static bool HitchTrailer(Ent e) => e != null && e.Root != null && e.Root.attachable != null && e.Root.attachable.utanfuto != null;
        private static bool HitchTrailer(SEnt e)
        {
            if (e.Hitch) return true;
            var rec = EditItemRecord(e, 0);
            var prefab = rec != null && itemdatabase.s != null ? itemdatabase.s.Item(rec.prefabID) : null;
            return e.Hitch = prefab != null && prefab.GetComponent<attachgripscript>() != null;
        }
        private static bool HitchCanJoin(Ent tr, Ent car) => tr.HitchAuthorityOwner == tr.OwnerId && car.HitchAuthorityOwner == car.OwnerId &&
            HitchAuthority.CanJoin(car.OwnerId, tr.OwnerId, MyId, tr.HitchWaiting, car.Proxy, tr.Proxy);
        private static Ent HitchCar(attachablescript at) => at != null && at.slot != null ? EditEntityOf(at.slot.tosaveitem) : null;

        private static void HitchEvent(HitchOp op, string reason)
        {
            var w = new NetWriter(); w.U8(HitchResult); WriteHitch(w, op); w.Str(reason ?? "accepted");
            ServerSendAll(w, true, -1);
        }
        private static void WriteHitch(NetWriter w, HitchOp op)
        {
            w.U32(op.Trailer); w.U32(op.Car); w.U32(op.TrailerEpoch); w.U32(op.CarEpoch);
            w.VarI32(op.Slot); w.VarI32(op.From); w.Bool(op.Connect); w.Bool(op.ReleaseBrake);
        }
        private static HitchOp ReadHitch(NetReader r) => new HitchOp { Trailer = r.U32(), Car = r.U32(), TrailerEpoch = r.U32(), CarEpoch = r.U32(),
            Slot = r.VarI32(), From = r.VarI32(), Connect = r.Bool(), ReleaseBrake = r.Bool() };

        private static void RequestHitch(Ent tr, Ent car, partslotscript slot, bool connect, bool releaseBrake = false)
        {
            var op = new HitchOp { Trailer = tr.NetId, Car = car.NetId, TrailerEpoch = tr.Epoch, CarEpoch = car.Epoch,
                Slot = car.Root.partslotscripts.IndexOf(slot), From = MyId, Connect = connect, ReleaseBrake = releaseBrake };
            SessionLog.Event("hitch-request", op.Trailer, op.Car, from: MyId, to: car.OwnerId, slot: op.Slot, epoch: tr.Epoch, result: connect ? "connect" : "disconnect");
            var w = new NetWriter(); w.U8(HitchRequest); WriteHitch(w, op); ToServer(w, true);
        }

        private static bool HitchAttach(attachablescript at, partslotscript slot)
        {
            if (!InSession || _hitchApplying || _applyingAttach || at == null || at.utanfuto == null) return true;
            if (!PlayerActing) return true;
            var tr = OwnEntity(at.tosave); var car = slot != null ? EditEntityOf(slot.tosaveitem) : null;
            if (tr != null && car != null && PlayerActing) RequestHitch(tr, car, slot, true);
            else SessionLog.Event("hitch-refused", tr != null ? tr.NetId : 0, result: "unshared target or automatic attach");
            return false;
        }

        [HarmonyPatch(typeof(partslotscript), nameof(partslotscript.Craft))]
        private static class HitchCraftHook
        {
            private static bool Prefix(partslotscript __instance, attachablescript p) => HitchAttach(p, __instance);
        }
        [HarmonyPatch(typeof(attachablescript), nameof(attachablescript.Attach), new[] { typeof(partslotscript) })]
        private static class HitchSlotHook
        {
            private static bool Prefix(attachablescript __instance, partslotscript _slot) => HitchAttach(__instance, _slot);
        }
        [HarmonyPatch(typeof(attachablescript), nameof(attachablescript.Attach), new[] { typeof(Transform) })]
        private static class HitchJointHook
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(attachablescript __instance, Transform Ppoint)
            {
                if (!InSession || __instance.utanfuto == null) return true;
                if (!_applyingAttach && !_hitchApplying && PlayerActing) return HitchAttach(__instance, __instance.slot ?? Ppoint.GetComponentInParent<partslotscript>());
                var tr = OwnEntity(__instance.tosave); var car = HitchCar(__instance);
                // Display copies keep the game's attachment record and lights, but never a physics joint.
                if (tr != null && car != null && HitchCanJoin(tr, car)) return true;
                __instance.attached = true; __instance.point = Ppoint;
                if (__instance.slot != null && !__instance.slot.parts.Contains(__instance)) __instance.slot.parts.Add(__instance);
                __instance.utanfuto.RefreshCenterOffMass(true, Ppoint.GetComponentInParent<rearlights>());
                HitchRemoveJoint(__instance);
                if (tr != null && tr.Proxy) ApplyProxyBodies(tr);
                return false;
            }
        }

        private static bool HitchDetach(attachablescript at)
        {
            if (!InSession || _hitchApplying || _applyingDetach || at == null || at.utanfuto == null) return true;
            var tr = OwnEntity(at.tosave); var car = HitchCar(at);
            if (tr == null || car == null) return true;
            if (PlayerActing) { RequestHitch(tr, car, at.slot, false); return false; }
            return HitchCanJoin(tr, car);
        }

        private static void ServerHitchRequest(int from, NetReader r)
        {
            var op = ReadHitch(r); op.From = from;
            if (r.Bad) return;
            bool known = Server.TryGetValue(op.Trailer, out var tr) && Server.ContainsKey(op.Car);
            Server.TryGetValue(op.Car, out var car);
            string why = HitchAuthority.Refusal(known, known && HitchTrailer(tr), known && car.Car,
                known && (!EditRangeCheck || from == 0 || AtObject(from, tr) && AtObject(from, car)),
                known && (op.TrailerEpoch != tr.Epoch || op.CarEpoch != car.Epoch), known && (tr.Stored || tr.Held && tr.OwnerId != from), false,
                known && op.Connect && (tr.HitchCar != 0 || tr.AttachParent != 0));
            if (why == null && (op.Slot < 0 || op.Slot > 4096 || op.Car == op.Trailer)) why = "slot index";
            if (why == null && !op.Connect && tr.HitchCar != op.Car && tr.AttachParent != op.Car) why = "not hitched to car";
            if (why == null && _hitchOps.ContainsKey(op.Trailer)) why = "hitch pending";
            SessionLog.Event("hitch-request", op.Trailer, op.Car, from: from, to: car != null ? car.OwnerId : -1, slot: op.Slot, result: op.Connect ? "connect" : "disconnect");
            if (why != null) { HitchEvent(op, why); return; }
            tr.HitchCar = op.Car; tr.HitchSlot = op.Slot;
            _hitchOps[tr.NetId] = op;
            if (tr.OwnerId != car.OwnerId) BeginHitchTransfer(tr, car.OwnerId);
            else SendHitchCommand(tr);
        }

        private static void SendHitchCommand(SEnt tr)
        {
            if (!_hitchOps.TryGetValue(tr.NetId, out var op) || !Server.TryGetValue(op.Car, out var car)) return;
            if (tr.OwnerId != car.OwnerId) { BeginHitchTransfer(tr, car.OwnerId); return; }
            HitchOwnerKnows(tr, car.OwnerId);
            op.Until = Time.realtimeSinceStartup + HitchTimeoutS;
            op.CarEpoch = car.Epoch; op.TrailerEpoch = tr.Epoch;
            var w = new NetWriter(); w.U8(HitchCommand); WriteHitch(w, op); ServerSendTo(car.OwnerId, w, true);
        }

        private static void BeginHitchTransfer(SEnt tr, int to)
        {
            if (_hitchTransfers.TryGetValue(tr.NetId, out var pending) && pending.To == to && pending.Epoch == tr.Epoch && !_hitchDeparted.Contains(tr.OwnerId)) return;
            if (++_hitchToken == 0) ++_hitchToken;
            var transfer = new HitchAuthority.Transfer { From = tr.OwnerId, To = to, Epoch = tr.Epoch, Token = _hitchToken,
                Until = Time.realtimeSinceStartup + HitchTimeoutS };
            _hitchTransfers[tr.NetId] = transfer;
            var w = new NetWriter(); w.U8(HitchFreeze); w.U32(tr.NetId); w.U32(tr.Epoch); w.U32(transfer.Token);
            if (_hitchDeparted.Contains(tr.OwnerId))
            {
                w = new NetWriter(); w.U8(HitchFrozen); w.U32(tr.NetId); w.U32(tr.Epoch); w.U32(transfer.Token); w.Bool(false);
                var r = new NetReader { Buf = w.Buf, Pos = 1, End = w.Len }; ServerHitchFrozen(tr.OwnerId, r);
                return;
            }
            ServerSendTo(tr.OwnerId, w, true);
        }

        private static void ApplyHitchFreeze(NetReader r)
        {
            uint net = r.U32(), epoch = r.U32(), token = r.U32();
            if (!r.Bad && token == 0 && ByNet.TryGetValue(net, out var preparing) && epoch >= preparing.Epoch)
            {
                preparing.HitchWaiting = true;
                HitchRemoveJoint(preparing.Root != null ? preparing.Root.attachable : null);
                HitchFreezeBodies(preparing);
                return;
            }
            if (r.Bad || !ByNet.TryGetValue(net, out var tr) || tr.Epoch != epoch || tr.OwnerId != MyId) return;
            Resolve(tr);
            var at = tr.Root != null ? tr.Root.attachable : null;
            var rb = tr.Root != null ? tr.Root.GetComponent<Rigidbody>() : null;
            var vel = rb != null && !rb.isKinematic ? rb.velocity : tr.LastVel;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl != null && tr.Root != null && pl.pickedUp == tr.Root.P) pl.Drop();
            var w = new NetWriter(); w.U8(HitchFrozen); w.U32(net); w.U32(epoch); w.U32(token); w.Bool(tr.Root != null);
            if (tr.Root != null)
            {
                var p = mainscript.GlobalFromUnityPos(tr.Root.transform.position); var q = tr.Root.transform.rotation;
                w.F64(p.x); w.F64(p.y); w.F64(p.z); WriteRot(w, q);
                w.F32(vel.x); w.F32(vel.y); w.F32(vel.z);
                w.Bool(at != null);
                var lp = at != null ? tr.Root.transform.InverseTransformPoint(at.transform.position) : Vector3.zero;
                w.F32(lp.x); w.F32(lp.y); w.F32(lp.z);
                WriteRot(w, at != null ? Quaternion.Inverse(q) * at.transform.rotation : Quaternion.identity);
            }
            tr.HitchWaiting = true; tr.HitchUntil = Time.time + 1f;
            HitchRemoveJoint(at); HitchFreezeBodies(tr);
            SessionLog.Event("hitch-frozen", net, from: MyId, epoch: epoch);
            ToServer(w, true);
        }

        private static void ServerHitchFrozen(int from, NetReader r)
        {
            uint net = r.U32(), epoch = r.U32(), token = r.U32(); bool loaded = r.Bool();
            var p = loaded ? new Vector3d(r.F64(), r.F64(), r.F64()) : default;
            var q = loaded ? ReadRot(r) : Quaternion.identity;
            var v = loaded ? new Vector3(r.F32(), r.F32(), r.F32()) : Vector3.zero;
            bool hasGrip = loaded && r.Bool();
            var gripPos = loaded ? new Vector3(r.F32(), r.F32(), r.F32()) : Vector3.zero;
            var gripRot = loaded ? ReadRot(r) : Quaternion.identity;
            if (r.Bad || !Finite(p) || !Finite(q) || !Finite(v) || !Finite(gripPos) || !Finite(gripRot) || !Server.TryGetValue(net, out var tr) || tr.Epoch != epoch || tr.OwnerId != from ||
                !_hitchTransfers.TryGetValue(net, out var transfer) || !transfer.Confirm(from, epoch, token)) return;
            CompleteHitchTransfer(tr, loaded, p, q, v, hasGrip, gripPos, gripRot);
        }

        private static void HitchOwnerKnows(SEnt tr, int target)
        {
            if (!HitchEnsureKnown || target == 0) return;   // the host already has the world's records
            // Control messages bypass interest. Their Add dependencies must do so too, before the ownership fence.
            if (Server.TryGetValue(tr.HitchCar, out var car) && !KnownBy(target).Contains(car.NetId)) SendObject(target, car);
            if (!KnownBy(target).Contains(tr.NetId)) SendObject(target, tr);
        }

        private static void CompleteHitchTransfer(SEnt tr, bool loaded, Vector3d p, Quaternion q, Vector3 v,
            bool hasGrip, Vector3 gripPos, Quaternion gripRot)
        {
            uint net = tr.NetId;
            int from = tr.OwnerId;
            bool cancelled = tr.HitchCar == 0 || !Server.ContainsKey(tr.HitchCar);
            int target = cancelled ? (_hitchDeparted.Contains(from) ? 0 : from) : Server[tr.HitchCar].OwnerId;
            _hitchTransfers.Remove(net);
            HitchOwnerKnows(tr, target);
            OwnerLog(tr, target, "hitch follows net " + tr.HitchCar);
            var w = new NetWriter(); w.U8(HitchFreeze); w.U32(net); w.U32(tr.Epoch + 1); w.U32(0);
            ServerSendTo(target, w, true);
            tr.OwnerId = target; tr.Epoch++; tr.CrashReturnTo = -1;
            w = new NetWriter(); w.U8(Owner); w.U32(net); w.VarU32((uint)target); w.U32(tr.Epoch);
            ServerSendAll(w, true, -1);
            // Normally the old simulator is frozen; a timeout instead fences its late packets with the new epoch.
            // The last known pose goes reliably after the grant so the recipient can always leave HitchWaiting.
            w = new NetWriter(); w.U8(HitchFrozen); w.U32(net); w.U32(tr.Epoch); w.U32(0); w.Bool(loaded || tr.HasState);
            if (loaded || tr.HasState)
            {
                if (!loaded) { p = tr.Pos; q = tr.Rot; v = Vector3.zero; }
                w.F64(p.x); w.F64(p.y); w.F64(p.z); WriteRot(w, q); w.F32(v.x); w.F32(v.y); w.F32(v.z);
                w.Bool(hasGrip);
                w.F32(gripPos.x); w.F32(gripPos.y); w.F32(gripPos.z); WriteRot(w, gripRot);
            }
            SessionLog.Event("hitch-ownership-claim", net, tr.HitchCar, from: from, to: target, epoch: tr.Epoch);
            ServerSendTo(target, w, true);
            CascadeOwner(tr);
            if (cancelled)
            {
                if (_hitchOps.TryGetValue(net, out var op))
                {
                    _hitchOps.Remove(net);
                    HitchEvent(op, !op.Connect && tr.AttachParent == 0 ? null : "hitch changed during transfer");
                }
            }
            else SendHitchCommand(tr);
        }

        private static void ApplyHitchFrozen(NetReader r)
        {
            uint net = r.U32(), epoch = r.U32(); r.U32(); bool loaded = r.Bool();
            var p = loaded ? new Vector3d(r.F64(), r.F64(), r.F64()) : default;
            var q = loaded ? ReadRot(r) : Quaternion.identity;
            var v = loaded ? new Vector3(r.F32(), r.F32(), r.F32()) : Vector3.zero;
            bool hasGrip = loaded && r.Bool();
            var gripPos = loaded ? new Vector3(r.F32(), r.F32(), r.F32()) : Vector3.zero;
            var gripRot = loaded ? ReadRot(r) : Quaternion.identity;
            if (r.Bad || !ByNet.TryGetValue(net, out var tr) || tr.Proxy || tr.Epoch != epoch || !Finite(p) || !Finite(q) || !Finite(v) || !Finite(gripPos) || !Finite(gripRot)) return;
            if (Resolve(tr) && tr.Root != null && loaded)
            {
                var bodies = new List<Rigidbody>(HitchBodies(tr));
                var poses = new List<Vector3>(); var rotations = new List<Quaternion>();
                var origin = tr.Root.transform.position; var turn = q * Quaternion.Inverse(tr.Root.transform.rotation);
                var destination = mainscript.UnityPosFromGlobal(p);
                var grip = tr.Root.GetComponent<attachgripscript>();
                if (!hasGrip && HitchTransferredGripPose && grip != null && grip.GripPos != null)
                {
                    // Timeout/unloaded acknowledgements have no drawbar pose: move the prefab's grip with the root too.
                    hasGrip = true; gripPos = tr.Root.transform.InverseTransformPoint(grip.GripPos.position);
                    gripRot = Quaternion.Inverse(tr.Root.transform.rotation) * grip.GripPos.rotation;
                }
                var gripDestination = destination + turn * (tr.Root.transform.TransformPoint(gripPos) - origin);
                foreach (var rb in bodies) { poses.Add(destination + turn * (rb.position - origin)); rotations.Add(turn * rb.rotation); }
                for (int i = 0; i < bodies.Count; i++) { bodies[i].position = poses[i]; bodies[i].rotation = rotations[i]; }
                // Rigidbody.position need not update Transform immediately. Compute from the received root pose.
                if (hasGrip && tr.Root.attachable != null) tr.Root.attachable.transform.SetPositionAndRotation(
                    HitchTransferredGripPose ? gripDestination : tr.Root.transform.TransformPoint(gripPos), q * gripRot);
            }
            var g = tr.Root != null ? tr.Root.GetComponent<attachgripscript>() : null;
            if (!hasGrip && g != null && g.GripPos != null && tr.Root.attachable != null)
                tr.Root.attachable.transform.SetPositionAndRotation(g.GripPos.position, g.GripPos.rotation);
            tr.LastVel = v; tr.HitchWaiting = false;
            HitchThaw(tr);
            HitchRestore(tr);
            SessionLog.Event("hitch-ownership-claim", net, from: MyId, epoch: epoch, result: "reliable pose");
        }

        private static void ApplyHitchCommand(NetReader r)
        {
            var op = ReadHitch(r); op.Until = Time.realtimeSinceStartup + HitchTimeoutS;
            if (!r.Bad) _hitchCommands[op.Trailer] = op;
        }
        private static void HitchCommandTick()
        {
            foreach (var kv in new List<KeyValuePair<uint, HitchOp>>(_hitchCommands))
            {
                var op = kv.Value;
                if (HitchTransferTimeout && Time.realtimeSinceStartup >= op.Until) { _hitchCommands.Remove(kv.Key); continue; }
                if (!ByNet.TryGetValue(op.Trailer, out var tr) || !ByNet.TryGetValue(op.Car, out var car)) continue;
                if (tr.HitchWaiting) continue;
                _hitchCommands.Remove(kv.Key);
                string why = null;
                if (tr.Epoch != op.TrailerEpoch || car.Epoch != op.CarEpoch || !HitchCanJoin(tr, car)) why = "owner changed";
                else if (!Resolve(tr) || !Resolve(car) || !HitchTrailer(tr) || car.Root == null) why = "not loaded";
                else if (op.Slot < 0 || op.Slot >= car.Root.partslotscripts.Count) why = "slot index";
                try
                {
                    if (why == null)
                    {
                        var at = tr.Root.attachable; var slot = car.Root.partslotscripts[op.Slot];
                        if (op.Connect && (slot == null || at.attached && at.slot != slot || !at.attached && (slot.hasPart() || !slot.CanCraft(at)))) why = "slot taken or incompatible";
                        else if (!op.Connect && (!at.attached || at.slot != slot)) why = "not hitched to car";
                        else
                        {
                            _hitchApplying = true;
                            try
                            {
                                if (op.Connect) { if (at.attached) HitchRestore(tr); else { HitchAlign(tr, slot); at.Attach(slot); } }
                                else Unmount(at, false);
                            }
                            finally { _hitchApplying = false; }
                            if (at.attached != op.Connect) why = "joint change failed";
                            if (why == null && op.Connect && op.ReleaseBrake && at.utanfuto.brake != null) at.utanfuto.brake.currentTurnState = 0;
                        }
                    }
                }
                catch (System.Exception) { why = "joint change failed"; }
                if (why == "joint change failed" && op.Connect && tr.Root != null)
                {
                    var at = tr.Root.attachable;
                    HitchRemoveJoint(at);
                    if (at != null)
                    {
                        _hitchApplying = true; _applyingDetach = true;
                        try { if (at.attached) Unmount(at, true); }
                        catch (System.Exception) { }
                        finally { _hitchApplying = false; _applyingDetach = false; }
                        if (at.slot != null) at.slot.parts.Remove(at);
                        at.slot = null; at.attached = false;
                        SendDetachSync(tr);
                    }
                }
                var w = new NetWriter(); w.U8(HitchResult); WriteHitch(w, op); w.Str(why ?? "accepted"); ToServer(w, true);
            }
        }

        private static void ServerHitchResult(int from, NetReader r)
        {
            var op = ReadHitch(r); string why = r.Str();
            if (r.Bad || !Server.TryGetValue(op.Trailer, out var tr) || !_hitchOps.TryGetValue(op.Trailer, out var pending) ||
                tr.OwnerId != from || pending.Car != op.Car || pending.Slot != op.Slot || pending.Connect != op.Connect ||
                pending.CarEpoch != op.CarEpoch || pending.TrailerEpoch != op.TrailerEpoch) return;
            if (_hitchTransfers.ContainsKey(op.Trailer)) return;
            op.From = pending.From;
            if (why != "accepted" && why != "owner changed" && why != "not loaded" && why != "slot index" &&
                why != "slot taken or incompatible" && why != "not hitched to car") why = "joint change failed";
            _hitchOps.Remove(tr.NetId);
            if (why != "accepted" && tr.AttachParent == 0) tr.HitchCar = 0;
            HitchEvent(op, why == "accepted" ? null : why);
        }

        private static void ApplyHitchResult(NetReader r)
        {
            var op = ReadHitch(r); string why = r.Str();
            if (!r.Bad) SessionLog.Event(why == "accepted" ? "hitch-accept" : "hitch-refuse", op.Trailer, op.Car, from: op.From, to: MyId,
                slot: op.Slot, epoch: op.TrailerEpoch, count: op.Connect ? 1 : 0, result: why);
        }

        /// A loose trailer's copy: its drawbar where the trailer's joint holds it on the owner's machine (attachgripscript:
        /// the drawbar's origin on GripPos).
        private static void ProxyGripToTrailer(Ent tr)
        {
            var g = tr.Root.GetComponent<attachgripscript>();
            var at = tr.Root.attachable;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (g == null || g.GripPos == null || at == null || pl != null && pl.pickedUp == tr.Root.P) return;
            var t = at.transform;
            if ((t.position - g.GripPos.position).sqrMagnitude < 0.0025f) return;
            t.SetPositionAndRotation(g.GripPos.position, g.GripPos.rotation);
            var rb = at.GetComponent<Rigidbody>();
            if (rb != null) { rb.position = t.position; rb.rotation = t.rotation; }
            ProxyGripsMoved++;
        }

        private static void HitchRemoveJoint(attachablescript at)
        {
            if (at == null || at.J == null) return;
            // Destroy is deferred until the end of the frame; the old constraint must be gone before roles change.
            Object.DestroyImmediate(at.J); at.J = null;
        }
        private static IEnumerable<Rigidbody> HitchBodies(Ent tr)
        {
            var seen = new HashSet<Rigidbody>();
            for (int i = 0; i < tr.Items.Count; i++)
            {
                if (tr.Split != null && tr.Split.Contains(i)) continue;
                var it = tr.Items[i];
                if (it != null) foreach (var rb in it.GetComponentsInChildren<Rigidbody>(true)) if (seen.Add(rb)) yield return rb;
            }
            var at = tr.Root != null ? tr.Root.attachable : null;
            var grip = at != null ? at.GetComponent<Rigidbody>() : null;
            if (grip != null && seen.Add(grip)) yield return grip;
        }
        private static void HitchFreezeBodies(Ent tr)
        {
            var root = tr.Root != null ? tr.Root.GetComponent<Rigidbody>() : null;
            if (root != null && !root.isKinematic) tr.LastVel = root.velocity;
            foreach (var rb in HitchBodies(tr))
                if (!rb.isKinematic) { tr.HitchBodies.Add(rb); rb.isKinematic = true; }
        }
        private static void HitchThaw(Ent tr)
        {
            if (tr.Proxy || tr.HitchWaiting) return;
            foreach (var rb in tr.HitchBodies) if (rb != null) { rb.isKinematic = false; rb.velocity = tr.LastVel; }
            tr.HitchBodies.Clear(); tr.SentAtRest = false;
        }
        private static void HitchMove(Ent tr, Vector3 offset, Vector3 velocity)
        {
            var bodies = new List<Rigidbody>(HitchBodies(tr)); var positions = new List<Vector3>();
            foreach (var rb in bodies) positions.Add(rb.position + offset);
            for (int i = 0; i < bodies.Count; i++)
            {
                var rb = bodies[i]; rb.position = positions[i];
                if (!rb.isKinematic) { rb.velocity = velocity; rb.angularVelocity = Vector3.zero; }
            }
        }
        private static void HitchAlign(Ent tr, partslotscript slot)
        {
            var car = EditEntityOf(slot.tosaveitem); var rb = car != null && car.Root != null ? car.Root.GetComponent<Rigidbody>() : null;
            var velocity = rb != null ? rb.GetPointVelocity(slot.T.position) : Vector3.zero;
            HitchMove(tr, slot.T.position - tr.Root.attachable.transform.position, velocity);
            tr.LastVel = velocity; tr.HitchHasVelocity = false; tr.HitchUntil = Time.time + 1f;
            if (car != null) { car.HitchUntil = tr.HitchUntil; if (car.Root.car != null) DamageGraceUntil[car.Root.car] = tr.HitchUntil; }
        }
        private static void HitchRestore(Ent tr)
        {
            if (!HitchTrailer(tr) || tr.HitchWaiting || tr.Proxy) return;
            var at = tr.Root.attachable; var car = HitchCar(at);
            if (!at.attached || at.J != null || car == null || !HitchCanJoin(tr, car)) return;
            _hitchApplying = true; _applyingAttach = true;
            try { HitchAlign(tr, at.slot); at.Attach(at.point); }
            finally { _applyingAttach = false; _hitchApplying = false; }
        }
        private static void HitchBeforeOwner(Ent e, int owner, uint epoch)
        {
            if (e.OwnerId == owner && e.Epoch == epoch) return;
            e.HitchHasVelocity = false;
            foreach (var tr in ByNet.Values)
            {
                if (!HitchTrailer(tr)) continue;
                if (tr != e && HitchCar(tr.Root.attachable) != e) continue;
                HitchRemoveJoint(tr.Root.attachable);
                tr.HitchUntil = e.HitchUntil = Time.time + 1f;
                if (e.Root != null && e.Root.car != null) DamageGraceUntil[e.Root.car] = e.HitchUntil;
                if (tr != e && tr.OwnerId == MyId && owner != MyId) { tr.HitchWaiting = true; HitchFreezeBodies(tr); }
            }
        }
        private static void CascadeHitches(SEnt car)
        {
            foreach (var tr in new List<SEnt>(Server.Values))
                if (tr.HitchCar == car.NetId && tr.OwnerId != car.OwnerId) BeginHitchTransfer(tr, car.OwnerId);
        }
        private static void HitchReset(bool wasHost)
        {
            foreach (var e in ByNet.Values) if (wasHost) e.OwnerId = e.HitchAuthorityOwner = MyId;
            foreach (var tr in ByNet.Values)
            {
                if (!HitchTrailer(tr)) continue;
                tr.HitchWaiting = false;
                if (wasHost) { HitchThaw(tr); HitchRestore(tr); }
            }
        }
        private static void HitchTick()
        {
            ServerHitchTick();
            HitchCommandTick();
            HitchInitialTick();
            foreach (var tr in ByNet.Values)
            {
                if (!HitchTrailer(tr)) continue;
                var at = tr.Root.attachable;
                if (tr.Proxy || tr.HitchWaiting)
                {
                    HitchRemoveJoint(at); HitchFreezeBodies(tr);
                    if (tr.Proxy && at.attached && at.point != null) at.transform.position = at.point.position;
                    else if (tr.Proxy && !at.attached && HitchProxyGripFollows) ProxyGripToTrailer(tr);
                }
                else { HitchThaw(tr); HitchRestore(tr); }
            }
        }

        private static void ServerHitchTick()
        {
            if (!IsHost || !HitchTransferTimeout || _hitchTransfers.Count == 0 && _hitchOps.Count == 0) return;
            foreach (var kv in new List<KeyValuePair<uint, HitchAuthority.Transfer>>(_hitchTransfers))
            {
                if (Time.realtimeSinceStartup < kv.Value.Until) continue;
                if (!Server.TryGetValue(kv.Key, out var tr)) { _hitchTransfers.Remove(kv.Key); continue; }
                SessionLog.Event("hitch-frozen", tr.NetId, from: tr.OwnerId, result: "freeze timeout; server pose");
                // A silent or unloaded owner cannot acknowledge. Grant with a fresh epoch and the server's last pose.
                CompleteHitchTransfer(tr, false, default, Quaternion.identity, Vector3.zero, false, Vector3.zero, Quaternion.identity);
            }
            foreach (var op in new List<HitchOp>(_hitchOps.Values))
                if (!_hitchTransfers.ContainsKey(op.Trailer) && Time.realtimeSinceStartup >= op.Until && Server.TryGetValue(op.Trailer, out var tr))
                    ReleaseHitch(tr, "hitch command timeout");
        }

        private static void ReleaseHitch(SEnt tr, string reason)
        {
            if (_hitchOps.TryGetValue(tr.NetId, out var op)) { _hitchOps.Remove(tr.NetId); HitchEvent(op, reason); }
            DropAttach(tr);   // also removes LastAttach and the parent's child entry, including for future joiners
            // Invalidate queued commands and late acknowledgements, and release either simulator's frozen bodies.
            CompleteHitchTransfer(tr, false, default, Quaternion.identity, Vector3.zero, false, Vector3.zero, Quaternion.identity);
            var w = new NetWriter(); w.U8(DetachSync); w.U32(tr.NetId); w.U32(tr.Epoch);
            var p = tr.HasState ? tr.Pos : tr.Where; var q = tr.HasState ? tr.Rot : Quaternion.identity;
            w.F64(p.x); w.F64(p.y); w.F64(p.z); WriteRot(w, q);
            ServerSendAll(w, true, -1);   // loaded and far-store copies use the ordinary clean unmount path
        }

        private static void RemoveHitches(uint net)
        {
            if (!HitchReleaseOnRemove) return;
            foreach (var tr in new List<SEnt>(Server.Values))
                if (tr.NetId != net && (tr.HitchCar == net || tr.Hitch && tr.AttachParent == net)) ReleaseHitch(tr, "tow car removed");
            _hitchTransfers.Remove(net); _hitchOps.Remove(net); _hitchCommands.Remove(net);
        }

        private static void HitchInitialTick()
        {
            foreach (var tr in ByNet.Values)
            {
                if (tr.Proxy || tr.HitchAuthorityOwner != tr.OwnerId || !HitchTrailer(tr)) continue;
                var at = tr.Root.attachable; var car = HitchCar(at);
                if (!at.attached || car == null || car.HitchAuthorityOwner != car.OwnerId || !tr.HitchAnnounced.Add(car.NetId)) continue;
                // A saved hitch predates the session: register it once both independent groups have ids.
                SendAttach(tr, at);
            }
        }

        private static void HitchPhysicsTelemetry()
        {
            foreach (var e in ByNet.Values)
            {
                if (e.Proxy || e.Root == null) continue;
                var car = HitchTrailer(e) ? HitchCar(e.Root.attachable) : null;
                if (car == null && Time.time >= e.HitchUntil) continue;
                var rb = e.Root.GetComponent<Rigidbody>(); if (rb == null || rb.isKinematic) continue;
                var v = rb.velocity;
                if (e.HitchHasVelocity && ((v - e.HitchVelocity).sqrMagnitude > 625f || v.sqrMagnitude > 22500f))
                    SessionLog.Event("speed-anomaly", e.NetId, car != null ? car.NetId : 0, epoch: e.Epoch,
                        value: (v - e.HitchVelocity).magnitude, result: Time.time < e.HitchUntil ? "hitch handover" : "hitched trailer");
                e.HitchVelocity = v; e.HitchHasVelocity = true;
            }
        }

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.FallDamage))]
        private static class HitchFallGuard
        {
            private static bool Prefix(fpscontroller __instance)
            {
                if (!InSession || mainscript.s == null || mainscript.s.player != __instance || __instance.RB == null) return true;
                float speed = __instance.RB.velocity.magnitude;
                if (Mathf.Abs(speed - __instance.lastVelocity) <= __instance.deathVelocityChange) return true;
                foreach (var e in ByNet.Values)
                {
                    if (e.Root == null || Time.time >= e.HitchUntil || (e.Root.transform.position - __instance.transform.position).sqrMagnitude > 36f) continue;
                    SessionLog.Event("speed-anomaly", e.NetId, from: MyId, epoch: e.Epoch, value: Mathf.Abs(speed - __instance.lastVelocity), result: "hitch player handover");
                    __instance.lastVelocity = __instance.currentVelocity = speed;
                    return false;
                }
                return true;
            }
        }

        public static string TestHitch(uint carNet, uint trailerNet, bool connect)
        {
            if (!ByNet.TryGetValue(carNet, out var car) || !ByNet.TryGetValue(trailerNet, out var tr) || !Resolve(car) || !Resolve(tr) || !HitchTrailer(tr)) return "{\"error\":\"no car or trailer\"}";
            var at = tr.Root.attachable;
            if (connect && at.attached) return "{\"already\":true}";
            partslotscript slot = at.attached ? at.slot : null;
            if (connect) foreach (var s in car.Root.partslotscripts) if (s != null && s.T != null && !s.hasPart() && s.CanCraft(at)) { slot = s; break; }
            if (slot == null) return "{\"error\":\"no hitch slot\"}";
            RequestHitch(tr, car, slot, connect, releaseBrake: true);
            if (car.OwnerId == MyId && tr.OwnerId == MyId && !tr.HitchWaiting) HitchCommandTick();
            if (connect && at.attached) return "{\"requested\":true,\"hitched\":" + Json.Str(slot.name) + "}";
            return "{\"requested\":true}";
        }
        public static string TestHitchState(uint net)
        {
            if (!ByNet.TryGetValue(net, out var tr) || !Resolve(tr) || !HitchTrailer(tr)) return "{\"error\":\"no trailer\"}";
            var at = tr.Root.attachable; var car = HitchCar(at);
            return "{\"attached\":" + (at.attached ? "true" : "false") + ",\"car\":" + (car != null ? car.NetId : 0) + ",\"owner\":" + tr.OwnerId +
                ",\"epoch\":" + tr.Epoch + ",\"waiting\":" + (tr.HitchWaiting ? "true" : "false") + ",\"joint\":" + (at.J != null ? "true" : "false") +
                ",\"trailerProxy\":" + (tr.Proxy ? "true" : "false") + ",\"carProxy\":" + (car == null || car.Proxy ? "true" : "false") + "}";
        }
    }
}
