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
            if (r.Bad || !Server.TryGetValue(s.Net, out var se) || se.OwnerId != from || se.Epoch != s.Epoch)
            { RabbitStatesStale++; return; }
            se.RabbitSnapshot = FinalSnapshot(r, true);
            // Change-only state must reach every known copy, including the far store. A range-filtered change
            // could leave a returning viewer stale forever. Unknown viewers get the cache with SendObject.
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            foreach (var kv in Known)
                if (kv.Key != from && kv.Value.Contains(s.Net)) ServerSendTo(kv.Key, WS, true);
        }

        private static void ApplyRabbitState(NetReader r)
        {
            var s = RabbitStateCodec.Read(r);
            if (r.Bad || !ByNet.TryGetValue(s.Net, out var e) || !e.Proxy || e.Epoch != s.Epoch) return;
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
