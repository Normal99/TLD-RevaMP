using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        // Use the shared ancestor's frame, including a container mounted on a moving part. Unshared parents fall
        // back to world poses, so their motion remains visible to the send decision.
        private static uint LockedFrame(Ent e, mountStuff mount, Transform t, out Vector3 lp, out Quaternion lq, out int item, out int index)
        {
            lp = default; lq = Quaternion.identity; item = index = -1;
            if (mount == null) return 0;
            for (var a = mount.transform; a != null; a = a.parent)
                foreach (var c in ByNet.Values)
                {
                    if (c == e || c.Root == null || c.Root.transform != a) continue;
                    // Attached/worn entities do not stream a world frame. Use world poses unless a streaming ancestor
                    // also owns the mount reference; otherwise a quiet child would inherit a stale server fallback.
                    if (RidesBolted(c) || AttachedCarPart(c) || WornRides(c)) continue;
                    item = c.Items.IndexOf(mount.parentTosave);
                    if (item < 0) continue;
                    var mounts = LockMounts(mount.parentTosave);
                    index = System.Array.IndexOf(mounts, mount);
                    if (index < 0) continue;
                    // Rotation-only offset: the server can resolve it even when the parent is stored out here.
                    lp = Quaternion.Inverse(a.rotation) * (t.position - a.position);
                    lq = Quaternion.Inverse(a.rotation) * t.rotation;
                    if (lp.sqrMagnitude > 1e4f) return 0; // same bound as the wire reader; keep streaming in world space
                    return c.NetId;
                }
            return 0;
        }

        private static mountStuff[] LockMounts(tosaveitemscript item) =>
            System.Array.FindAll(item.GetComponentsInChildren<mountStuff>(true), m => m.parentTosave == item);

        private static void ApplyPoseLock(Ent e, bool locked, uint frame, int item, int mount)
        {
            bool changed = e.PoseLocked != locked || e.LockFrame != frame || e.LockItem != item || e.LockMount != mount;
            if (changed)
                SessionLog.Event(locked ? "physlock-lock" : "physlock-unlock", e.NetId, frame, from: e.OwnerId, epoch: e.Epoch);
            e.PoseLocked = locked; e.LockFrame = frame; e.LockItem = item; e.LockMount = mount;
            EnsurePoseLock(e);
        }

        // Apply the owner's decision, rather than waiting for this machine's container scans/5 m release heuristic.
        // Retrying also covers the item arriving before its parent, and objects placed again by world streaming.
        private static void EnsurePoseLock(Ent e)
        {
            var p = e.Root != null ? e.Root.P : null;
            if (p == null) return;
            mountStuff wanted = null;
            if (e.PoseLocked && e.LockFrame != 0 && ByNet.TryGetValue(e.LockFrame, out var c) && c.Root != null &&
                e.LockItem >= 0 && e.LockItem < c.Items.Count && c.Items[e.LockItem] != null)
            {
                var mounts = LockMounts(c.Items[e.LockItem]);
                if (e.LockMount >= 0 && e.LockMount < mounts.Length) wanted = mounts[e.LockMount];
            }
            if (p.physlock == wanted) return;
            if (e.MovedByBody) StopBodyMotion(e); // a lock destroys the old body; a release must start the new one without a sweep
            if (p.physlock != null) { p.UnPhysicsLock(); ApplyProxyBodies(e); }
            if (wanted == null) return;
            var ct = ByNet[e.LockFrame].Root.transform;
            p.transform.SetPositionAndRotation(ct.position + ct.rotation * e.LockLocalPos, ct.rotation * e.LockLocalRot);
            wanted.Store(p, true);
        }

        private static void ServerLockedPose(SEnt se, int depth = 0)
        {
            if (depth >= 16 || se.LockFrame == 0 || !Server.TryGetValue(se.LockFrame, out var parent) || !parent.HasState) return;
            ServerLockedPose(parent, depth + 1);
            var offset = parent.Rot * se.LockLocalPos;
            se.Pos = parent.Pos + new Vector3d(offset.x, offset.y, offset.z);
            se.Rot = parent.Rot * se.LockLocalRot; se.Where = se.Pos;
        }

        private static bool LockedWorld(Ent e, out Vector3d pos, out Quaternion rot, int depth = 0)
        {
            pos = e.CarriedGlobal; rot = e.CarriedGlobalRot;
            if (depth >= 16 || e.LockFrame == 0 || !ByNet.TryGetValue(e.LockFrame, out var parent)) return false;
            Vector3d pp; Quaternion pq;
            if (parent.Root != null) { pp = mainscript.GlobalFromUnityPos(parent.Root.transform.position); pq = parent.Root.transform.rotation; }
            else if (!parent.PoseLocked || !LockedWorld(parent, out pp, out pq, depth + 1)) { pp = parent.FarPos; pq = parent.FarRot; }
            var offset = pq * e.LockLocalPos;
            pos = pp + new Vector3d(offset.x, offset.y, offset.z); rot = pq * e.LockLocalRot;
            return true;
        }

    }
}
