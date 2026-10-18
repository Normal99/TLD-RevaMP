using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TLDRevamp
{
    // Kept free of Unity so the buffer and wire-to-disk formats can be exercised offline.
    internal static class Json
    {
        public static string Num(double n, string format = "F3") => double.IsNaN(n) || double.IsInfinity(n) ? "null" : n.ToString(format, CultureInfo.InvariantCulture);
        public static string Str(string s)
        {
            var b = new StringBuilder().Append('"');
            foreach (char c in s ?? "")
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default: if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c); break;
                }
            return b.Append('"').ToString();
        }
    }

    internal struct TelemetryFrame
    {
        public double St, Local;
        public float Ms, Speed;
        public int Gc0, Gc1, Gc2, Flags, Gen;
        public long Sent, Received;
        internal void ObserveGc(float gcMs)
        {
            // A marker proves GC work even if Mono reports no completed collection.
            if (Gc0 == 0 && Gc1 == 0 && Gc2 == 0 && gcMs > 0) Gc0 = 1;
        }
    }

    internal sealed class FrameRing
    {
        private readonly TelemetryFrame[] _rows;
        private readonly float[] _phases;
        private readonly int _phaseCount;
        private int _head;
        public int Count { get; private set; }
        public long Overwritten { get; private set; }
        public FrameRing(int capacity, int phases)
        {
            if (capacity < 1 || phases < 0) throw new ArgumentOutOfRangeException();
            _rows = new TelemetryFrame[capacity]; _phaseCount = phases;
            _phases = new float[capacity * phases];
        }
        public TelemetryFrame Row(int i) => _rows[(_head - Count + i + _rows.Length) % _rows.Length];
        public float Phase(int i, int p) => _phases[((_head - Count + i + _rows.Length) % _rows.Length) * _phaseCount + p];
        public void Clear() { _head = Count = 0; Overwritten = 0; }
        public void Add(TelemetryFrame row, float[] phases, double seconds = 30)
        {
            while (Count > 0 && row.Local - Row(0).Local > seconds) Count--;
            if (Count == _rows.Length) Overwritten++; else Count++;
            _rows[_head] = row;
            Array.Copy(phases, 0, _phases, _head * _phaseCount, _phaseCount);
            _head = (_head + 1) % _rows.Length;
        }
        public void CopyTo(FrameRing dst)
        {
            if (dst._rows.Length < Count || dst._phaseCount != _phaseCount) throw new ArgumentException();
            int start = (_head - Count + _rows.Length) % _rows.Length;
            int first = Math.Min(Count, _rows.Length - start), rest = Count - first;
            Array.Copy(_rows, start, dst._rows, 0, first);
            Array.Copy(_rows, 0, dst._rows, first, rest);
            Array.Copy(_phases, start * _phaseCount, dst._phases, 0, first * _phaseCount);
            Array.Copy(_phases, 0, dst._phases, first * _phaseCount, rest * _phaseCount);
            dst.Count = Count; dst._head = Count % dst._rows.Length; dst.Overwritten = Overwritten;
        }
        public FrameRing Snapshot(double from = double.MinValue, double to = double.MaxValue)
        {
            var dst = new FrameRing(Math.Max(1, Count), _phaseCount);
            for (int i = 0; i < Count; i++)
            {
                // Include a freeze that ends after the aftermath boundary but overlaps the requested window.
                var r = Row(i); if (r.Local < from || r.Local - r.Ms / 1000.0 > to) continue;
                int k = dst.Count++;
                dst._rows[k] = r;
                for (int p = 0; p < _phaseCount; p++) dst._phases[k * _phaseCount + p] = Phase(i, p);
            }
            dst._head = dst.Count % dst._rows.Length; dst.Overwritten = Overwritten;
            return dst;
        }
        public void Write(TextWriter w, string head, string[] phaseNames, double from = double.MinValue, double to = double.MaxValue)
        {
            w.Write("{\"head\":"); w.Write(head); w.Write(",\"overwritten\":"); w.Write(Overwritten);
            w.Write(",\"cols\":[\"st\",\"ms\"");
            foreach (string name in phaseNames) { w.Write(','); w.Write(Json.Str(name)); }
            w.Write(",\"gc0\",\"gc1\",\"gc2\",\"rebase\",\"stream\",\"saving\",\"loading\",\"gen\",\"bytesSent\",\"bytesReceived\",\"speedKmh\"],\"rows\":[");
            bool first = true;
            for (int i = 0; i < Count; i++)
            {
                var r = Row(i); if (r.Local < from || r.Local - r.Ms / 1000.0 > to) continue;
                if (!first) w.Write(','); first = false;
                w.Write('['); w.Write(Json.Num(r.St)); w.Write(','); w.Write(Json.Num(r.Ms));
                for (int p = 0; p < _phaseCount; p++) { w.Write(','); w.Write(Json.Num(Phase(i, p))); }
                w.Write(','); w.Write(r.Gc0); w.Write(','); w.Write(r.Gc1); w.Write(','); w.Write(r.Gc2);
                for (int bit = 1; bit <= 8; bit <<= 1) { w.Write(','); w.Write((r.Flags & bit) != 0 ? "1" : "0"); }
                w.Write(','); w.Write(r.Gen); w.Write(','); w.Write(r.Sent); w.Write(','); w.Write(r.Received);
                w.Write(','); w.Write(Json.Num(r.Speed)); w.Write(']');
            }
            w.Write("]}");
        }
    }

    internal sealed class MarkLimiter
    {
        private readonly Dictionary<ulong, double> _last = new Dictionary<ulong, double>();
        public bool Try(ulong player, double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now)) return false;
            if (_last.TryGetValue(player, out double at) && now - at < 10) return false;
            _last[player] = now; return true;
        }
        public void Clear() => _last.Clear();
    }

    internal sealed class WarningCounter
    {
        internal sealed class Entry { public string Kind, Msg, At; public long N; }
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly int _capacity;
        public long Dropped { get; private set; }
        public WarningCounter(int capacity = int.MaxValue) => _capacity = capacity;
        public void Add(string kind, string msg, string at)
        {
            msg = msg ?? ""; at = at ?? "";
            if (msg.Length > 160) msg = msg.Substring(0, 160);
            int nl = at.IndexOf('\n'); if (nl >= 0) at = at.Substring(0, nl);
            if (at.Length > 160) at = at.Substring(0, 160);
            string key = kind + "\n" + msg + "\n" + at;
            if (_entries.TryGetValue(key, out var e)) { e.N++; return; }
            if (_entries.Count >= _capacity) { Dropped++; return; }
            _entries.Add(key, new Entry { Kind = kind, Msg = msg, At = at, N = 1 });
        }
        public WarningCounter Snapshot()
        {
            var copy = new WarningCounter(_capacity) { Dropped = Dropped };
            foreach (var pair in _entries)
            {
                var e = pair.Value;
                copy._entries.Add(pair.Key, new Entry { Kind = e.Kind, Msg = e.Msg, At = e.At, N = e.N });
            }
            return copy;
        }
        public string Json(int top = int.MaxValue, Func<string, string> redact = null)
        {
            var sorted = new List<Entry>(_entries.Values);
            sorted.Sort((a, b) => { int n = b.N.CompareTo(a.N); return n != 0 ? n : string.CompareOrdinal(a.Kind + a.Msg + a.At, b.Kind + b.Msg + b.At); });
            var b = new StringBuilder("[");
            for (int i = 0; i < Math.Min(top, sorted.Count); i++)
            {
                var e = sorted[i]; if (i > 0) b.Append(',');
                b.Append("{\"kind\":").Append(TLDRevamp.Json.Str(e.Kind)).Append(",\"msg\":").Append(TLDRevamp.Json.Str(redact != null ? redact(e.Msg) : e.Msg))
                 .Append(",\"at\":").Append(TLDRevamp.Json.Str(redact != null ? redact(e.At) : e.At)).Append(",\"n\":").Append(e.N).Append('}');
            }
            return b.Append(']').ToString();
        }
        public void Clear() { _entries.Clear(); Dropped = 0; }
    }
}
