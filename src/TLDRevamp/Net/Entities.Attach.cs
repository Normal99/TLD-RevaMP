using System.Collections.Generic;
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
        public const byte AttachSync = 41, DetachSync = 42, AttachReq = 62;
        private static bool _applyingAttach;
        public static long AttachesSent, AttachesApplied, AttachesSame, AttachesFailed, DetachSyncsSent, DetachSyncsApplied;
        public static string AttachFails = "", UnmountTrace = "";

        /// Parented into what it's attached to: its transform follows the parent on every machine, nothing to stream.
        /// An attach or detach of an object whose copy here is in the far store (we're far from it): before v0.66.2 it was
        /// dropped ("nopart" — 15 in the 2026-10-09 playtest's client log) and the copy came back as it was before: a door
        /// bolted back on while we were away was missing, a wheel taken off still on (meetagain.py A1/B1). Now the
        /// stored records change the way the game's own save would have them: the attach record written (the game's
        /// placing loads it), the object's record moved to its parent's; a detach drops the record (DetachStored).
        /// A parent that is loaded here: the part is placed now (the game's GetOne) and attached the normal way.
        public static bool StoredAttach = true;   // A/B: false = before v0.66.2
        public static long AttachesStored, DetachesStored;

        private static bool AttachStored(Ent pe, int attachType, int indexType, int index, uint parentNet, int parentIdx, int poigen, Vector3d poiid, Vector3d lpos, Vector3d gpos)
        {
            var data = savedatascript.s != null ? savedatascript.s.data : null;
            if (data == null || data.itemData == null || pe.RootId == 0 || !savedatascript.IndexOfID(data.itemData.items, pe.RootId, out int k)) return false;
            var list = data.itemData.items;
            uint parentId = 0;
            if (attachType == 1)
            {
                if (!ByNet.TryGetValue(parentNet, out var pa)) return false;
                if (pa.Root != null)
                {
                    // the parent is here: place the part now — the caller then attaches it the normal way
                    if (itemPlaceRemoveScript.s != null && itemPlaceRemoveScript.s.GetOne(pe.RootId, out _)) { PartsPlacedForAttach++; Resolve(pe); }
                    return false;
                }
                parentId = parentIdx == 0 ? pa.RootId : pa.ItemIds != null && parentIdx - 1 < pa.ItemIds.Count ? pa.ItemIds[parentIdx - 1] : 0;
                if (parentId == 0 || !savedatascript.IndexOfID(list, parentId, out int pk)) return false;
                var pt = list[pk].transform;
                MoveRecord(list, k, pt.pos, pt.rot.Load());   // stored with its parent: placed back together
            }
            else if (attachType > 0) MoveRecord(list, k, gpos, list[k].transform.rot.Load());
            data.itemData.attachable.RemoveAll(a => a != null && a.id == pe.RootId);
            data.itemData.attachable.Add(new save_attachable { id = pe.RootId, attachType = attachType, indexType = indexType, index = index, parentid = parentId,
                                                               poigenid = poigen, poiid = poiid, lpos = lpos, gpos = gpos });
            AttachesStored++;
            AttachesApplied++;
            return true;
        }
        public static long PartsPlacedForAttach;

        public static bool ClearHeldOnAttach = true;   // A/B: false = before v0.66.2 (heldattach.py)
        public static long HeldClearedOnAttach;

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
                if (pe == null) return;
                if (pe.Proxy)
                {
                    // the local player bolted a part this machine doesn't simulate (a pickup claim still on its way, or
                    // overtaken: another player's car claimed it as it was set down): the owner does it — a local-only
                    // attach stayed bolted here and loose everywhere else (buglist2m 3b, full sequence)
                    if (AttachRequests && PlayerActing) { SendAttach(pe, __instance, AttachReq); ApplyProxyBodies(pe); pe.Ip = new PoseInterpolator(); }
                    return;
                }
                SendAttach(pe, __instance);
            }
        }

        public static bool AttachRequests = true;   // A/B: false = before v0.66.4 (a copy bolted by the player stays local)
        public static long AttachReqsSent, AttachReqsApplied, AttachReqsRefused;

        private static void SendAttach(Ent pe, attachablescript at, byte type = AttachSync)
        {
            var rec = new save_attachable(at, at.tosave.idInSave);
            if (rec.attachType == 0) return;
            uint parentNet = 0; int parentIdx = 0;
            if (rec.attachType == 1)
            {
                var pt = Fixes.SlotOwnerSave.Owner(at);   // a slot outside its owner's hierarchy (Bus01's rear wheels)
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
            W.Reset(); W.U8(type); W.U32(pe.NetId); W.U8((byte)rec.attachType); W.U8((byte)rec.indexType); W.VarU32((uint)(rec.index + 1));
            W.U32(parentNet); W.VarU32((uint)parentIdx); W.VarU32((uint)(rec.poigenid + 1));
            W.F64(rec.poiid.x); W.F64(rec.poiid.y); W.F64(rec.poiid.z);
            W.F64(rec.lpos.x); W.F64(rec.lpos.y); W.F64(rec.lpos.z);
            W.F64(rec.gpos.x); W.F64(rec.gpos.y); W.F64(rec.gpos.z);
            ToServer(W, true);
            if (type == AttachReq) AttachReqsSent++; else AttachesSent++;
        }

        /// Server: a player bolted a part it doesn't own — to the part's owner (who may be the requester by now: its
        /// claim went through first — then it is a plain attach).
        internal static void ServerAttachReq(int from, NetReader r)
        {
            int start = r.Pos;
            uint partNet = r.U32();
            if (r.Bad || !Server.TryGetValue(partNet, out var se)) return;
            r.Pos = start;
            if (se.OwnerId == from) { ServerAttach(from, r); return; }
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);   // the whole message, its type byte included
            ServerSendTo(se.OwnerId, WS, true);
        }

        /// The owner runs the requested attach and tells everyone (AttachSync, as its own). It can't (the part in its
        /// player's hands, the slot taken, the parent not here): it tells everyone the part is off (DetachSync) — the
        /// requester's local bolt is undone instead of staying different from everyone else's.
        internal static void ApplyAttachReq(NetReader r)
        {
            int start = r.Pos;
            uint partNet = r.U32();
            if (r.Bad || !ByNet.TryGetValue(partNet, out var pe) || !Resolve(pe) || pe.Root == null || pe.Proxy || pe.Root.attachable == null) return;
            r.Pos = start;
            if (!IsHeld(pe.Root))
            {
                long failed = AttachesFailed;
                ApplyAttachSync(r);
                if (AttachesFailed == failed && pe.Root != null && pe.Root.attachable.attached)
                {
                    SendAttach(pe, pe.Root.attachable);
                    AttachReqsApplied++;
                    return;
                }
            }
            AttachReqsRefused++;
            if (pe.Root != null && !pe.Root.attachable.attached) SendDetachSync(pe);
        }

        /// Server: everyone else replays it; bolted onto another player's object, it becomes theirs.
        internal static void ServerAttach(int from, NetReader r)
        {
            uint partNet = r.U32(); byte attachType = r.U8(); r.U8(); r.VarU32(); uint parentNet = r.U32();
            if (r.Bad || !Server.TryGetValue(partNet, out var se) || se.OwnerId != from) return;
            r.Pos = 0; int len = r.End;
            WS.Reset(); WS.Bytes(r.Buf, 0, len);
            ServerSendAll(WS, true, from);
            // kept for whoever gets these objects later: a joiner gets the part's own record (taken when it came off:
            // loose, where it came off) and its states, which leave out parents in other groups (per-machine ids) — the
            // hubcap bolted back by another player lay loose 22 m from the car for a player who joined later (buglist2m 9d)
            KeepAttach(se, attachType == 1 ? parentNet : 0, r.Buf, len);
            // bolted on: in nobody's hands. The owner sends no more states for it (it rides its parent), so the "held" of
            // its last one stayed — and the server refused everyone's claims on it from then on (playtest 2026-10-09:
            // parts locked for both players)
            if (ClearHeldOnAttach && (se.Held || se.Stored)) { se.Held = false; se.Stored = false; HeldClearedOnAttach++; }
            if (attachType == 1 && Server.TryGetValue(parentNet, out var parent) && parent.OwnerId != se.OwnerId)
            {
                OwnerLog(se, parent.OwnerId, "attached onto net " + parentNet);
                se.OwnerId = parent.OwnerId; se.Epoch++;
                W.Reset(); W.U8(Owner); W.U32(partNet); W.VarU32((uint)se.OwnerId); W.U32(se.Epoch);
                ServerSendAll(W, true, -1);
                CascadeOwner(se);
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
            if (ByNet.TryGetValue(partNet, out var pd) && !Resolve(pd) && StoredAttach && AttachStored(pd, attachType, indexType, index, parentNet, parentIdx, poigen, poiid, lpos, gpos))
                return;
            if (!ByNet.TryGetValue(partNet, out var pe) || !Resolve(pe) || pe.Root == null || pe.Root.attachable == null)
            { AttachesFailed++; if (AttachFails.Length < 400) AttachFails += "nopart" + partNet + " "; return; }
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
                if (at.attached && Fixes.SlotOwnerSave.Owner(at) == pt && sameSlot) { AttachesSame++; return; }
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
            if (ClearHeldOnAttach && pe.Held) ShowHeld(pe, false);   // bolted on: no more states come — its copy collides again
            if (ClearHeldOnAttach && pe.Stored) ShowStored(pe, false);
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

        /// Server: an object's owner changed — what is bolted onto it goes along, all the way down (a hubcap on a wheel on
        /// a car). Without it, a part bolted on while another player held the car (a claim, a hand-off) stayed that
        /// player's when the car went back: its owner, 850 m away with the car stored, answered nothing when the car's
        /// owner took it off — loose on one machine, bolted on the other (playround.py R3, 2026-10-09).
        public static bool OwnerCascade = true;   // A/B: false = before v0.66.5
        public static long OwnerCascaded;
        private static void CascadeOwner(SEnt se, int depth = 0)
        {
            if (!OwnerCascade || se.AttachKids == null || depth > 8) return;
            foreach (var kid in new List<uint>(se.AttachKids))
            {
                if (kid == se.NetId || !Server.TryGetValue(kid, out var k) || k.AttachParent != se.NetId || k.OwnerId == se.OwnerId || k.Held || k.Stored) continue;
                OwnerLog(k, se.OwnerId, "with net " + se.NetId + " (bolted onto it)");
                k.OwnerId = se.OwnerId; k.Epoch++; k.CrashReturnTo = -1;
                W.Reset(); W.U8(Owner); W.U32(k.NetId); W.VarU32((uint)k.OwnerId); W.U32(k.Epoch);
                ServerSendAll(W, true, -1);
                OwnerCascaded++;
                CascadeOwner(k, depth + 1);
            }
        }

        private static void KeepAttach(SEnt se, uint parentNet, byte[] msg, int len)
        {
            DropAttach(se);
            se.LastAttach = new byte[len]; System.Buffer.BlockCopy(msg, 0, se.LastAttach, 0, len);
            se.AttachParent = parentNet;
            if (parentNet != 0 && Server.TryGetValue(parentNet, out var pa)) (pa.AttachKids ?? (pa.AttachKids = new System.Collections.Generic.HashSet<uint>())).Add(se.NetId);
            AttachesKept++;
        }

        private static void DropAttach(SEnt se)
        {
            if (se.AttachParent != 0 && Server.TryGetValue(se.AttachParent, out var pa) && pa.AttachKids != null) pa.AttachKids.Remove(se.NetId);
            se.LastAttach = null; se.AttachParent = 0;
        }

        public static long AttachesKept, AttachesReplayed;

        /// SendObject: the object's last attach once what it's attached to is known to that player, and the last attaches
        /// of objects attached onto it that the player knows (after their Add/part messages: the joiner's copy exists).
        private static void ReplayAttaches(int playerId, SEnt se)
        {
            if (se.LastAttach != null && (se.AttachParent == 0 || KnownTo(playerId, se.AttachParent))) SendKept(playerId, se);
            if (se.AttachKids == null) return;
            foreach (var kid in se.AttachKids)
                if (kid != se.NetId && Server.TryGetValue(kid, out var k) && k.LastAttach != null && KnownTo(playerId, kid)) SendKept(playerId, k);
        }

        /// Known to the player: sent itself, or a part that came off an object it was sent (WritePartDetached).
        private static bool KnownTo(int playerId, uint net) =>
            KnownBy(playerId).Contains(net) || (Server.TryGetValue(net, out var s) && s.ParentNet != 0 && KnownBy(playerId).Contains(s.ParentNet));

        private static void SendKept(int playerId, SEnt se)
        {
            WS.Reset(); WS.Bytes(se.LastAttach, 0, se.LastAttach.Length);
            ServerSendTo(playerId, WS, true);
            AttachesReplayed++;
        }

        internal static void ServerDetachSync(int from, NetReader r)
        {
            uint net = r.U32();
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId != from) return;
            DropAttach(se);
            r.Pos = 0; int len = r.End;
            WS.Reset(); WS.Bytes(r.Buf, 0, len);
            ServerSendAll(WS, true, from);
        }

        internal static void ApplyDetachSync(NetReader r)
        {
            uint net = r.U32(); r.U32();
            var pos = new Vector3d(r.F64(), r.F64(), r.F64()); var rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            if (r.Bad || !ByNet.TryGetValue(net, out var pe)) return;
            if (!Resolve(pe) || pe.Root == null)
            {
                // our copy is in the far store: it comes off there (placed back loose where it landed)
                if (StoredAttach && DetachStored(pe.RootId, pos, rot)) { DetachSyncsApplied++; DetachesStored++; }
                return;
            }
            var at = pe.Root.attachable;
            if (at != null && at.attached)
            {
                _applyingDetach = true;
                try { Unmount(at, true); } finally { _applyingDetach = false; }
            }
            pe.Root.transform.SetPositionAndRotation(mainscript.UnityPosFromGlobal(pos), rot);
            if (pe.Proxy) { ApplyProxyBodies(pe); pe.Ip = new PoseInterpolator(); }
            DetachSyncsApplied++;
            HandOver(pe.Root);
        }

        public static string AttachStats() => "{\"attachesStored\":" + AttachesStored + ",\"detachesStored\":" + DetachesStored + ",\"placedForAttach\":" + PartsPlacedForAttach + ",\"heldClearedOnAttach\":" + HeldClearedOnAttach + ",\"sent\":" + AttachesSent + ",\"applied\":" + AttachesApplied + ",\"same\":" + AttachesSame + ",\"failed\":" + AttachesFailed
                                              + ",\"fails\":" + Json.Str(AttachFails) + ",\"detachSent\":" + DetachSyncsSent + ",\"detachApplied\":" + DetachSyncsApplied + ",\"unmountTrace\":" + Json.Str(UnmountTrace) + "}";
    }
}
