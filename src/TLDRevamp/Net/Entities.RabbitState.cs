using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private sealed class RabView { public byte Flags; public float Run, ShownRun; public Vector3d Interest; public bool Has; }
        private static readonly Dictionary<nyulscript, RabView> RabViews = new Dictionary<nyulscript, RabView>();
        private static readonly Dictionary<nyulscript, bool> RabSeen = new Dictionary<nyulscript, bool>();
        private static void ServerRabbitState(int from, NetReader r)
        {
            var s = RabbitStateCodec.Read(r);
            if (r.Bad || !Server.TryGetValue(s.Net, out var se) || se.Epoch != s.Epoch)
            { RabbitStatesStale++; return; }
            if (se.OwnerId != from)
            {
                // One final target from the departing owner, using the existing handover epoch/window.
                if (se.PrevOwnerId != from || Time.realtimeSinceStartup - se.PrevOwnerAt > HandoverAcceptS || se.RabbitHandoverEpoch == s.Epoch)
                { RabbitStatesStale++; return; }
                se.RabbitHandoverEpoch = s.Epoch;
            }
            se.RabbitSnapshot = FinalSnapshot(r, true);
            // The host keeps the durable snapshot; returning known copies get it from StateInterestCatchUp.
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            if (se.OwnerId != from) ServerSendTo(se.OwnerId, WS, true);
            foreach (var kv in Known)
                if (kv.Key != from && kv.Key != se.OwnerId && kv.Value.Contains(s.Net) && RabbitReceiverNear(kv.Key, se)) ServerSendTo(kv.Key, WS, true);
        }

        private static void ApplyRabbitState(NetReader r)
        {
            var s = RabbitStateCodec.Read(r);
            if (r.Bad || !ByNet.TryGetValue(s.Net, out var e) || e.Epoch != s.Epoch) return;
            if (!e.Proxy)
            {
                if (Time.realtimeSinceStartup - e.OwnerAt <= HandoverWindowS) RabbitHandoverTarget(e, s);
                return;
            }
            e.RabbitSnapshot = FinalSnapshot(r, true);
            RestoreRabbitState(e);
            RabbitStatesApplied++;
        }

        private static void RestoreRabbitState(Ent e)
        {
            if (e.RabbitSnapshot == null || !e.Proxy || e.Root == null) return;
            var r = new NetReader { Buf = e.RabbitSnapshot, Pos = 1, End = e.RabbitSnapshot.Length };
            var s = RabbitStateCodec.Read(r);
            if (r.Bad || s.Epoch != e.Epoch) return;
            var ny = NyOf(e);
            if (ny == null) return;
            if (!RabViews.TryGetValue(ny, out var v)) RabViews[ny] = v = new RabView { ShownRun = ny.runValue };
            v.Flags = s.Flags; v.Run = s.Run / 200f; v.Interest = new Vector3d(s.X, s.Y, s.Z); v.Has = true;
            RabSeen[ny] = s.Seen;
        }

        private static void SendRabbitSnapshot(int playerId, SEnt se)
        {
            if (se.RabbitSnapshot == null) return;
            var r = new NetReader { Buf = se.RabbitSnapshot, Pos = 1, End = se.RabbitSnapshot.Length };
            var s = RabbitStateCodec.Read(r);
            if (r.Bad || s.Epoch != se.Epoch) return;
            WS.Reset(); WS.Bytes(se.RabbitSnapshot, 0, se.RabbitSnapshot.Length); ServerSendTo(playerId, WS, true);
        }
    }
}
