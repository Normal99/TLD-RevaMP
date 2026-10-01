using UnityEngine;

namespace TLDRevamp.Net
{
    /// Attaching a shared object to something — bolting a part into a car's slot, sticking an item onto another item,
    /// a POI or the world. The game describes every attachment with one record (save_attachable: what it's attached to,
    /// which collider or slot, where) and rebuilds it with save_attachable.Load — the official MP replays attaches the
    /// same way (syncScript.UpdAttachable). Here the attaching machine sends that record with its per-machine save id
    /// translated to (entity, group member); every other machine replays it through the game's loader, so a slot
    /// crafts on everyone's copy with all the game's side effects (installed mesh, mass, colliders, lights).
    ///
    /// Bolted onto someone else's car, the object changes hands to the car's owner: a display copy (kinematic) bolted
    /// onto a live car would pin it, and the car's owner is who simulates it from now on. While parented into what it's
    /// attached to, it moves with it: no pose stream either way.
    public static partial class Entities
    {
        public const byte AttachSync = 41, DetachSync = 42;
        private static bool _applyingAttach;
        public static long AttachesSent, AttachesApplied, AttachesSame, AttachesFailed, DetachSyncsSent, DetachSyncsApplied;
        public static string AttachFails = "", UnmountTrace = "";

        /// Parented into what it's attached to: its transform follows the parent on every machine, nothing to stream.
        internal static bool RidesParent(Ent e)
        {
            var at = e.Root != null ? e.Root.attachable : null;
            return at != null && at.attached && at.parentAtAttach && e.Root.transform.parent != null;
        }

        /// The entity this object is the root of (its own shared object), or null.
        private static Ent OwnEntity(tosaveitemscript ts)
        {
            foreach (var e in ByNet.Values) if (e.Root == ts) return e;
            return null;
        }

        [HarmonyLib.HarmonyPatch(typeof(attachablescript), nameof(attachablescript.Attach), new[] { typeof(Transform) })]
        private static class AttachHook
        {
            /// Every attach overload and slot Craft ends here: the local player (or the game on its behalf) attached
            /// a shared object this machine simulates → tell everyone.
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(attachablescript __instance)
            {
                if (_applyingAttach || _applyingState || !InSession || __instance.tosave == null) return;
                var pe = OwnEntity(__instance.tosave);
                if (pe == null || pe.Proxy) return;
                SendAttach(pe, __instance);
            }
        }

        private static void SendAttach(Ent pe, attachablescript at)
        {
            var rec = new save_attachable(at, at.tosave.idInSave);
            if (rec.attachType == 0) return;
            uint parentNet = 0; int parentIdx = 0;
            if (rec.attachType == 1)
            {
                var pt = at.AttachedToTosave();
                Ent pa = null;
                if (pt != null)
                    foreach (var e in ByNet.Values)
                    {
                        if (e == pe || e.Root == null) continue;
                        if (e.Root == pt) { pa = e; parentIdx = 0; break; }
                        int i = e.Items.IndexOf(pt);
                        if (i >= 0 && pa == null) { pa = e; parentIdx = i + 1; }
                    }
                if (pa == null) { AttachesFailed++; AttachFails += "unshared-parent(" + (pt != null ? pt.name : "?") + ") "; return; }
                parentNet = pa.NetId;
            }
            W.Reset(); W.U8(AttachSync); W.U32(pe.NetId); W.U8((byte)rec.attachType); W.U8((byte)rec.indexType); W.VarU32((uint)(rec.index + 1));
            W.U32(parentNet); W.VarU32((uint)parentIdx); W.VarU32((uint)(rec.poigenid + 1));
            W.F64(rec.poiid.x); W.F64(rec.poiid.y); W.F64(rec.poiid.z);
            W.F64(rec.lpos.x); W.F64(rec.lpos.y); W.F64(rec.lpos.z);
            W.F64(rec.gpos.x); W.F64(rec.gpos.y); W.F64(rec.gpos.z);
            ToServer(W, true);
            AttachesSent++;
        }

        /// Server: everyone else replays it; bolted onto another player's object, it becomes theirs.
        internal static void ServerAttach(int from, NetReader r)
        {
            uint partNet = r.U32(); byte attachType = r.U8(); r.U8(); r.VarU32(); uint parentNet = r.U32();
            if (r.Bad || !Server.TryGetValue(partNet, out var se) || se.OwnerId != from) return;
            r.Pos = 0; int len = r.End;
            WS.Reset(); WS.Bytes(r.Buf, 0, len);
            ServerSendAll(WS, true, from);
            if (attachType == 1 && Server.TryGetValue(parentNet, out var parent) && parent.OwnerId != se.OwnerId)
            {
                se.OwnerId = parent.OwnerId; se.Epoch++;
                W.Reset(); W.U8(Owner); W.U32(partNet); W.VarU32((uint)se.OwnerId); W.U32(se.Epoch);
                ServerSendAll(W, true, -1);
            }
        }

