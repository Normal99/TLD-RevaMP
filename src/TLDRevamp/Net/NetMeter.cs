using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// What this machine sends and receives, by message type and by player: messages, bytes and the time spent on them
    /// (sending: the Steam call; receiving: the message's handler). Every Steam message passes SteamTransport.Send/Poll,
    /// so the totals here add up to the transport's BytesOut/BytesIn; a server Batch is split into its messages (each
    /// with its length prefix), the batch's own type byte counted as "Batch". The host's own messages to itself never
    /// touch the network and aren't counted. Always on: a few array additions and one Stopwatch read per message.
    /// Seen with Settings → Revamp → Debugging → Network packets (a panel, top right), the bridge's `net meter` (since the
    /// last `net meter reset`: totals, per second and per frame) and `net log [n]` (the last messages, one by one).
    public static class NetMeter
    {
        public static bool Show;
        private static readonly TrafficInterval Telemetry = new TrafficInterval();
        private static readonly PeerTrafficTable TelemetryPeers = new PeerTrafficTable();
        private static double _telemetrySince;
        private static readonly System.Collections.Generic.HashSet<uint>[] RestObjects = CreateRestObjects();
        private static System.Collections.Generic.HashSet<uint>[] CreateRestObjects()
        {
            var sets = new System.Collections.Generic.HashSet<uint>[8];
            for (int i = 0; i < sets.Length; i++) sets[i] = new System.Collections.Generic.HashSet<uint>();
            return sets;
        }
        private static readonly TrafficCount[][] FrameTypes = { new TrafficCount[256], new TrafficCount[256] };
        private static readonly int[] FrameNumbers = { -1, -1 };
        private static readonly TrafficCount[] EmptyFrame = new TrafficCount[256];
        private static readonly TrafficCount[] SampledEarly = new TrafficCount[256], FrameWork = new TrafficCount[256];
        private static int _sampledFrame = -1;
        private static bool _sampledEarly;
        internal static TrafficCount[] SampleFrame(bool capture)
        {
            int frame = Time.frameCount;
            var previous = FrameNumbers[(frame - 1) & 1] == frame - 1 ? FrameTypes[(frame - 1) & 1] : EmptyFrame;
            bool hasCurrent = FrameNumbers[frame & 1] == frame;
            var current = hasCurrent ? FrameTypes[frame & 1] : EmptyFrame;
            if (capture) for (int i = 0; i < 256; i++) FrameWork[i] = TrafficCount.Between(previous[i], _sampledFrame == frame - 1 ? SampledEarly[i] : default, current[i]);
            if (hasCurrent) Array.Copy(current, SampledEarly, 256);
            else if (_sampledEarly) Array.Clear(SampledEarly, 0, 256);
            _sampledEarly = hasCurrent; _sampledFrame = frame;
            return FrameWork;
        }
        internal static void ResetTelemetry() { Telemetry.Clear(); TelemetryPeers.Clear(); foreach (var set in RestObjects) set.Clear(); _telemetrySince = Time.realtimeSinceStartupAsDouble; }
        private static void Traffic(bool sent, byte type, int bytes, long ticks, byte[] buf, int at, int length, uint peer)
        {
            TelemetryPeers.Add(peer, sent, bytes);
            ref var interval = ref Telemetry.Types[type];
            if (sent) { interval.Sent++; interval.OutBytes += bytes; }
            else { interval.Received++; interval.InBytes += bytes; interval.Ticks += ticks; }
            int f = Time.frameCount, slot = f & 1;
            if (FrameNumbers[slot] != f) { Array.Clear(FrameTypes[slot], 0, 256); FrameNumbers[slot] = f; }
            ref var frame = ref FrameTypes[slot][type];
            if (sent) { frame.Sent++; frame.OutBytes += bytes; }
            else { frame.Received++; frame.InBytes += bytes; frame.Ticks += ticks; }
            int category = Entities.TelemetryPacketCategory(type, buf, at, length, out uint net);
            if (category < 0) return;
            ref var count = ref Telemetry.CategoriesCount[category];
            if (sent) { count.Sent++; count.OutBytes += bytes; }
            else { count.Received++; count.InBytes += bytes; }
            if (sent && category != 6)
            {
                bool rest = Entities.TelemetryAtRest(net);
                ref var detail = ref Telemetry.Details[category];
                detail.Add(type == Entities.State ? 0 : type == Entities.CompactState ? 1 : type == Entities.RabbitState ? 2 : 3, bytes, rest);
                if (rest) { RestObjects[category].Add(net); detail.RestObjects = RestObjects[category].Count; }
            }
        }
        internal static string TelemetryTick(double now, double seconds)
        {
            Telemetry.Local = now; Telemetry.St = SessionLog.St; Telemetry.Seconds = seconds;
            Entities.TelemetryOwned(Telemetry.Owned);
            Entities.TelemetryCensus(Telemetry.Census);
            Entities.BandwidthSnapshot(Telemetry.Bandwidth);
            Telemetry.InterestCount = Entities.InterestSkipSnapshot(Telemetry.InterestPlayers, Telemetry.InterestSkipped);
            Telemetry.PeerCount = 0; Telemetry.PeersDropped = TelemetryPeers.Dropped;
            for (int i = 0; i < PeerTrafficTable.Capacity; i++) if (TelemetryPeers.Used[i])
            {
                int j = Telemetry.PeerCount++;
                Telemetry.Players[j] = Mp.Server != null ? Mp.Server.PlayerOfConn(TelemetryPeers.Keys[i]) : 0;
                Telemetry.Peers[j] = TelemetryPeers.Counts[i];
            }
            SessionLog.History.Add(Telemetry);
            using (var w = new System.IO.StringWriter(System.Globalization.CultureInfo.InvariantCulture))
            { Telemetry.Write(w, Name); ResetTelemetry(); return w.ToString(); }
        }
        internal static string CategoryJson()
        {
            Telemetry.Local = Time.realtimeSinceStartupAsDouble; Telemetry.St = SessionLog.St; Telemetry.Seconds = Telemetry.Local - _telemetrySince;
            Entities.TelemetryOwned(Telemetry.Owned);
            Entities.TelemetryCensus(Telemetry.Census);
            Entities.BandwidthSnapshot(Telemetry.Bandwidth);
            Telemetry.InterestCount = Entities.InterestSkipSnapshot(Telemetry.InterestPlayers, Telemetry.InterestSkipped);
            using (var w = new System.IO.StringWriter(System.Globalization.CultureInfo.InvariantCulture)) { Telemetry.Write(w, Name); return w.ToString(); }
        }

        private static readonly long[] OutMsgs = new long[256], OutBytes = new long[256], OutTicks = new long[256];
        private static readonly long[] InMsgs = new long[256], InBytes = new long[256], InTicks = new long[256];
        private static readonly Dictionary<uint, long[]> Peers = new Dictionary<uint, long[]>();   // connection → out msgs, out bytes, in msgs, in bytes
        private static double _since; private static int _sinceFrame;   // since the last reset (0: the game's start)

        private struct Rec { public float T; public bool Out; public byte Type; public int Bytes; public uint Peer; }
        private static readonly Rec[] Ring = new Rec[512];
        private static int _ringAt; private static long _ringN;

        public static long Now() => Stopwatch.GetTimestamp();
        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static void Note(bool isOut, byte type, int bytes, uint peer)
        {
            Ring[_ringAt] = new Rec { T = Time.realtimeSinceStartup, Out = isOut, Type = type, Bytes = bytes, Peer = peer };
            _ringAt = (_ringAt + 1) % Ring.Length; _ringN++;
            if (!Peers.TryGetValue(peer, out var p)) Peers[peer] = p = new long[4];
            if (isOut) { p[0]++; p[1] += bytes; } else { p[2]++; p[3] += bytes; }
        }

        /// SteamTransport.Send: one Steam message (a Batch: each message in it), with the sending transport's role.
        internal static void Sent(byte[] buf, int len, uint peer, long t0, bool server)
        {
            if (len <= 0) return;
            long ticks = Now() - t0;
            byte type = buf[0];
            if (type != Protocol.Batch) { OutMsgs[type]++; OutBytes[type] += len; OutTicks[type] += ticks; Traffic(true, type, len, 0, buf, 0, len, peer); Note(true, type, len, peer); ItemTraffic.Packet(buf, 0, len, len, true, server); return; }
            OutMsgs[type]++; OutBytes[type]++; OutTicks[type] += ticks;
            Traffic(true, type, 1, 0, null, 0, 0, peer);
            int pos = 1;
            while (pos < len)
            {
                int at = pos; uint n = 0; int shift = 0;
                while (pos < len && shift < 35) { byte b = buf[pos++]; n |= (uint)(b & 0x7F) << shift; shift += 7; if ((b & 0x80) == 0) break; }
                if (n == 0 || n > len - pos) break;
                byte st = buf[pos];
                int bytes = (int)n + (pos - at);
                OutMsgs[st]++; OutBytes[st] += bytes; Note(true, st, bytes, peer);
                Traffic(true, st, bytes, 0, buf, pos, (int)n, peer);
                ItemTraffic.Packet(buf, pos, (int)n, bytes, true, server);
                pos += (int)n;
            }
        }

        /// A received message handled (for a Batch: only its own bytes; its messages come here one by one).
        /// The transport's role matters: a host's in-process bots are clients too.
        internal static void Received(byte type, byte[] buf, int offset, int length, int bytes, uint peer, long t0, bool server)
        {
            long ticks = Now() - t0;
            InMsgs[type]++; InBytes[type] += bytes; InTicks[type] += ticks;
            Traffic(false, type, bytes, ticks, buf, offset, length, peer);
            if (type != Protocol.Batch) { Note(false, type, bytes, peer); ItemTraffic.Packet(buf, offset, length, bytes, false, server); }
        }

        public static void Reset()
        {
            Array.Clear(OutMsgs, 0, 256); Array.Clear(OutBytes, 0, 256); Array.Clear(OutTicks, 0, 256);
            Array.Clear(InMsgs, 0, 256); Array.Clear(InBytes, 0, 256); Array.Clear(InTicks, 0, 256);
            ItemTraffic.Reset();
            Peers.Clear(); _since = Time.realtimeSinceStartupAsDouble; _sinceFrame = Time.frameCount;
            _ringAt = 0; _ringN = 0; _rateAt = 0;
        }

        // ---------------------------------------------------------------- names (every message type constant of the mod)
        private static string[] _names;
        public static string Name(byte type)
        {
            if (_names == null)
            {
                var n = new string[256];
                foreach (var t in new[] { typeof(Protocol), typeof(Entities), typeof(ModObjects), typeof(ModTransfer), typeof(PlayerSave), typeof(PlayerCombat), typeof(Reports), typeof(Tpa), typeof(TelemetryMarks) })
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (!f.IsLiteral || f.FieldType != typeof(byte) || f.IsPrivate || (f.Name.StartsWith("Reject") && f.Name != "Reject")) continue;
                        byte v = (byte)f.GetRawConstantValue();
                        if (v == 0) continue;
                        string name = f.Name == "Message" || f.Name == "Record" || f.Name == "Restore" ? t.Name + "." + f.Name : f.Name;
                        n[v] = n[v] == null ? name : n[v] + "/" + name;
                    }
                _names = n;
            }
            return _names[type] ?? "#" + type;
        }

        // ---------------------------------------------------------------- rates for the panel (over the last ≥1 s)
        private static readonly long[] _pOutB = new long[256], _pInB = new long[256], _pOutM = new long[256], _pInM = new long[256], _pTicks = new long[256];
        private static readonly float[] _rOutB = new float[256], _rInB = new float[256], _rOutM = new float[256], _rInM = new float[256], _rMs = new float[256];
        private static float _rateAt; private static int _rateFrame; private static float _rFrames;
        private static void Rates()
        {
            float now = Time.realtimeSinceStartup;
            if (_rateAt > 0 && now - _rateAt < 1f) return;
            float dt = now - _rateAt;
            bool ok = _rateAt > 0 && dt < 10f;
            _rFrames = ok ? (Time.frameCount - _rateFrame) / dt : 0f;
            for (int i = 0; i < 256; i++)
            {
                long ticks = OutTicks[i] + InTicks[i];
                if (ok)
                {
                    _rOutB[i] = (OutBytes[i] - _pOutB[i]) / dt; _rInB[i] = (InBytes[i] - _pInB[i]) / dt;
                    _rOutM[i] = (OutMsgs[i] - _pOutM[i]) / dt; _rInM[i] = (InMsgs[i] - _pInM[i]) / dt;
                    _rMs[i] = (float)Ms(ticks - _pTicks[i]) / dt;
                }
                _pOutB[i] = OutBytes[i]; _pInB[i] = InBytes[i]; _pOutM[i] = OutMsgs[i]; _pInM[i] = InMsgs[i]; _pTicks[i] = ticks;
            }
            _rateAt = now; _rateFrame = Time.frameCount;
        }

        private static readonly List<int> _order = new List<int>();
        /// The panel: the busiest message types of the last second.
        public static string PanelText(int rows)
        {
            Rates();
            _order.Clear();
            float outT = 0, inT = 0, msT = 0;
            for (int i = 0; i < 256; i++)
            {
                outT += _rOutB[i]; inT += _rInB[i]; msT += _rMs[i];
                if (_rOutB[i] + _rInB[i] > 0 || _rMs[i] > 0.001f) _order.Add(i);
            }
            _order.Sort((a, b) => (_rOutB[b] + _rInB[b]).CompareTo(_rOutB[a] + _rInB[a]));
            float fps = Mathf.Max(1f, _rFrames);
            var sb = new System.Text.StringBuilder();
            sb.Append($"<b>Network packets</b>  up {outT / 1024f:F1} KB/s ({outT / fps:F0} B/frame)  down {inT / 1024f:F1} KB/s ({inT / fps:F0} B/frame)  {msT:F2} ms/s\n");
            sb.Append("<b>type               up msg/s  up KB/s  down msg/s  down KB/s  ms/s</b>\n");
            for (int k = 0; k < _order.Count && k < rows; k++)
            {
                int i = _order[k];
                string nm = Name((byte)i); if (nm.Length > 18) nm = nm.Substring(0, 18);
                sb.Append(nm.PadRight(19)).Append(_rOutM[i].ToString("F1").PadLeft(9)).Append((_rOutB[i] / 1024f).ToString("F2").PadLeft(9))
                  .Append(_rInM[i].ToString("F1").PadLeft(12)).Append((_rInB[i] / 1024f).ToString("F2").PadLeft(11)).Append(_rMs[i].ToString("F2").PadLeft(7)).Append('\n');
            }
            if (_order.Count == 0) sb.Append("(nothing in the last second)\n");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- bridge
        /// `net meter`: since the last reset — per type and per player: messages, bytes, ms; per second and per frame.
        public static string Json()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            double secs = Math.Max(1e-3, Time.realtimeSinceStartupAsDouble - _since);
            int frames = Math.Max(1, Time.frameCount - _sinceFrame);
            var rows = new List<string>();
            long ob = 0, ib = 0, om = 0, im = 0; long tk = 0;
            for (int i = 0; i < 256; i++)
            {
                if (OutMsgs[i] == 0 && InMsgs[i] == 0) continue;
                ob += OutBytes[i]; ib += InBytes[i]; om += OutMsgs[i]; im += InMsgs[i]; tk += OutTicks[i] + InTicks[i];
                rows.Add("{\"type\":" + i + ",\"name\":" + TLDRevamp.Json.Str(Name((byte)i)) + ",\"outMsgs\":" + OutMsgs[i] + ",\"outBytes\":" + OutBytes[i] +
                         ",\"inMsgs\":" + InMsgs[i] + ",\"inBytes\":" + InBytes[i] + ",\"sendMs\":" + Ms(OutTicks[i]).ToString("F3", ic) + ",\"handleMs\":" + Ms(InTicks[i]).ToString("F3", ic) +
                         ",\"outBytesPerFrame\":" + (OutBytes[i] / (double)frames).ToString("F2", ic) + ",\"inBytesPerFrame\":" + (InBytes[i] / (double)frames).ToString("F2", ic) + "}");
            }
            var peers = new List<string>();
            foreach (var kv in Peers)
                peers.Add("{\"conn\":" + kv.Key + ",\"player\":" + (Mp.Server != null ? Mp.Server.PlayerOfConn(kv.Key) : -1) + ",\"outMsgs\":" + kv.Value[0] + ",\"outBytes\":" + kv.Value[1] +
                          ",\"inMsgs\":" + kv.Value[2] + ",\"inBytes\":" + kv.Value[3] + "}");
            return "{\"seconds\":" + secs.ToString("F2", ic) + ",\"frames\":" + frames + ",\"outMsgs\":" + om + ",\"outBytes\":" + ob + ",\"inMsgs\":" + im + ",\"inBytes\":" + ib +
                   ",\"ms\":" + Ms(tk).ToString("F3", ic) + ",\"outBytesPerFrame\":" + (ob / (double)frames).ToString("F2", ic) + ",\"inBytesPerFrame\":" + (ib / (double)frames).ToString("F2", ic) +
                   ",\"outBytesPerSec\":" + (ob / secs).ToString("F0", ic) + ",\"inBytesPerSec\":" + (ib / secs).ToString("F0", ic) +
                   ",\"types\":[" + string.Join(",", rows) + "],\"peers\":[" + string.Join(",", peers) + "]}";
        }

        /// `net log [n]`: the last n messages (newest last), each with when, which way, type, bytes and connection.
        public static string Log(int n)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            n = (int)Math.Min(Math.Min(n, Ring.Length), _ringN);
            var rows = new List<string>();
            for (int k = n; k >= 1; k--)
            {
                var r = Ring[(_ringAt - k + Ring.Length) % Ring.Length];
                rows.Add("{\"t\":" + r.T.ToString("F3", ic) + ",\"dir\":\"" + (r.Out ? "out" : "in") + "\",\"type\":" + TLDRevamp.Json.Str(Name(r.Type)) + ",\"bytes\":" + r.Bytes + ",\"conn\":" + r.Peer + "}");
            }
            return "{\"messages\":[" + string.Join(",", rows) + "]}";
        }
    }
}
