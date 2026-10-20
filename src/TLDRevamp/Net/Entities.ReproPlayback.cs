using System;
using System.Text;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        internal static string ReproResolve(int prefab, double x, double y, double z)
        {
            uint net = 0; int count = 0;
            foreach (var e in ByNet.Values)
            {
                if (e.Root == null || e.Root.id != prefab || !e.Root.gameObject.activeInHierarchy) continue;
                var g = mainscript.GlobalFromUnityPos(e.Root.transform.position);
                if ((g - new Vector3d(x, y, z)).sqrMagnitude > 9) continue;
                net = e.NetId; count++;
            }
            return count == 1 ? "{\"net\":" + net + "}" : "{\"error\":\"repro target missing or ambiguous; supply an explicit net map\",\"matches\":" + count + "}";
        }
        private static tosaveitemscript ReproTarget(uint net, int item = 0) => ByNet.TryGetValue(net, out var e) && Resolve(e) &&
            item >= 0 && item < e.Items.Count ? e.Items[item] : null;
        internal static void ReproReleaseHeld() { ForceHeld = null; _keyTestCar = null; ReproDriveEnd(); }
        internal static string ReproAct(ReproRow r)
        {
            var p = mainscript.s != null ? mainscript.s.player : null;
            if (p == null || !InSession) return "{\"error\":\"multiplayer world not ready\"}";
            var it = ReproTarget(r.Net, Math.Max(0, r.Item));
            switch (r.Kind)
            {
                case ReproKind.Pose:
                    if (!p.Bsitting) p.Teleport(mainscript.UnityPosFromGlobal(new Vector3d(r.X, r.Y, r.Z)), new Vector3(0, (float)r.A, 0));
                    p.FyRot = (float)r.B; p.FxRot = (float)r.C;
                    if (p.Th != null) p.Th.localEulerAngles = new Vector3(p.FyRot, p.FxRot, 0);
                    return "{\"ok\":true}";
                case ReproKind.SeatEnter:
                    LocalSeat(out uint seatedNet, out int seatedSlot);
                    return seatedNet == r.Net && seatedSlot == r.Slot ? "{\"ok\":true}" : SitSeat(r.Slot, false, r.Net);
                case ReproKind.SeatLeave:
                    if (!p.Bsitting) return "{\"error\":\"not seated\"}";
                    p.GetOut(p.transform.position + p.transform.right); return "{\"ok\":true}";
                case ReproKind.Pickup:
                    if (it == null || it.P == null) return "{\"error\":\"pickup target absent\"}";
                    p.Pickup(it.P, it.transform.position); ForceHeld = it; return "{\"ok\":true}";
                case ReproKind.Drop: case ReproKind.Throw:
                    if (it == null || p.pickedUp != it.P) return "{\"error\":\"expected held item absent\"}";
                    if (r.Kind == ReproKind.Throw)
                    {
                        var dir = new Vector3((float)r.X, (float)r.Y, (float)r.Z);
                        if (dir.sqrMagnitude < 0.9f || dir.sqrMagnitude > 1.1f || r.A <= p.minThrowForce || r.A > 1) return "{\"error\":\"invalid throw\"}";
                        p.Th.rotation = Quaternion.LookRotation(dir); p.throwForce = (float)r.A;
                    }
                    else p.throwForce = 0;
                    ForceHeld = null; p.Drop(); return "{\"ok\":true}";
                case ReproKind.InventorySwitch: case ReproKind.InventorySwap: case ReproKind.InventoryStore: case ReproKind.InventoryUnstore:
                    if (p.inventory == null || r.Slot < 0 || r.Slot >= p.inventory.Count) return "{\"error\":\"inventory slot absent\"}";
                    ForceHeld = null;
                    if (r.Kind == ReproKind.InventorySwitch) p.InvSwitchTo(r.Slot);
                    else if (r.Kind == ReproKind.InventorySwap) p.InvSwapTo(r.Slot);
                    else if (r.Kind == ReproKind.InventoryStore) p.InvStore(r.Slot); else p.InvUnStore(r.Slot);
                    if (p.pickedUp != null) ForceHeld = p.pickedUp.tosave;
                    return "{\"ok\":true}";
                case ReproKind.RightGrip: ForceHeld = null; p.RightHandGrip(); return "{\"ok\":true}";
                case ReproKind.RightRelease: p.RightHandRelease(true); if (p.pickedUp != null) ForceHeld = p.pickedUp.tosave; return "{\"ok\":true}";
                case ReproKind.Usable: case ReproKind.PoiUsable:
                    usablescript u = null;
                    if (r.Kind == ReproKind.Usable && it != null)
                    { var us = ItemUsables(it); if (r.Slot >= 0 && r.Slot < us.Length) u = us[r.Slot]; }
                    else if (r.Kind == ReproKind.PoiUsable)
                    {
                        u = PoiUsableOf(new PoiKey { Gen = r.Item, X = r.E, Y = r.F, Z = r.G, U = r.Slot });
                    }
                    if (u == null) return "{\"error\":\"usable absent\"}";
                    u.Refresh((float)r.X, (float)r.Y, (float)r.Z, (int)r.A); u.tunedFloat = (float)r.B; u.SyncMulti(true); return "{\"ok\":true}";
                case ReproKind.Ignition:
                    if (r.A == 0) { _keyTestUntil = Time.time - 1; _keyTestCar = null; return "{\"ok\":true}"; }
                    return KeyTest(r.Net, 3600);
                case ReproKind.EntityPose:
                    if (!ByNet.TryGetValue(r.Net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"entity pose target absent\"}";
                    if (e.Proxy || e.OwnerId != MyId) return ReproNotOwner();
                    e.Root.transform.SetPositionAndRotation(mainscript.UnityPosFromGlobal(new Vector3d(r.X, r.Y, r.Z)), Quaternion.Euler((float)r.A, (float)r.B, (float)r.C));
                    return "{\"ok\":true}";
                case ReproKind.Attach: return ReproAttachAction(r);
                case ReproKind.Detach:
                    if (it == null || it.attachable == null) return "{\"error\":\"detach target absent\"}";
                    if (PartIsProxy(it.attachable)) return RequestDetach(it.attachable) ? "{\"requested\":true}" : "{\"error\":\"detach request refused\"}";
                    return Unmount(it.attachable, false) ? "{\"ok\":true}" : "{\"error\":\"unmount refused\"}";
                case ReproKind.PhysLock:
                    var parent = ReproTarget(r.Other, (int)r.A);
                    if (it == null || it.P == null || parent == null || !ByNet.TryGetValue(r.Net, out var own)) return "{\"error\":\"physlock target or mount absent\"}";
                    var mounts = LockMounts(parent);
                    if (r.Slot < 0 || r.Slot >= mounts.Length) return "{\"error\":\"mount absent\"}";
                    if (own.Proxy || own.OwnerId != MyId) return ReproNotOwner();
                    mounts[r.Slot].Store(it.P, false); return "{\"ok\":true}";
                case ReproKind.PhysUnlock:
                    if (it == null || it.P == null || !ByNet.TryGetValue(r.Net, out var unlocked)) return "{\"error\":\"unlock target absent\"}";
                    if (unlocked.Proxy || unlocked.OwnerId != MyId) return ReproNotOwner();
                    it.P.UnPhysicsLock(); return "{\"ok\":true}";
                case ReproKind.Hitch: case ReproKind.Unhitch: return ReproHitchAction(r);
                case ReproKind.Drive: return ReproDriveAction(r);
                default: return "{\"error\":\"unsupported repro action\"}";
            }
        }
        private static string ReproNotOwner() => ReproPlayback.Active ? "{\"ok\":true,\"skipped\":true}" : "{\"error\":\"requires local owner\"}";
        private static string ReproHitchAction(ReproRow r)
        {
            if (!ByNet.TryGetValue(r.Net, out var tr) || !ByNet.TryGetValue(r.Other, out var car) || !Resolve(tr) || !Resolve(car) ||
                !HitchTrailer(tr) || car.Root == null || r.Slot < 0 || r.Slot >= car.Root.partslotscripts.Count)
                return "{\"error\":\"recorded hitch target or slot absent\"}";
            RequestHitch(tr, car, car.Root.partslotscripts[r.Slot], r.Kind == ReproKind.Hitch);
            return "{\"requested\":true}";
        }
        private static string ReproAttachAction(ReproRow r)
        {
            var part = ReproTarget(r.Net); var parent = ReproTarget(r.Other, r.Item == 0 ? 0 : r.Item - 1);
            if (part == null || part.attachable == null) return "{\"error\":\"attach target absent\"}";
            if (r.B == 1 && parent == null) return "{\"error\":\"attach parent absent\"}";
            var p = mainscript.s != null ? mainscript.s.player : null;
            if (p != null && p.pickedUp == part.P) { ForceHeld = null; p.Drop(); }
            if (r.A == 2 && parent != null)
            {
                if (r.Slot < 0 || r.Slot >= parent.partslotscripts.Count) return "{\"error\":\"attach slot absent\"}";
                ForceHeld = null; parent.partslotscripts[r.Slot].Craft(part.attachable); return "{\"ok\":true}";
            }
            var point = mainscript.UnityPosFromGlobal(new Vector3d(r.X, r.Y, r.Z));
            Collider col = parent != null ? parent.GetColliderFromIndex(r.Slot) : null;
            if (parent == null)
            {
                var hits = Physics.OverlapSphere(point, 0.05f);
                foreach (var hit in hits) if (hit != null && hit.GetComponentInParent<tosaveitemscript>() != part) { if (col != null) return "{\"error\":\"ambiguous world attach collider\"}"; col = hit; }
            }
            if (col == null) return "{\"error\":\"attach collider absent\"}";
            if (parent != null) point = parent.transform.TransformPoint(new Vector3((float)r.C, (float)r.D, (float)r.E));
            ForceHeld = null; part.attachable.Attach(part.attachable.NewPoint(col.transform, point, Quaternion.identity), col); return "{\"ok\":true}";
        }
        internal static string ReproCheck(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"repro check target absent\"}";
            var g = mainscript.GlobalFromUnityPos(e.Root.transform.position);
            var sb = new StringBuilder("{\"pos\":[" + Json.Num(g.x) + "," + Json.Num(g.y) + "," + Json.Num(g.z) + "],\"stored\":" + ((e.Proxy ? e.Stored : IsStored(e.Root)) ? "true" : "false") +
                ",\"held\":" + ((e.Proxy ? e.Held : IsHeld(e.Root)) ? "true" : "false") +
                ",\"parent\":" + e.ParentNet + ",\"ignition\":" + (e.Root.car != null && e.Root.car.ignition ? "true" : "false") +
                ",\"engine\":" + (e.Root.car != null && (e.Proxy ? e.EngRunning : e.Root.car.Engine != null && e.Root.car.Engine.running) ? "true" : "false") + ",\"items\":[");
            for (int i = 0; i < e.Items.Count; i++)
            {
                if (i > 0) sb.Append(','); var it = e.Items[i];
                if (it == null) { sb.Append("null"); continue; }
                sb.Append("{\"attached\":").Append(it.attachable != null && it.attachable.attached ? "true" : "false")
                    .Append(",\"locked\":").Append(it.P != null && it.P.physlock != null ? "true" : "false").Append(",\"usables\":[");
                var us = ItemUsables(it);
                for (int j = 0; j < us.Length; j++)
                {
                    if (j > 0) sb.Append(','); var u = us[j]; if (u == null) { sb.Append("null"); continue; }
                    sb.Append('[').Append(Json.Num(u.slideValue)).Append(',').Append(Json.Num(u.xRot)).Append(',').Append(Json.Num(u.yRot)).Append(',').Append(u.currentTurnState).Append(',').Append(Json.Num(u.tunedFloat)).Append(']');
                }
                sb.Append("],\"radio\":");
                if (it.radio == null) sb.Append("null");
                else sb.Append('[').Append(it.radio.ON ? "true" : "false").Append(',').Append(it.radio.amfm ? "true" : "false").Append(',').Append(Json.Num(it.radio.FR)).Append(']');
                sb.Append('}');
            }
            return sb.Append("]}").ToString();
        }
        internal static string ReproPoiCheck(int gen, double x, double y, double z, int index)
        {
            var u = PoiUsableOf(new PoiKey { Gen = gen, X = x, Y = y, Z = z, U = index });
            return u == null ? "{\"error\":\"repro POI usable absent\"}" : "{\"state\":[" + Json.Num(u.slideValue) + "," + Json.Num(u.xRot) + "," + Json.Num(u.yRot) + "," + u.currentTurnState + "," + Json.Num(u.tunedFloat) + "]}";
        }
    }
    public static partial class RemotePlayers
    {
        internal static string ReproState()
        {
            var p = mainscript.s != null ? mainscript.s.player : null;
            if (p == null || All.Count != 1) return "{\"error\":\"repro needs one local player and one peer\"}";
            Entities.LocalSeat(out uint net, out int seat);
            var g = mainscript.GlobalFromUnityPos(p.transform.position);
            string local = ReproPlayer(g, net, seat), remote = "null";
            foreach (var rp in All.Values) if (rp.Has) remote = ReproPlayer(rp.Target, rp.SeatNet, rp.SeatIdx);
            return "{\"local\":" + local + ",\"remote\":" + remote + "}";
        }
        private static string ReproPlayer(Vector3d g, uint net, int seat) => "{\"pos\":[" + Json.Num(g.x) + "," + Json.Num(g.y) + "," + Json.Num(g.z) + "],\"net\":" + net + ",\"seat\":" + seat + "}";
    }
}
