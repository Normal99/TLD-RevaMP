using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// A part destroyed while it is still in its object's group: an exploding tank takes the part it sits in with it
    /// (tankscript.TryExplode → Destroy(parentDest): a radiator, a fuel tank), and the game destroys other members the
    /// same way. Only a whole object's deletion went out (RemoveItem), so the owner's car lost the radiator and every
    /// other screen kept it for good (playtest 2026-10-16/17: client 1's car hit a building, its tank went off, the
    /// radiator was gone on their screen and still there on the host's — "slotted part destroyed #7: Car02Coolant ...
    /// owner 3").
    /// Now the owner's machine names the destroyed member (its group index) the frame after; the others destroy their
    /// copy of it, and the server remembers it for everyone who gets the object later. Message 86, no protocol bump:
    /// an older peer ignores it (as before).
    public static partial class Entities
    {
        public const byte MemberGone = 86;
        public static bool MemberGoneSync = true;   // A/B: false = members destroyed on the owner's machine only (before v0.68.3)
        public static long MembersGoneSent, MembersGoneApplied, MembersGoneRejected;

        private struct GoneMember { public uint Net; public int Idx; public string Name; }
        private static readonly List<GoneMember> _goneQ = new List<GoneMember>();

        /// From ItemDestroyed (a real deletion, not streaming): a member of a group this machine owns.
        private static void NoteMemberDestroyed(tosaveitemscript it)
        {
            if (!MemberGoneSync) return;
            var e = SharedGroupOf(it);
            if (e == null || e.Root == null || e.Root == it || e.Proxy || e.OwnerId != MyId) return;
            int idx = e.Items.IndexOf(it);
            if (idx < 0 || (e.Split != null && e.Split.Contains(idx))) return;   // came off: its own object
            _goneQ.Add(new GoneMember { Net = e.NetId, Idx = idx, Name = it.name });
        }

        /// Per frame: sent once the frame is over — a whole object going (its root's own RemoveItem) takes its members
        /// with it, and those are not sent one by one.
        private static void MemberGoneTick()
        {
            if (_goneQ.Count == 0) return;
            foreach (var g in _goneQ)
            {
                if (!ByNet.TryGetValue(g.Net, out var e) || e.Root == null || e.Proxy) continue;
                W.Reset(); W.U8(MemberGone); W.U32(g.Net); W.VarU32((uint)g.Idx);
                ToServer(W, true);
                if (MembersGoneSent++ < 10) Plugin.Log.LogInfo($"member destroyed: {g.Name} (net {g.Net}, index {g.Idx}) — sent");
            }
            _goneQ.Clear();
        }

        // ---------------------------------------------------------------- server
        private static void ServerMemberGone(int from, NetReader r)
        {
            uint net = r.U32(); int idx = (int)r.VarU32();
            if (r.Bad || idx < 0 || !Server.TryGetValue(net, out var se) || se.PartIndex >= 0) { MembersGoneRejected++; return; }
            if (se.OwnerId != from)
            {
                // another player deleted it on their copy (Entities.Lifecycle, PlayerDeleteSync): its owner deletes it and
                // tells everyone, as for a member destroyed on its own machine
                if (!PlayerDeleteSync || (se.Gone != null && se.Gone.Contains(idx))) { MembersGoneRejected++; return; }
                WriteMemberGone(WS, net, idx);
                ServerSendTo(se.OwnerId, WS, true);
                MemberDeletesForwarded++;
                return;
            }
            (se.Gone ?? (se.Gone = new HashSet<int>())).Add(idx);
            se.ItemState.Remove(idx);   // a later joiner's resync must not bring it back
            WriteMemberGone(WS, net, idx);
            ServerSendAll(WS, true, from);
        }

        private static void WriteMemberGone(NetWriter w, uint net, int idx)
        {
            w.Reset(); w.U8(MemberGone); w.U32(net); w.VarU32((uint)idx);
        }

        /// Someone getting the object now: after the object itself (SendObject), what is gone from it.
        private static void SendGoneMembers(int playerId, SEnt se)
        {
            if (se.Gone == null) return;
            foreach (int idx in se.Gone) { WriteMemberGone(WS, se.NetId, idx); ServerSendTo(playerId, WS, true); }
        }

        // ---------------------------------------------------------------- everyone else
        private static void ApplyMemberGone(NetReader r)
        {
            uint net = r.U32(); int idx = (int)r.VarU32();
            if (r.Bad || idx < 0 || !ByNet.TryGetValue(net, out var e)) { MembersGoneRejected++; return; }
            if (!e.Proxy)
            {
                // ours: another player deleted this member on their copy — deleted here the game's way; its OnDestroy (a
                // real deletion, no far-store record) sends MemberGone to everyone
                if (!PlayerDeleteSync || e.OwnerId != MyId || !Resolve(e) || idx >= e.Items.Count || (e.Split != null && e.Split.Contains(idx))) { MembersGoneRejected++; return; }
                var mine = e.Items[idx];
                if (mine == null || mine == e.Root) { MembersGoneRejected++; return; }
                mine.JustDestroy();
                MemberDeletesApplied++;
                return;
            }
            if (e.ItemIds != null && idx < e.ItemIds.Count)
            {
                uint gid = e.ItemIds[idx];
                e.ItemIds[idx] = 0;   // never bound again (RebindMembers)
                if (e.Root == null) { if (gid != 0) RemoveRecord(gid); MembersGoneApplied++; return; }   // stored here: out of the record
            }
            if (e.Root == null || idx >= e.Items.Count) return;
            var it = e.Items[idx];
            e.Items[idx] = null;
            if (it == null || it == e.Root) return;
            SuppressDestroyEcho = true;
            try { Object.Destroy(it.gameObject); }   // its OnDestroy takes it out of this machine's save
            finally { SuppressDestroyEcho = false; }
            MembersGoneApplied++;
        }

        public static string MemberGoneStats() => "{\"sent\":" + MembersGoneSent + ",\"applied\":" + MembersGoneApplied + ",\"rejected\":" + MembersGoneRejected +
                                                  ",\"playerDeleteSync\":" + (PlayerDeleteSync ? "true" : "false") + ",\"playerDeletes\":" + PlayerDeletesSent + ",\"memberDeletesSent\":" + MemberDeletesSent +
                                                  ",\"memberDeletesForwarded\":" + MemberDeletesForwarded + ",\"memberDeletesApplied\":" + MemberDeletesApplied +
                                                  ",\"keepPartsOnRemove\":" + (KeepPartsOnRemove ? "true" : "false") + ",\"partsKept\":" + PartsKeptOnRemove +
                                                  ",\"partsOrphaned\":" + PartsOrphaned + ",\"partsOrphanedNoRecord\":" + PartsOrphanedNoRecord + "}";
    }
}
