using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        internal static void OverlaySelect(OverlayNearest<Ent> nearest, Vector3 eye)
        {
            nearest.Clear();
            foreach (var e in ByNet.Values)
                if (e.Root != null && e.Root.gameObject.activeInHierarchy)
                    nearest.Offer(e, e.NetId, (e.Root.transform.position - eye).sqrMagnitude);
        }
        internal static Ent OverlayLook() => ByNet.TryGetValue(TelemetryNet(_telemetryLook), out var e) ? e : null;
        internal static Transform OverlayFrame(Ent e)
        {
            var limbs = e.Root.car == null && e.Rag != null && (e.Rag.Act == null || e.Rag.Act.ragdoll) ? Limbs(e) : null;
            if (limbs != null)
            {
                foreach (var limb in limbs) if (limb != null && limb != e.Root.transform && limb.GetComponent<Rigidbody>() != null) return limb;
                foreach (var limb in limbs) if (limb != null && limb != e.Root.transform) return limb;
            }
            return e.Root.transform;
        }
        internal static bool OverlaySample(Ent e, out Vector3d p, out Quaternion q)
        {
            p = default; q = Quaternion.identity;
            if (!e.Proxy || e.ReceivedStateEpoch != e.Epoch) return false;
            if (e.PoseLocked && e.LockFrame != 0 && LockedWorld(e, out p, out q)) return true;
            if (e.Ip == null || !e.Ip.Ready) return false;
            if (e.CarriedBy != 0 && ByNet.TryGetValue(e.CarriedBy, out var car) && car.Root != null)
            {
                var ct = car.Root.transform;
                p = mainscript.GlobalFromUnityPos(e.PoseLocked ? ct.position + ct.rotation * (Vector3)e.Ip.LastPos : ct.TransformPoint((Vector3)e.Ip.LastPos));
                q = ct.rotation * e.Ip.LastRot;
                return true;
            }
            p = e.CarriedBy != 0 ? e.CarriedGlobal : e.Ip.LastPos;
            q = e.CarriedBy != 0 ? e.CarriedGlobalRot : e.Ip.LastRot;
            return true;
        }
        internal static string OverlaySendState(Ent e)
        {
            if (e.HitchWaiting) return "waiting for hitch";
            if (OverlayBolted(e) || AttachedCarPart(e)) return "attached (no states)";
            var t = OverlayFrame(e);
            var q = t.rotation;
            if ((CompactGround(e) || CompactFlyer(e)) && !IsHeld(e.Root) && !IsStored(e.Root) && !IsSpraying(e.Root))
                q = Quaternion.Euler(0, q.eulerAngles.y, 0);
            bool changed = (mainscript.GlobalFromUnityPos(t.position) - e.LastSentPos).sqrMagnitude > BandwidthRules.PositionM * BandwidthRules.PositionM ||
                           Quaternion.Angle(q, e.LastSentRot) > BandwidthRules.RotationDeg;
            return WorldOverlayLogic.SendState(e.LockPose.Locked, e.SentAtRest, e.QuietTail.Active, changed);
        }
        // RidesBolted also increments a gameplay counter; inspecting it must not change that counter.
        private static bool OverlayBolted(Ent e) => RidesParent(e) && (e.PartIndex >= 0 || e.Items.Count <= 1 || BoltedGroupRides);
        internal static string OverlayMode(Ent e)
        {
            if (e.TelemetrySnapFrame == Time.frameCount) return "snap";
            if (e.PoseLocked) return "in frame";
            if (OverlayBolted(e) || AttachedCarPart(e) || RidesOwnersContainer(e)) return "rides parent";
            if (e.Physical) return "physical";
            string mode = e.Ip == null || !e.Ip.Ready ? "waiting for sample" : WorldOverlayLogic.Mode(e.Ip.TelemetryMode);
            return e.CarriedBy != 0 ? "in frame / " + mode : mode;
        }
        internal static bool OverlayMoving(Ent e) => WorldOverlayLogic.Moving(e.SettledState != null,
            e.PoseLocked || e.Stored || e.Held || RidesParent(e), e.LastVel.sqrMagnitude > RestSpeed * RestSpeed, e.Ip != null && e.Ip.LastPoseChanged);

        private static bool OverlaySameOwner(Ent child, Ent parent, bool hitch) => !hitch || child != null && !child.HitchWaiting &&
            (parent == null || !parent.HitchWaiting) && !_hitchTransfers.ContainsKey(child.NetId) && !_hitchCommands.ContainsKey(child.NetId);

        private static readonly List<mountStuff> OverlayMounts = new List<mountStuff>(64);
        private static Dictionary<tosaveitemscript, Ent> OverlayOwners;
        private static Ent OverlayEntityOf(tosaveitemscript it) => it != null && OverlayOwners.TryGetValue(it, out var e) ? e : null;
        internal static void OverlayLinks(OverlayNearest<Ent> nearest)
        {
            if (OverlayOwners == null) OverlayOwners = new Dictionary<tosaveitemscript, Ent>();
            OverlayOwners.Clear();
            // Own roots win over old group membership; split members never resolve through their old group.
            foreach (var e in ByNet.Values) if (e.Root != null) OverlayOwners.TryAdd(e.Root, e);
            foreach (var e in ByNet.Values)
                for (int i = 0; i < e.Items.Count; i++)
                    if (e.Items[i] != null && (e.Split == null || !e.Split.Contains(i))) OverlayOwners.TryAdd(e.Items[i], e);
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl != null)
            {
                LocalSeat(out uint seated, out _);
                OverlayRider(pl.transform, MyId, seated != 0 ? seated : _telemetryFooting, seated != 0, null);
            }
            RemotePlayers.OverlayLinks();
            int members = 0, slots = 0, mounts = 0;
            for (int n = 0; n < nearest.Count; n++)
            {
                var e = nearest.Items[n];
                if (e.Root == null) continue;
                if (Mp.Server != null && Server.TryGetValue(e.NetId, out var se) && se.AttachParent != 0 &&
                    ByNet.TryGetValue(se.AttachParent, out var announced) && announced.Root != null)
                {
                    var at = e.Root.attachable;
                    var actual = at != null && at.attached ? OverlayEntityOf(Fixes.SlotOwnerSave.Owner(at)) : null;
                    if (actual != announced)
                        WorldOverlays.Link(e.Root.transform, announced.Root.transform, e.OwnerId, announced.OwnerId, se.Hitch ? "hitch" : "part", OverlaySameOwner(e, announced, se.Hitch), false, e.NetId, announced.NetId);
                }
                OverlayItemLinks(e.Root, e);
                for (int i = 0; i < e.Items.Count && members < 512; i++, members++)
                {
                    var it = e.Items[i];
                    if (it == null || it == e.Root || e.Split != null && e.Split.Contains(i)) continue;
                    OverlayItemLinks(it, e);
                }
                // Inspect the reverse slot reference too: it can survive after the part loses its attachment.
                var list = e.Root.partslotscripts;
                if (list != null) for (int i = 0; i < list.Count && slots < 512; i++, slots++)
                {
                    var slot = list[i];
                    if (slot == null || slot.parts == null) continue;
                    for (int j = 0; j < slot.parts.Count && slots < 512; j++, slots++)
                    {
                        var at = slot.parts[j];
                        if (at == null || at.tosave == null) continue;
                        var child = OverlayEntityOf(at.tosave);
                        WorldOverlays.Link(at.transform, slot.transform, child != null ? child.OwnerId : -1, e.OwnerId,
                            at.utanfuto != null ? "hitch" : "part", OverlaySameOwner(child, e, at.utanfuto != null), at.attached && at.slot == slot, child != null ? child.NetId : 0, e.NetId);
                    }
                }
                if (mounts >= 512) continue;
                OverlayMounts.Clear();
                e.Root.GetComponentsInChildren(true, OverlayMounts);
                for (int i = 0; i < OverlayMounts.Count && mounts < 512; i++, mounts++)
                {
                    var m = OverlayMounts[i];
                    if (m == null) continue;
                    for (int j = 0; j < m.stored.Count && mounts < 512; j++, mounts++)
                    {
                        var p = m.stored[j];
                        if (p == null) continue;
                        var child = p.GetComponent<tosaveitemscript>();
                        var ce = OverlayEntityOf(child);
                        WorldOverlays.Link(p.transform, m.transform, ce != null ? ce.OwnerId : -1, e.OwnerId, "lock", true, p.physlock == m, ce != null ? ce.NetId : 0, e.NetId);
                    }
                    for (int j = 0; j < m.storedcars.Count && mounts < 512; j++, mounts++)
                    {
                        var car = m.storedcars[j];
                        if (car == null || !car.whlocked) continue;
                        var ce = OverlayEntityOf(car.GetComponent<tosaveitemscript>());
                        WorldOverlays.Link(car.transform, m.transform, ce != null ? ce.OwnerId : -1, e.OwnerId, "lock", true, true, ce != null ? ce.NetId : 0, e.NetId);
                    }
                }
            }
            OverlayOwners.Clear(); OverlayMounts.Clear();
        }
        private static void OverlayItemLinks(tosaveitemscript it, Ent e)
        {
            var p = it.P;
            var m = p != null ? p.physlock : null;
            if (m != null)
            {
                var parent = OverlayEntityOf(m.parentTosave);
                bool reciprocal = m.stored.Contains(p);
                bool wire = it != e.Root || !e.Proxy || e.ReceivedStateEpoch != e.Epoch || e.PoseLocked &&
                    (e.LockFrame == 0 || parent != null && parent.NetId == e.LockFrame);
                WorldOverlays.Link(it.transform, m.transform, e.OwnerId, parent != null ? parent.OwnerId : -1, "lock", true, reciprocal && wire, e.NetId, parent != null ? parent.NetId : 0);
            }
            else if (it == e.Root && e.Proxy && e.PoseLocked && e.LockFrame != 0 && ByNet.TryGetValue(e.LockFrame, out var parent) && parent.Root != null)
                WorldOverlays.Link(it.transform, parent.Root.transform, e.OwnerId, parent.OwnerId, "lock", true, false, e.NetId, parent.NetId);
            var at = it.attachable;
            if (at == null || !at.attached) return;
            var owner = Fixes.SlotOwnerSave.Owner(at);
            var pe = OverlayEntityOf(owner);
            var target = at.slot != null ? at.slot.transform : at.point != null ? at.point : owner != null ? owner.transform : null;
            bool back = at.slot == null || at.slot.parts.Contains(at);
            if (Mp.Server != null && it == e.Root && Server.TryGetValue(e.NetId, out var se) && pe != null)
                back &= se.AttachParent == pe.NetId;
            WorldOverlays.Link(it.transform, target, e.OwnerId, pe != null ? pe.OwnerId : -1, at.utanfuto != null ? "hitch" : "part", OverlaySameOwner(e, pe, at.utanfuto != null), back, e.NetId, pe != null ? pe.NetId : 0);
        }
        internal static void OverlayRider(Transform body, int player, uint net, bool seated, Transform actualParent)
        {
            if (net == 0 || body == null || !ByNet.TryGetValue(net, out var car) || car.Root == null) return;
            bool back = !seated || actualParent != null;
            // The local player uses the game's seat directly; remote players retain SeatedOn after placement.
            if (player == MyId) back = true;
            WorldOverlays.Link(body, car.Root.transform, player, car.OwnerId, seated ? "passenger" : "standing", false, back, 0, car.NetId, player);
        }
    }

    public static partial class RemotePlayers
    {
        internal static void OverlayLinks()
        {
            foreach (var rp in All.Values)
                if (rp.Go != null && rp.Go.activeInHierarchy)
                    Entities.OverlayRider(rp.Go.transform, rp.Id, rp.SeatNet != 0 ? rp.SeatNet : rp.Foot.Net, rp.SeatNet != 0, rp.SeatedOn);
        }
    }
}
