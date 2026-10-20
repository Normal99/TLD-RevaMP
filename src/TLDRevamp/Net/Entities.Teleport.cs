using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private sealed class TpaSync
        {
            internal Vector3d Target;
            internal float Until, Started, Next;
            internal readonly TpaDelivery Delivery = new TpaDelivery();
            internal readonly HashSet<uint> FullOwed = new HashSet<uint>();
        }
        private static readonly Dictionary<int, TpaSync> TpaSyncs = new Dictionary<int, TpaSync>();
        private static readonly List<int> TpaDone = new List<int>();
        private static readonly Queue<byte[]> TpaInbox = new Queue<byte[]>();
        private static readonly HashSet<uint> TpaAdds = new HashSet<uint>();
        private static bool _tpaReplaying;
        private static bool _tpaSending, _tpaSendFailed;

        internal static void ResetTpaSync() { TpaSyncs.Clear(); TpaInbox.Clear(); TpaAdds.Clear(); }
        internal static void ServerTpaBegin(int player, Vector3d target)
        {
            if (player != 0) TpaSyncs[player] = new TpaSync { Target = target, Until = Time.realtimeSinceStartup + 35 };
        }

        // Uses the accepted destination, not a position cached before the large jump. Normal interest is untouched.
        internal static void ServerTpaTick()
        {
            if (Mp.Server == null || TpaSyncs.Count == 0) return;
            TpaDone.Clear();
            foreach (var pair in TpaSyncs)
            {
                int player = pair.Key; var sync = pair.Value; float now = Time.realtimeSinceStartup;
                if (now > sync.Until || !Mp.Server.PeerPos(player, out var pos))
                { TpaDone.Add(player); SessionLog.Event("tpa-failure", to: player, result: "arrival sync timed out or peer left"); continue; }
                if (!TpaArrival.InRange(pos.x, pos.z, sync.Target.x, sync.Target.z, 32) || now < sync.Next) continue;
                if (sync.Started == 0) sync.Started = now;
                sync.Next = now + .1f;
                var delivery = sync.Delivery; delivery.Due.Clear();
                foreach (var se in Server.Values)
                {
                    ServerLockedPose(se);
                    // A detached part in range needs its parent's full record first, even if the car is far away.
                    uint root = se.PartIndex >= 0 ? se.ParentNet : se.NetId;
                    delivery.Offer(root, se.Where.x, se.Where.z, pos.x, pos.z, InterestM, System.Math.Max(1, InterestPerTick));
                }
                foreach (var net in delivery.Due)
                {
                    if (!Server.TryGetValue(net, out var se)) continue;
                    _tpaSending = true; _tpaSendFailed = false;
                    bool known = KnownBy(player).Contains(net) && !sync.FullOwed.Contains(net);
                    try
                    {
                        if (!known) SendObject(player, se);
                        else SendTpaState(player, se);
                    }
                    finally { _tpaSending = false; }
                    if (!_tpaSendFailed) { delivery.Sent.Add(net); sync.FullOwed.Remove(net); }
                    else { if (!known) { sync.FullOwed.Add(net); KnownBy(player).Remove(net); } break; }
                }
                // Keep discovering objects shared during destination generation, without resending the whole area.
                if (delivery.Due.Count == 0 && now - sync.Started >= 5)
                {
                    var delta = pos - sync.Target;
                    SessionLog.Event("tpa-arrived", from: player, to: player, value: (float)delta.magnitude, count: delivery.Sent.Count,
                        dx: (float)delta.x, dy: (float)delta.y, dz: (float)delta.z, result: "objects sent");
                    TpaDone.Add(player);
                }
            }
            foreach (int player in TpaDone) TpaSyncs.Remove(player);
        }

        private static void SendTpaState(int player, SEnt se)
        {
            SendSettledState(player, se);
            SendRabbitSnapshot(player, se);
            SendCreatureSnapshots(player, se);
            foreach (var pn in se.Parts)
                if (Server.TryGetValue(pn, out var part))
                {
                    if (!KnownBy(player).Contains(pn))
                    { WritePartDetached(W, part); ServerSendTo(player, W, true); if (!_tpaSendFailed) KnownBy(player).Add(pn); }
                    SendSettledState(player, part);
                }
            if (_staleEnts.TryGetValue(player, out var stale) && stale.Contains(se.NetId))
            {
                if (se.ItemState.Count > 0) { WriteResyncAll(WS, se); ServerSendTo(player, WS, true); }
                if (!_tpaSendFailed) InterestGotAll(player, se.NetId);
            }
        }

        // Geometry-dependent updates keep their order. Control traffic continues; ownership of a queued Add
        // waits for that Add too, so it cannot be discarded before its copy exists.
        private static bool DeferTpa(byte type, NetReader r)
        {
            if (Mp.Server != null || _tpaReplaying || (!Tpa.WaitingWorld && TpaInbox.Count == 0)) return false;
            uint net = r.Remaining >= 4 ? System.BitConverter.ToUInt32(r.Buf, r.Pos) : 0;
            bool depends = type == Add || type == State || type == CompactState || type == Resync || type == PartDetached
                || type == AttachSync || type == DetachSync || type == PhysLockSync || type == MemberGone || type == RemoveItem
                || type == Worn || type == RabbitState || type == AiState || type == HitchCommand
                || ((type == Owner || type == HandoverState || type == Edit || type == Protocol.LiveUsable || type == RadioSync || type == IgnitionKey) && TpaAdds.Contains(net));
            if (!depends) return false;
            if (type == Add && r.Remaining >= 4) TpaAdds.Add(net);
            if (type == PartDetached)
            {
                var part = new NetReader { Buf = r.Buf, Pos = r.Pos, End = r.End };
                part.U32(); part.VarU32(); uint child = part.U32();
                if (!part.Bad) TpaAdds.Add(child);
            }
            var bytes = new byte[r.Remaining + 1]; bytes[0] = type;
            System.Buffer.BlockCopy(r.Buf, r.Pos, bytes, 1, r.Remaining);
            TpaInbox.Enqueue(bytes);
            return true;
        }
        internal static void DrainTpaInbox()
        {
            // Bounded work per frame; new messages join the tail while replay is pending.
            int budget = 200;
            _tpaReplaying = true;
            try
            {
                while (!Tpa.WaitingWorld && budget-- > 0 && TpaInbox.Count > 0)
                {
                    var bytes = TpaInbox.Dequeue(); var r = new NetReader { Buf = bytes, Pos = 1, End = bytes.Length };
                    ClientReceive(bytes[0], r);
                }
                if (TpaInbox.Count == 0) TpaAdds.Clear();
            }
            finally { _tpaReplaying = false; }
        }
        internal static bool TpaInboxEmpty => TpaInbox.Count == 0;
    }
}
