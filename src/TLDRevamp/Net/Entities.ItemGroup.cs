using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        /// A detached member stays in its old group's list: its own entity, then an unsplit membership, wins.
        private static Ent EditEntityOf(tosaveitemscript it)
        {
            if (it == null) return null;
            var own = OwnEntity(it);
            if (own != null) return own;
            foreach (var e in ByNet.Values)
            {
                if (e.Items == null) continue;
                int idx = e.Items.IndexOf(it);
                if (idx >= 0 && (e.Split == null || !e.Split.Contains(idx))) return e;
            }
            return null;
        }

        private static bool PartIsProxy(attachablescript at)
        {
            var e = at != null ? EditEntityOf(at.tosave) : null;
            return e != null ? e.Proxy : IsProxy(at);
        }

        private static save_item EditItemRecord(SEnt se, int item)
        {
            if (item < 0) return null;
            var g = GroupRecord(se);
            if (g == null && item == 0 && se.PartIndex >= 0 && Server.TryGetValue(se.ParentNet, out var parent)) { g = GroupRecord(parent); item = se.PartIndex; }
            return g != null && item < g.items.Count ? g.items[item] : null;
        }

        private static SEnt EditReachEntity(SEnt se)
        {
            for (int depth = 0; depth < 8 && se.AttachParent != 0; depth++)
            {
                if (!Server.TryGetValue(se.AttachParent, out var parent) || parent == se) break;
                se = parent;
            }
            return se;
        }

        /// The same reach and edit validation as other changes on somebody else's object.
        internal static void ServerDetachReq(int from, uint net, int idx)
        {
            if (!Server.TryGetValue(net, out var se)) return;
            string why = idx < 0 || idx > 4096 ? "item index" : null;
            if (why == null && EditValidation && from != 0 && !TakeToken(_editBuckets, from, EditRatePerS, EditBurst)) why = "edit rate";
            if (why == null && EditRangeCheck && from != 0 && !AtObject(from, se)) why = "edit reach";
            if (why != null) { SessionLog.Event("detach-refused", net, item: idx, from: from, to: se.OwnerId, result: why); return; }
            // Ownership may have reached the server while this player's copy was still a proxy. Its local
            // UnCraft was suppressed, so even an already-owner request must run on the current owner.
            W.Reset(); W.U8(DetachReq); W.U32(net); W.VarU32((uint)idx);
            ServerSendTo(se.OwnerId, W, true);
        }

        /// The part's owner handles removal, even when its slot sits on another player's frame.
        private static bool RequestDetach(attachablescript at)
        {
            var ts = at != null ? at.tosave : null;
            var e = EditEntityOf(ts);
            if (e == null || !e.Proxy) return false;
            int idx = e.Items.IndexOf(ts);
            if (idx < 0) return false;
            W.Reset(); W.U8(DetachReq); W.U32(e.NetId); W.VarU32((uint)idx);
            ToServer(W, true);
            DetachReqsSent++;
            SessionLog.Event("detach-request", e.NetId, item: idx, from: MyId, to: e.OwnerId, epoch: e.Epoch);
            return true;
        }
    }
}