        internal static void ApplyAttachSync(NetReader r)
        {
            uint partNet = r.U32(); int attachType = r.U8(), indexType = r.U8(), index = (int)r.VarU32() - 1;
            uint parentNet = r.U32(); int parentIdx = (int)r.VarU32(), poigen = (int)r.VarU32() - 1;
            var poiid = new Vector3d(r.F64(), r.F64(), r.F64());
            var lpos = new Vector3d(r.F64(), r.F64(), r.F64());
            var gpos = new Vector3d(r.F64(), r.F64(), r.F64());
            if (r.Bad) return;
            if (!ByNet.TryGetValue(partNet, out var pe) || !Resolve(pe) || pe.Root == null || pe.Root.attachable == null)
            { AttachesFailed++; AttachFails += "nopart" + partNet + " "; return; }
            var at = pe.Root.attachable;
            uint parentId = 0;
            if (attachType == 1)
            {
                tosaveitemscript pt = null;
                if (ByNet.TryGetValue(parentNet, out var pa) && Resolve(pa) && pa.Root != null)
                    pt = parentIdx == 0 ? pa.Root : parentIdx - 1 < pa.Items.Count ? pa.Items[parentIdx - 1] : null;
                if (pt == null) { AttachesFailed++; AttachFails += "noparent" + parentNet + "/" + parentIdx + " "; return; }
                parentId = pt.idInSave;
                bool sameSlot = indexType != 2 || (at.slot != null && pt.partslotscripts.IndexOf(at.slot) == index);
                if (at.attached && at.AttachedToTosave() == pt && sameSlot) { AttachesSame++; return; }
            }
            if (at.attached)
            {
                _applyingDetach = true;
                try { Unmount(at, true); } finally { _applyingDetach = false; }
            }
            _applyingAttach = true;
            try { save_attachable.Load(at, indexType, attachType, parentId, poigen, poiid, index, lpos, gpos); }
            finally { _applyingAttach = false; }
            if (!at.attached) { AttachesFailed++; AttachFails += "load" + partNet + " "; return; }
            if (pe.Proxy) { ApplyProxyBodies(pe); pe.Ip = new PoseInterpolator(); }
            AttachesApplied++;
        }

        /// The simulating machine took its own shared object off what it was attached to (a crash, the wrench, a
        /// player's grab): everyone's copy comes off the same way and follows its pose stream again.
        private static void SendDetachSync(Ent pe)
        {
            var t = pe.Root.transform; var g = mainscript.GlobalFromUnityPos(t.position); var q = t.rotation;
            W.Reset(); W.U8(DetachSync); W.U32(pe.NetId); W.U32(pe.Epoch);
            W.F64(g.x); W.F64(g.y); W.F64(g.z); W.F32(q.x); W.F32(q.y); W.F32(q.z); W.F32(q.w);
            ToServer(W, true);
            pe.SentAtRest = false;
            DetachSyncsSent++;
        }

        internal static void ServerDetachSync(int from, NetReader r)
        {
            uint net = r.U32();
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId != from) return;
            r.Pos = 0; int len = r.End;
            WS.Reset(); WS.Bytes(r.Buf, 0, len);
            ServerSendAll(WS, true, from);
        }

        internal static void ApplyDetachSync(NetReader r)
        {
            uint net = r.U32(); r.U32();
            var pos = new Vector3d(r.F64(), r.F64(), r.F64()); var rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            if (r.Bad || !ByNet.TryGetValue(net, out var pe) || !Resolve(pe) || pe.Root == null) return;
            var at = pe.Root.attachable;
            if (at != null && at.attached)
            {
                _applyingDetach = true;
                try { Unmount(at, true); } finally { _applyingDetach = false; }
            }
            pe.Root.transform.SetPositionAndRotation(mainscript.UnityPosFromGlobal(pos), rot);
            if (pe.Proxy) { ApplyProxyBodies(pe); pe.Ip = new PoseInterpolator(); }
            DetachSyncsApplied++;
        }

        public static string AttachStats() => "{\"sent\":" + AttachesSent + ",\"applied\":" + AttachesApplied + ",\"same\":" + AttachesSame + ",\"failed\":" + AttachesFailed
                                              + ",\"fails\":" + Json.Str(AttachFails) + ",\"detachSent\":" + DetachSyncsSent + ",\"detachApplied\":" + DetachSyncsApplied + ",\"unmountTrace\":" + Json.Str(UnmountTrace) + "}";
    }
}
