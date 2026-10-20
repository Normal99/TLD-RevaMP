using UnityEngine;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private sealed class LockFrameCache
        {
            internal mountStuff Mount;
            internal tosaveitemscript Container;
            internal readonly List<Transform> Chain = new List<Transform>();
            internal readonly List<Ent> Candidates = new List<Ent>();
            internal readonly List<bool> Streaming = new List<bool>();
            internal Ent Parent;
            internal Ent AddressParent;
            internal int AddressItem = -1, AddressIndex = -1;
            internal int Item = -1, Index = -1, Count, Revision;
        }
        private static readonly ConditionalWeakTable<Ent, LockFrameCache> LockFrames = new ConditionalWeakTable<Ent, LockFrameCache>();
        private sealed class MountCache { internal mountStuff[] Mounts; }
        private static readonly ConditionalWeakTable<tosaveitemscript, MountCache> ContainerMounts = new ConditionalWeakTable<tosaveitemscript, MountCache>();
        private static int LockBindingRevision;

        private static bool LockChainMatches(LockFrameCache cache, mountStuff mount)
        {
            if (cache.Mount != mount || cache.Container != mount.parentTosave || cache.Count != ByNet.Count || cache.Revision != LockBindingRevision) return false;
            var a = mount.transform;
            foreach (var previous in cache.Chain)
            {
                if (a == null || a != previous) return false;
                a = a.parent;
            }
            if (a != null) return false;
            for (int i = 0; i < cache.Candidates.Count; i++)
            {
                var candidate = cache.Candidates[i];
                if (!ByNet.TryGetValue(candidate.NetId, out var current) || current != candidate || candidate.Root == null ||
                    cache.Streaming[i] != StreamsLockFrame(candidate)) return false;
            }
            var c = cache.Parent;
            return c == null || c.Root != null && !RidesBolted(c) && !AttachedCarPart(c) && !WornRides(c);
        }

        private static bool StreamsLockFrame(Ent e) => !RidesBolted(e) && !AttachedCarPart(e) && !WornRides(e);

        // Use the shared ancestor's frame, including a container mounted on a moving part. Unshared parents fall
        // back to world poses, so their motion remains visible to the send decision.
        private static uint LockedFrame(Ent e, mountStuff mount, Transform t, out Vector3 lp, out Quaternion lq, out int item, out int index)
        {
            lp = default; lq = Quaternion.identity; item = index = -1;
            if (mount == null) return 0;
            var cache = LockFrames.GetValue(e, _ => new LockFrameCache());
            if (!LockChainMatches(cache, mount)) ResolveLockFrame(e, mount, cache);
            var parent = cache.Parent ?? cache.AddressParent;
            if (parent == null || parent.Root == null) return 0;
            item = cache.Parent != null ? cache.Item : cache.AddressItem;
            index = cache.Parent != null ? cache.Index : cache.AddressIndex;
            var a = parent.Root.transform;
            lp = Quaternion.Inverse(a.rotation) * (t.position - a.position);
            lq = Quaternion.Inverse(a.rotation) * t.rotation;
            return lp.sqrMagnitude > 1e4f || cache.Parent == null ? 0 : parent.NetId;
        }

        private static uint WorldLockContainer(Ent e) =>
            LockFrames.TryGetValue(e, out var cache) && cache.AddressParent != null ? cache.AddressParent.NetId : 0;

        private static void ResolveLockFrame(Ent e, mountStuff mount, LockFrameCache cache)
        {
            cache.Mount = mount; cache.Container = mount.parentTosave; cache.Count = ByNet.Count; cache.Revision = LockBindingRevision;
            cache.Parent = null; cache.Item = cache.Index = -1; cache.Chain.Clear(); cache.Candidates.Clear(); cache.Streaming.Clear();
            cache.AddressParent = null; cache.AddressItem = cache.AddressIndex = -1;
            for (var a = mount.transform; a != null; a = a.parent) cache.Chain.Add(a);
            foreach (var a in cache.Chain)
                foreach (var c in ByNet.Values)
                {
                    if (c == e || c.Root == null || c.Root.transform != a) continue;
                    // Attached/worn entities do not stream a world frame. Use world poses unless a streaming ancestor
                    // also owns the mount reference; otherwise a quiet child would inherit a stale server fallback.
                    int item = c.Items.IndexOf(mount.parentTosave);
                    if (item < 0) continue;
                    bool streaming = StreamsLockFrame(c);
                    cache.Candidates.Add(c); cache.Streaming.Add(streaming);
                    var mounts = LockMounts(mount.parentTosave);
                    int index = System.Array.IndexOf(mounts, mount);
                    if (index < 0) continue;
                    if (cache.AddressParent == null) { cache.AddressParent = c; cache.AddressItem = item; cache.AddressIndex = index; }
                    if (!streaming) continue;
                    cache.Parent = c; cache.Item = item; cache.Index = index;
                    return;
                }
        }

        private static mountStuff[] LockMounts(tosaveitemscript item) =>
            ContainerMounts.GetValue(item, it => new MountCache {
                Mounts = System.Array.FindAll(it.GetComponentsInChildren<mountStuff>(true), m => m.parentTosave == it)
            }).Mounts;

        private static void RebindPoseLocks(Ent parent)
        {
            LockBindingRevision++;
            if (parent.Proxy && parent.PoseLocked && parent.ReceivedStateEpoch == parent.Epoch) EnsurePoseLock(parent);
            foreach (var child in ByNet.Values)
                if (child != parent && child.Proxy && child.PoseLocked && child.ReceivedStateEpoch == child.Epoch && child.LockFrame == parent.NetId) EnsurePoseLock(child);
        }

        private static void TickPoseLock(Ent e)
        {
            if (!e.Proxy || !e.PoseLocked || e.LockFrame == 0 || e.ReceivedStateEpoch != e.Epoch || e.Root.P == null) return;
            if (e.PoseLockMount == null || e.Root.P.physlock != e.PoseLockMount) EnsurePoseLock(e);
        }

        // A separately attached container has no streamed frame. Its mount address is only enforced at takeover;
        // an overlap against this machine's newer car pose can miss the owner's valid lock entirely.
        private static void RecoverWorldLock(Ent e)
        {
            var p = e.Root != null ? e.Root.P : null;
            if (!e.PoseLocked || e.LockFrame != 0 || p == null || p.physlock != null) return;
            StopBodyMotion(e);
            if (e.LockContainer != 0 && ByNet.TryGetValue(e.LockContainer, out var c) && c.Root != null &&
                e.LockItem >= 0 && e.LockItem < c.Items.Count && c.Items[e.LockItem] != null)
            {
                var mounts = LockMounts(c.Items[e.LockItem]);
                if (e.LockMount >= 0 && e.LockMount < mounts.Length)
                {
                    var ct = c.Root.transform;
                    p.transform.SetPositionAndRotation(ct.position + ct.rotation * e.LockLocalPos, ct.rotation * e.LockLocalRot);
                    mounts[e.LockMount].Store(p, true);
                    return;
                }
            }
            Physics.SyncTransforms();
            foreach (var m in Object.FindObjectsOfType<mountStuff>())
            {
                if (m.parentTosave == e.Root || m.noPhysicsLock || m.onlyManual || m.col == null || !m.CanStore()) continue;
                m.Scan();
                foreach (var hit in m.cols)
                {
                    if (hit == null) continue;
                    var layer = hit.GetComponent<layerScript>();
                    if ((layer != null && layer.p != null ? layer.p : hit.GetComponentInParent<pickupable>()) == p)
                    {
                        m.Store(p, true);
                        return;
                    }
                }
            }
        }

        private static void ApplyPoseLock(Ent e, bool locked, uint frame, int item, int mount, uint container = 0)
        {
            bool changed = e.PoseLocked != locked || e.LockFrame != frame || e.LockContainer != container || e.LockItem != item || e.LockMount != mount;
            if (changed)
            {
                SessionLog.Event(locked ? "physlock-lock" : "physlock-unlock", e.NetId, frame, from: e.OwnerId, epoch: e.Epoch);
                if (e.Ip != null) e.Ip = new PoseInterpolator();
            }
            e.PoseLocked = locked; e.LockFrame = frame; e.LockContainer = container; e.LockItem = item; e.LockMount = mount;
            if (changed) EnsurePoseLock(e);
        }

        // Apply the owner's decision, rather than waiting for this machine's container scans/5 m release heuristic.
        // Retrying also covers the item arriving before its parent, and objects placed again by world streaming.
        private static void EnsurePoseLock(Ent e)
        {
            var p = e.Root != null ? e.Root.P : null;
            if (p == null) return;
            if (e.PoseLocked && e.LockFrame == 0) return; // world-pose addresses are enforced only at takeover; keep the viewer's scan guard
            mountStuff wanted = null;
            if (e.PoseLocked && e.LockFrame != 0 && ByNet.TryGetValue(e.LockFrame, out var c) && c.Root != null &&
                e.LockItem >= 0 && e.LockItem < c.Items.Count && c.Items[e.LockItem] != null)
            {
                var mounts = LockMounts(c.Items[e.LockItem]);
                if (e.LockMount >= 0 && e.LockMount < mounts.Length) wanted = mounts[e.LockMount];
            }
            e.PoseLockMount = wanted;
            if (p.physlock == wanted) return;
            if (e.MovedByBody) StopBodyMotion(e); // a lock destroys the old body; a release must start the new one without a sweep
            if (p.physlock != null) { p.UnPhysicsLock(); ApplyProxyBodies(e); }
            if (wanted == null) return;
            var ct = ByNet[e.LockFrame].Root.transform;
            p.transform.SetPositionAndRotation(ct.position + ct.rotation * e.LockLocalPos, ct.rotation * e.LockLocalRot);
            wanted.Store(p, true);
            if (e.Proxy) ApplyProxyBodies(e);
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
