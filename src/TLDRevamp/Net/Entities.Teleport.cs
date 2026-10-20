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
        }
        private static readonly Dictionary<int, TpaSync> TpaSyncs = new Dictionary<int, TpaSync>();
        private static readonly List<int> TpaDone = new List<int>();
        private static readonly Queue<byte[]> TpaInbox = new Queue<byte[]>();
        private static bool _tpaReplaying;
        private static bool _tpaSending, _tpaSendFailed;

        internal static void ResetTpaSync() { TpaSyncs.Clear(); TpaInbox.Clear(); }
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
                    try { SendObject(player, se); } // full add, detached parts, complete resync, then settled states
                    finally { _tpaSending = false; }
                    if (!_tpaSendFailed) delivery.Sent.Add(net); // a full transport queue is retried next tick
                    else break;
                }
                // Keep discovering objects shared during destination generation, without resending the whole area.
                if (delivery.Due.Count == 0 && now - sync.Started >= 5)
                {
                    var delta = pos - sync.Target;
                    SessionLog.Event("tpa-arrived", to: player, value: (float)delta.magnitude, count: delivery.Sent.Count,
                        dx: (float)delta.x, dy: (float)delta.y, dz: (float)delta.z, result: "full objects sent");
                    TpaDone.Add(player);
                }
            }
            foreach (int player in TpaDone) TpaSyncs.Remove(player);
        }

        // Hold the entity stream in order until there is world geometry at the destination. Otherwise fresh copies
        // can be streamed out/destroyed before the ground exists. Replay also preserves add -> parts -> resync ordering.
        private static bool DeferTpa(byte type, NetReader r)
        {
            if (Mp.Server != null || _tpaReplaying || (!Tpa.WaitingWorld && TpaInbox.Count == 0)) return false;
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
            }
            finally { _tpaReplaying = false; }
        }
        internal static bool TpaInboxEmpty => TpaInbox.Count == 0;
    }
}
