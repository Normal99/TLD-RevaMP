using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private static void PoiTests()
        {
            LiveUsablesReset(); Buildings.Clear(); Sent.Clear(); Reliable.Clear(); Relayed.Clear(); RelayReliable.Clear();
            ServerPoiUsables.Clear(); KnownPoiUsables.Clear(); _poiObserved.Clear(); _poiServerOrder.Clear();
            var u = new usablescript { setidPoi = true, poigenid = 2, usableid = 3, poiid = new Vector3d { x = 123.25, y = 10, z = -456.5 } };
            var k = PoiLiveKey(u); Buildings[k.Poi] = u;
            Time.realtimeSinceStartup = 10; SessionLog.St = 10;
            Input(u, 0); LiveUsableTick();
            Check(Sent.Count == 0, "building zero input stays silent");
            // Run the same sender as items at 120 FPS for a 1.5 s swing, including its exact endpoint.
            for (int frame = 1; frame <= 180; frame++)
            {
                Time.realtimeSinceStartup = 10 + frame / 120f; SessionLog.St = 10 + frame / 120.0;
                Input(u, frame * .5f); LiveUsableTick();
            }
            Check(Sent.Count >= 45 && Sent.Count <= 92 && !Reliable.Contains(true), "building motion is 30-60 Hz and unreliable");
            float final = u.xRot;
            Time.realtimeSinceStartup = 11.9f; SessionLog.St = 11.9; LiveUsableTick();
            Check(Reliable[Reliable.Count - 1] && Sent[Sent.Count - 1][0] == PoiUsable, "building settles with reliable 52");
            int count = Sent.Count;
            Time.realtimeSinceStartup = 13; SessionLog.St = 13; LiveUsableTick();
            Check(Sent.Count == count, "settled building sends nothing");
            Check(KnownPoiUsables[k.Poi].XRot == final, "local exact state retained for respawn");
            var packets = Sent.ToArray();
            for (int length = 1; length < packets[1].Length; length++)
            {
                var r = new NetReader { Buf = packets[1], Pos = 1, End = length };
                ReadLiveUsable(r, out var cutKey, out _, out double cutStamp, out _, out var cutState);
                Check(!PoiPacketValid(r, cutKey, cutStamp, cutState), "truncated building live protocol refused");
            }
            var extra = new byte[packets[1].Length + 1]; Array.Copy(packets[1], extra, packets[1].Length);
            var trailing = Reader(extra); ReadLiveUsable(trailing, out var trailingKey, out _, out double trailingStamp, out _, out var trailingState);
            Check(!PoiPacketValid(trailing, trailingKey, trailingStamp, trailingState), "trailing building protocol bytes refused");
            _localUsables.Clear();
            foreach (var packet in packets)
            {
                if (packet[0] == PoiUsable) { SessionLog.St = 11.9; ServerPoiUsable(1, Reader(packet)); }
                else
                {
                    ReadLiveUsable(Reader(packet), out var key, out _, out double stamp, out _, out var st);
                    Check(key.Equals(k) && st.XRot >= 0 && st.XRot <= 90, "building live protocol address/value round trip");
                    SessionLog.St = stamp; ServerLiveUsable(1, Reader(packet));
                }
            }
            Check(Relayed.Count == count && RelayReliable[RelayReliable.Count - 1], "server relays live and final with their delivery modes");
            ReadPoi(Reader(ServerPoiUsables[k.Poi]), out var recordKey, out var record);
            Check(recordKey.Equals(k.Poi) && record.XRot == final, "server exact reliable record survives for joiners");
            long stale = LiveUsablesStale;
            ServerLiveUsable(1, Reader(packets[1]));
            Check(Relayed.Count == count && LiveUsablesStale > stale, "old motion cannot overwrite server final");
            // Real frame renderer with packet loss/jitter; independent of sender ownership/entity state.
            u.xRot = 0; u.EndpointSounds = u.SwitchEvents = 0;
            _liveIn.Clear(); _remoteUsables.Clear(); _localUsables.Clear();
            int next = 0, moving = 0; float previous = 0, largest = 0;
            for (int frame = 0; frame <= 270; frame++)
            {
                double now = 10 + frame / 120.0; SessionLog.St = now; Time.realtimeSinceStartup = (float)now;
                while (next < Relayed.Count)
                {
                    var packet = Relayed[next]; double stamp;
                    if (packet[0] == PoiUsable) { var r = Reader(packet); ReadPoi(r, out _, out _); r.U16(); stamp = r.F64(); }
                    else ReadLiveUsable(Reader(packet), out _, out _, out stamp, out _);
                    if (stamp + .02 + next % 4 * .003 > now) break;
                    next++;
                    if (packet[0] == PoiUsable) ApplyPoiUsable(Reader(packet));
                    else if (next % 7 != 0) ApplyLiveUsable(Reader(packet));
                }
                LiveUsableTick();
                if (frame > 45 && frame < 175 && u.xRot > previous) moving++;
                largest = Math.Max(largest, Math.Abs(u.xRot - previous)); previous = u.xRot;
            }
            Check(moving > 120 && largest < 3, "building buffered renderer progresses every frame under jitter/loss");
            Check(u.xRot == final && !_remoteUsables.ContainsKey(k), "building reliable final renders exactly and retires buffer");
            Check(u.SwitchEvents == 0 && u.EndpointSounds == 2, "open leaves minimum and reaches maximum once without switch spam");
            ApplyPoiUsable(Reader(Relayed[Relayed.Count - 1]));
            Check(u.EndpointSounds == 2 && !_remoteUsables.ContainsKey(k), "duplicate final cannot replay door sounds");
            Check(_poiObserved[k.Poi].Received.Hz(Time.realtimeSinceStartup + 2) == 0, "diagnostic rate expires after a second");
            // Late packets cannot resurrect an older stream after the reliable final.
            ApplyLiveUsable(Reader(Relayed[1]));
            Check(!_remoteUsables.ContainsKey(k), "delayed unreliable refuses settled state");
            // An unloaded building retains the newest accepted state for ApplyKnown on spawn.
            Buildings.Clear(); _liveIn.Clear();
            ApplyPoiUsable(Reader(Relayed[Relayed.Count - 1]));
            Check(KnownPoiUsables[k.Poi].XRot == final, "unloaded building remembers exact final");
            // Full open/close through the real merge: no sounds on held endpoints or repeated switch finals.
            u.xRot = 0; u.EndpointSounds = 0;
            for (int i = 0; i <= 180; i++) UsableMerge.ApplyRendered(u, 0, i <= 90 ? i : 180 - i, 0, 0, 0, false);
            for (int i = 0; i < 10; i++) UsableMerge.ApplyRendered(u, 0, 0, 0, 0, 0, false);
            Check(u.EndpointSounds == 4, "open and close each play only their endpoint edges");
            UsableMerge.ApplyRendered(u, 0, 0, 0, 1, 0, true);
            UsableMerge.ApplyRendered(u, 0, 0, 0, 1, 0, false);
            Check(u.SwitchEvents == 1, "discrete switch fires once; settling cannot replay it");
            // Sub-quantum input must postpone finalization yet still retain its exact raw float.
            LiveUsablesReset(); Sent.Clear(); Reliable.Clear(); Buildings[k.Poi] = u;
            Time.realtimeSinceStartup = 16; SessionLog.St = 16; u.xRot = 45;
            Input(u, 45); LiveUsableTick();
            Input(u, 45.0001f); LiveUsableTick();
            Check(Sent.Count == 0, "sub-quantum building change sends no redundant live packet");
            Time.realtimeSinceStartup = 16.2f; SessionLog.St = 16.2; Input(u, 45.0002f); LiveUsableTick();
            Time.realtimeSinceStartup = 16.4f; SessionLog.St = 16.4; LiveUsableTick();
            Check(Sent.Count == 0, "sub-quantum change postpones reliable settlement");
            Time.realtimeSinceStartup = 16.6f; SessionLog.St = 16.6; LiveUsableTick();
            ReadPoi(Reader(Sent[0]), out _, out var exact);
            Check(Sent.Count == 1 && Reliable[0] && exact.XRot == 45.0002f, "sub-quantum final keeps exact raw value");
            ushort beforeReload = _liveOut[k];
            _liveOut[new UsableKey { Net = 44 }] = 123;
            LiveUsablesReset(keepPoiSequences: true);
            Check(_liveOut[k] == beforeReload && !_liveOut.ContainsKey(new UsableKey { Net = 44 }), "world reload preserves building sequences while retiring item addresses");
            Input(u, 46); Time.realtimeSinceStartup = 17; SessionLog.St = 17; LiveUsableTick();
            Check(_liveOut[k] > beforeReload, "first post-reload building motion continues sequence");
            var rate = new UsableRate(); rate.Add(16.6f);
            for (int i = 0; i < 100; i++) rate.Hz(16.6f);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) rate.Hz(16.6f);
            Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "building rate reads allocate zero per frame");
            // Truncated/trailing/nonfinite/stale input is not admitted, and the existing authority check is used.
            int relays = Relayed.Count; PoiAllowed = false; SessionLog.St = 20;
            WriteLiveUsable(WL, k, 400, 20, default, new PoiState { XRot = 12 }); ServerLiveUsable(2, Reader(WL.Buf.AsSpan(0, WL.Len).ToArray()));
            Check(Relayed.Count == relays, "building motion obeys CheckPoiUsable authority"); PoiAllowed = true;
            WriteLiveUsable(WL, k, 400, double.NaN, default, default); ServerLiveUsable(2, Reader(WL.Buf.AsSpan(0, WL.Len).ToArray()));
            Check(Relayed.Count == relays, "building nonfinite clock refused");
            WriteLiveUsable(WL, k, 400, 20, default, new PoiState { XRot = float.NaN }); ServerLiveUsable(2, Reader(WL.Buf.AsSpan(0, WL.Len).ToArray()));
            Check(Relayed.Count == relays, "building nonfinite motion refused");
            // Reordered sender sequence, source change and wrap all share the live ordering logic.
            var orders = new System.Collections.Generic.Dictionary<UsableKey, LiveOrder>();
            Check(LiveNewer(orders, k, 65535, 30) && LiveNewer(orders, k, 0, 31) && !LiveNewer(orders, k, 65535, 32), "building sequence wrap and stale refusal");
            LiveUsablesReset(); Buildings.Clear();
        }
    }
}
