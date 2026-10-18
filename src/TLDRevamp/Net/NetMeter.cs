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

        /// SteamTransport.Send: one Steam message (a Batch: each message in it).
        internal static void Sent(byte[] buf, int len, uint peer, long t0)
        {
            if (len <= 0) return;
            long ticks = Now() - t0;
            byte type = buf[0];
            if (type != Protocol.Batch) { OutMsgs[type]++; OutBytes[type] += len; OutTicks[type] += ticks; Note(true, type, len, peer); return; }
            OutMsgs[type]++; OutBytes[type]++; OutTicks[type] += ticks;
            int pos = 1;
            while (pos < len)
            {
                int at = pos; uint n = 0; int shift = 0;
                while (pos < len && shift < 35) { byte b = buf[pos++]; n |= (uint)(b & 0x7F) << shift; shift += 7; if ((b & 0x80) == 0) break; }
                if (n == 0 || n > len - pos) break;
                byte st = buf[pos];
                int bytes = (int)n + (pos - at);
                OutMsgs[st]++; OutBytes[st] += bytes; Note(true, st, bytes, peer);
                pos += (int)n;
            }
        }

        /// A received message handled (for a Batch: only its own bytes; its messages come here one by one).
        internal static void Received(byte type, int bytes, uint peer, long t0)
        {
            InMsgs[type]++; InBytes[type] += bytes; InTicks[type] += Now() - t0;
            if (type != Protocol.Batch) Note(false, type, bytes, peer);
        }

        public static void Reset()
        {
            Array.Clear(OutMsgs, 0, 256); Array.Clear(OutBytes, 0, 256); Array.Clear(OutTicks, 0, 256);
            Array.Clear(InMsgs, 0, 256); Array.Clear(InBytes, 0, 256); Array.Clear(InTicks, 0, 256);
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
