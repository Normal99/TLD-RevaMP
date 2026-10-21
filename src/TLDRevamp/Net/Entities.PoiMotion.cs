using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private struct PoiKey : IEquatable<PoiKey>
        {
            public int Gen, U; public double X, Y, Z;
            public bool Equals(PoiKey o) => Gen == o.Gen && U == o.U && X == o.X && Y == o.Y && Z == o.Z;
            public override bool Equals(object o) => o is PoiKey k && Equals(k);
            public override int GetHashCode() => Gen * 397 ^ U * 7919 ^ X.GetHashCode() ^ Y.GetHashCode() * 31 ^ Z.GetHashCode() * 17;
        }
        private struct PoiState { public float XRot, YRot, Slide, Tuned; public int Turn; }
        private static readonly Dictionary<PoiKey, byte[]> ServerPoiUsables = new Dictionary<PoiKey, byte[]>();
        private static readonly Dictionary<PoiKey, PoiState> KnownPoiUsables = new Dictionary<PoiKey, PoiState>();
        private static readonly Dictionary<PoiKey, double> _poiServerOrder = new Dictionary<PoiKey, double>();

        // Fixed storage per observed address. No queues or per-frame allocations for diagnostic rates.
        private sealed class UsableRate
        {
            private readonly float[] _times = new float[256];
            private int _next, _count;
            public void Add(float now) { _times[_next] = now; _next = (_next + 1) % _times.Length; if (_count < _times.Length) _count++; }
            public int Hz(float now)
            {
                int n = 0;
                for (int i = 0; i < _count; i++) if (now - _times[i] >= 0 && now - _times[i] < 1) n++;
                return n;
            }
        }
        private sealed class PoiObservation
        {
            public float ChangedAt;
            public long ReceivedTotal;
            public readonly UsableRate Sent = new UsableRate(), Received = new UsableRate();
        }
        private static readonly Dictionary<PoiKey, PoiObservation> _poiObserved = new Dictionary<PoiKey, PoiObservation>();
        private static PoiObservation PoiObserved(PoiKey k)
        {
            if (!_poiObserved.TryGetValue(k, out var s)) _poiObserved[k] = s = new PoiObservation();
            return s;
        }
        private static UsableKey PoiLiveKey(usablescript u) => new UsableKey { Building = true,
            Poi = new PoiKey { Gen = u.poigenid, X = u.poiid.x, Y = u.poiid.y, Z = u.poiid.z, U = u.usableid } };

        private static void WritePoi(NetWriter w, PoiKey k, PoiState st)
        {
            w.VarU32((uint)k.Gen); w.F64(k.X); w.F64(k.Y); w.F64(k.Z); w.VarU32((uint)k.U);
            w.F32(st.XRot); w.F32(st.YRot); w.F32(st.Slide); w.VarI32(st.Turn); w.F32(st.Tuned);
        }
        private static bool ReadPoi(NetReader r, out PoiKey k, out PoiState st)
        {
            k = new PoiKey { Gen = (int)r.VarU32(), X = r.F64(), Y = r.F64(), Z = r.F64(), U = (int)r.VarU32() };
            st = new PoiState { XRot = r.F32(), YRot = r.F32(), Slide = r.F32(), Turn = r.VarI32(), Tuned = r.F32() };
            return !r.Bad && Finite(st.XRot) && Finite(st.YRot) && Finite(st.Slide) && Finite(st.Tuned);
        }
        private static void WritePoiFinal(NetWriter w, UsableKey k, ushort seq, double stamp, PoiState st)
        { w.Reset(); w.U8(PoiUsable); WritePoi(w, k.Poi, st); w.U16(seq); w.F64(stamp); }
        private static void SendPoiFinal(LocalUsable s)
        {
            s.Dirty = false; s.Sent = s.Value; s.HasSent = true;
            _liveOut[s.Key] = ++s.Seq;
            KnownPoiUsables[s.Key.Poi] = s.Raw;
            WritePoiFinal(WL, s.Key, s.Seq, SessionLog.St, s.Raw); ToServer(WL, true); PoiUsablesSent++;
        }
        private static void ServerPoiUsable(int from, NetReader r)
        {
            ReadPoi(r, out var k, out var st); ushort seq = r.U16(); double stamp = r.F64();
            ServerPoiMotion(from, r, new UsableKey { Building = true, Poi = k }, seq, stamp, st, true);
        }
        private static void ApplyPoiUsable(NetReader r)
        {
            ReadPoi(r, out var k, out var st); ushort seq = r.U16(); double stamp = r.F64();
            ReceivePoiMotion(r, new UsableKey { Building = true, Poi = k }, seq, stamp, st, true);
        }
        private static bool PoiPacketValid(NetReader r, UsableKey k, double stamp, PoiState st) =>
            !r.Bad && r.Remaining == 0 && Finite(stamp) && Finite(k.Poi.X) && Finite(k.Poi.Y) && Finite(k.Poi.Z) &&
            Finite(st.XRot) && Finite(st.YRot) && Finite(st.Slide) && Finite(st.Tuned);

        private static void ServerPoiMotion(int from, NetReader r, UsableKey k, ushort seq, double stamp, PoiState st, bool final)
        {
            if (!PoiPacketValid(r, k, stamp, st) || Math.Abs(SessionLog.St - stamp) > 10)
            { Refuse(ref PoiRefusedValues, "poi value", $"building usable from player {from}: unreadable value/clock"); return; }
            if (!CheckPoiUsable(from, k.Poi, st)) return;
            if (_poiServerOrder.TryGetValue(k.Poi, out double last) && stamp < last) { LiveUsablesStale++; return; }
            if (!_liveServerIn.TryGetValue(from, out var orders)) _liveServerIn[from] = orders = new Dictionary<UsableKey, LiveOrder>();
            if (!LiveNewer(orders, k, seq, stamp)) return;
            _poiServerOrder[k.Poi] = stamp;
            _liveRelaySeq.TryGetValue(k, out ushort relaySeq); _liveRelaySeq[k] = ++relaySeq;
            // Retain even moving state: joiners/interest catch-up and unloaded hosts need no prefab to decode it.
            WritePoiFinal(WL, k, relaySeq, stamp, st);
            if (!ServerPoiUsables.TryGetValue(k.Poi, out var bytes) || bytes.Length != WL.Len) bytes = new byte[WL.Len];
            Buffer.BlockCopy(WL.Buf, 0, bytes, 0, WL.Len);
            ServerPoiUsables[k.Poi] = bytes;
            if (!final) WriteLiveUsable(WL, k, relaySeq, stamp, default, st);
            RelayPoi(k.Poi, WL, from, final);
        }
        private static void ReceivePoiMotion(NetReader r, UsableKey k, ushort seq, double stamp, PoiState st, bool final)
        {
            if (!PoiPacketValid(r, k, stamp, st) || !LiveNewer(_liveIn, k, seq, stamp)) return;
            var observation = PoiObserved(k.Poi);
            if (!KnownPoiUsables.TryGetValue(k.Poi, out var was) || !SameUsable(was, st)) observation.ChangedAt = Time.realtimeSinceStartup;
            KnownPoiUsables[k.Poi] = st;
            if (!final) { observation.Received.Add(Time.realtimeSinceStartup); observation.ReceivedTotal++; }
            var u = PoiUsableOf(k.Poi);
            if (u == null) { PoiUsablesUnplaced++; return; }
            if (LocalUsableHeld(u)) return;
            u.FStart();
            if (!_remoteUsables.TryGetValue(k, out var s) || s.U != u)
            {
                s = new RemoteUsable { ClockOffset = SessionLog.St - stamp, U = u };
                s.Motion.Add(stamp - LiveStepS, UsableState(u)); _remoteUsables[k] = s;
            }
            s.At = Time.realtimeSinceStartup; s.Final = true;
            s.Motion.Add(stamp, st);
            if (u.currentTurnState != st.Turn || !final && u.turnable && u.maxTurnState == 0)
            { var v = UsableState(u); v.Turn = st.Turn; ApplyUsable(u, v, true); }
            PoiUsablesApplied++;
            if (!final) LiveUsablesApplied++;
        }
    }
}
