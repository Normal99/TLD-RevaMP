using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace TLDRevamp
{
    internal interface IHistoryRow { double LocalTime { get; } }

    internal sealed class HistoryRing<T> where T : struct, IHistoryRow
    {
        private readonly T[] _rows;
        private int _head;
        public int Count { get; private set; }
        public long Overwritten { get; private set; }
        public HistoryRing(int capacity) { if (capacity < 1) throw new ArgumentOutOfRangeException(); _rows = new T[capacity]; }
        public T Row(int i) => _rows[(_head - Count + i + _rows.Length) % _rows.Length];
        public void Clear() { _head = Count = 0; Overwritten = 0; }
        public void Add(T row)
        {
            while (Count > 0 && row.LocalTime - Row(0).LocalTime > 35) Count--;
            if (Count == _rows.Length) Overwritten++; else Count++;
            _rows[_head] = row; _head = (_head + 1) % _rows.Length;
        }
        public void CopyTo(HistoryRing<T> dst, double now, double to = double.MaxValue)
        {
            dst.Clear();
            for (int i = 0; i < Count; i++) if (now - Row(i).LocalTime <= 35 && Row(i).LocalTime <= to) dst.Add(Row(i));
            dst.Overwritten += Overwritten;
        }
    }

    internal struct MotionRow : IHistoryRow
    {
        public double Local, St, Clock, X, Y, Z;
        public uint Net, Epoch;
        public int Owner, Kind, Mode, Item, Slot;
        public float Yaw, Speed, Interval, Age, Correction;
        public double LocalTime => Local;
    }

    internal sealed class EventSampler
    {
        private readonly double[] _until;
        public EventSampler(int capacity = 512) { if (capacity < 1) throw new ArgumentOutOfRangeException(); _until = new double[capacity]; }
        public void Clear() => Array.Clear(_until, 0, _until.Length);
        public bool Take(uint net, int item, int slot, int kind, double now)
        {
            int key = (int)((net * 397u ^ (uint)item * 31u ^ (uint)slot ^ (uint)kind) % (uint)_until.Length);
            if (now < _until[key]) return false;
            _until[key] = now + .5; return true;
        }
    }

    internal sealed class FrameSet
    {
        private readonly int[] _ids, _frames;
        private readonly bool[] _used;
        public FrameSet(int capacity = 512) { if (capacity < 1) throw new ArgumentOutOfRangeException(); _ids = new int[capacity]; _frames = new int[capacity]; _used = new bool[capacity]; }
        public int Add(int id, int frame)
        {
            int start = (int)((uint)id * 2654435761u % (uint)_ids.Length);
            for (int probe = 0; probe < 4; probe++)
            {
                int at = (start + probe) % _ids.Length;
                if (!_used[at] || _frames[at] != frame) { _used[at] = true; _ids[at] = id; _frames[at] = frame; return 1; }
                if (_ids[at] == id) return 0;
            }
            return -1;
        }
    }

    internal sealed class MotionTrace
    {
        public const int Capacity = 32768, FocusLimit = 8;
        internal readonly HistoryRing<MotionRow> Ring;
        private readonly uint[] _focus = new uint[FocusLimit], _nextFocus = new uint[FocusLimit];
        private readonly double[] _since = new double[FocusLimit], _nextSince = new double[FocusLimit];
        public long FocusDropped { get; private set; }
        public MotionTrace(int capacity = Capacity) => Ring = new HistoryRing<MotionRow>(capacity);
        public uint Focus(int i) => _focus[i];
        public bool Contains(uint net)
        {
            if (net == 0) return false;
            for (int i = 0; i < _focus.Length; i++) if (_focus[i] == net) return true;
            return false;
        }
        public void Clear() { Ring.Clear(); Array.Clear(_focus, 0, _focus.Length); Array.Clear(_since, 0, _since.Length); FocusDropped = 0; }
        public void SetFocus(uint[] ids, double now, double st)
        {
            Array.Clear(_nextFocus, 0, _nextFocus.Length);
            int n = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                uint net = ids[i]; if (net == 0 || Array.IndexOf(_nextFocus, net) >= 0) continue;
                if (n == _nextFocus.Length) { FocusDropped++; continue; }
                int old = Array.IndexOf(_focus, net);
                _nextFocus[n] = net; _nextSince[n++] = old >= 0 ? _since[old] : now;
            }
            for (int i = 0; i < _focus.Length; i++)
                if (_focus[i] != 0 && Array.IndexOf(_nextFocus, _focus[i]) < 0)
                    Ring.Add(new MotionRow { Net = _focus[i], Local = now, St = st, Kind = 6 });
            for (int i = 0; i < n; i++) if (!Contains(_nextFocus[i]))
                Ring.Add(new MotionRow { Net = _nextFocus[i], Local = now, St = st, Kind = 5 });
            Array.Copy(_nextFocus, _focus, _focus.Length); Array.Copy(_nextSince, _since, n);
        }
        public void Add(MotionRow row) { if (Contains(row.Net)) Ring.Add(row); }
        public void CopyTo(MotionTrace dst, double now)
        {
            Ring.CopyTo(dst.Ring, now, now); Array.Copy(_focus, dst._focus, _focus.Length); Array.Copy(_since, dst._since, _since.Length); dst.FocusDropped = FocusDropped;
        }
        private sealed class Rate { public double Start, Seconds; public int Sent, Received; public readonly Dictionary<(int, int), int[]> Usables = new Dictionary<(int, int), int[]>(); }
        public void Write(TextWriter w, double now, bool includeRows = true)
        {
            w.Write("{\"overwritten\":" + Ring.Overwritten + ",\"focusDropped\":" + FocusDropped + ",\"focus\":[");
            for (int i = 0; i < _focus.Length; i++) { if (i > 0) w.Write(','); w.Write(_focus[i]); }
            w.Write("],\"cols\":[\"local\",\"st\",\"net\",\"epoch\",\"owner\",\"kind\",\"clock\",\"xCm\",\"yCm\",\"zCm\",\"yaw\",\"speed\",\"interval\",\"age\",\"mode\",\"correction\",\"item\",\"slot\"],\"rows\":[");
            var rates = new Dictionary<uint, Rate>();
            double first = Ring.Count > 0 ? Math.Max(now - 35, Ring.Row(0).Local) : now;
            bool rowComma = false;
            for (int i = 0; i < Ring.Count; i++)
            {
                var r = Ring.Row(i);
                if (r.Local < now - 35 || r.Local > now) continue;
                if (includeRows)
                {
                    if (rowComma) w.Write(','); rowComma = true;
                    w.Write("[" + Json.Num(r.Local) + "," + Json.Num(r.St) + "," + r.Net + "," + r.Epoch + "," + r.Owner + "," + r.Kind + "," + Json.Num(r.Clock) + "," + Json.Num(r.X * 100, "F0") + "," + Json.Num(r.Y * 100, "F0") + "," + Json.Num(r.Z * 100, "F0") + "," + Json.Num(r.Yaw) + "," + Json.Num(r.Speed) + "," + Json.Num(r.Interval) + "," + Json.Num(r.Age) + "," + r.Mode + "," + Json.Num(r.Correction) + "," + r.Item + "," + r.Slot + "]");
                }
                int active = Array.IndexOf(_focus, r.Net);
                if (!rates.TryGetValue(r.Net, out var rate)) rates[r.Net] = rate = new Rate { Start = r.Kind == 5 ? r.Local : active >= 0 ? Math.Max(now - 35, Math.Min(first, _since[active])) : first };
                if (r.Kind == 5) rate.Start = r.Local;
                if (r.Kind == 6) { rate.Seconds += Math.Max(0, r.Local - rate.Start); rate.Start = double.NaN; }
                if (r.Kind == 0) rate.Sent++;
                if (r.Kind == 1) rate.Received++;
                if (r.Kind == 3 || r.Kind == 4)
                {
                    var key = (r.Item, r.Slot);
                    if (!rate.Usables.TryGetValue(key, out var counts)) rate.Usables[key] = counts = new int[2];
                    counts[r.Kind - 3]++;
                }
            }
            for (int i = 0; i < _focus.Length; i++) if (_focus[i] != 0 && !rates.ContainsKey(_focus[i])) rates[_focus[i]] = new Rate { Start = Math.Max(now - 35, _since[i]) };
            w.Write("],\"rates\":["); bool sep = false;
            foreach (var pair in rates)
            {
                var r = pair.Value; double seconds = r.Seconds + (double.IsNaN(r.Start) ? 0 : Math.Max(0, now - r.Start));
                if (sep) w.Write(','); sep = true;
                w.Write("{\"net\":" + pair.Key + ",\"seconds\":" + Json.Num(seconds) + ",\"sent\":" + r.Sent + ",\"received\":" + r.Received + ",\"sentHz\":" + Json.Num(seconds > 0 ? r.Sent / seconds : double.NaN) + ",\"receivedHz\":" + Json.Num(seconds > 0 ? r.Received / seconds : double.NaN) + ",\"usables\":[");
                bool comma = false;
                foreach (var u in r.Usables)
                {
                    if (comma) w.Write(','); comma = true;
                    w.Write("{\"item\":" + u.Key.Item1 + ",\"slot\":" + u.Key.Item2 + ",\"sent\":" + u.Value[0] + ",\"applied\":" + u.Value[1] + ",\"sendHz\":" + Json.Num(seconds > 0 ? u.Value[0] / seconds : double.NaN) + ",\"applyHz\":" + Json.Num(seconds > 0 ? u.Value[1] / seconds : double.NaN) + "}");
                }
                w.Write("]}");
            }
            w.Write("]}");
        }
    }

    internal struct TrafficCount
    {
        public long Sent, Received, OutBytes, InBytes, Ticks;
        public bool Any => Sent != 0 || Received != 0;
        public static TrafficCount Between(TrafficCount previous, TrafficCount early, TrafficCount current) => new TrafficCount {
            Sent = previous.Sent - early.Sent + current.Sent, Received = previous.Received - early.Received + current.Received,
            OutBytes = previous.OutBytes - early.OutBytes + current.OutBytes, InBytes = previous.InBytes - early.InBytes + current.InBytes,
            Ticks = previous.Ticks - early.Ticks + current.Ticks };
        public void Write(TextWriter w) => w.Write("\"sent\":" + Sent + ",\"received\":" + Received + ",\"outBytes\":" + OutBytes + ",\"inBytes\":" + InBytes + ",\"handleMs\":" + Json.Num(Ticks * 1000.0 / Stopwatch.Frequency));
    }

    internal sealed class TrafficInterval
    {
        internal static readonly string[] Categories = { "car", "trailer", "item", "rabbit/animal", "AI/munka", "ragdoll/corpse", "player", "other" };
        public readonly TrafficCount[] Types = new TrafficCount[256], CategoriesCount = new TrafficCount[8];
        public readonly int[] Owned = new int[8];
        public readonly TrafficCount[] Peers = new TrafficCount[128];
        public readonly int[] Players = new int[128];
        public int PeerCount;
        public long PeersDropped;
        public double Local, St, Seconds;
        public void Clear() { Array.Clear(Types, 0, Types.Length); Array.Clear(CategoriesCount, 0, CategoriesCount.Length); Array.Clear(Owned, 0, Owned.Length); PeerCount = 0; PeersDropped = 0; }
        public void CopyTo(TrafficInterval dst)
        {
            Array.Copy(Types, dst.Types, 256); Array.Copy(CategoriesCount, dst.CategoriesCount, 8); Array.Copy(Owned, dst.Owned, 8);
            dst.Local = Local; dst.St = St; dst.Seconds = Seconds;
            Array.Copy(Peers, dst.Peers, PeerCount); Array.Copy(Players, dst.Players, PeerCount); dst.PeerCount = PeerCount; dst.PeersDropped = PeersDropped;
        }
        public void Write(TextWriter w, Func<byte, string> name)
        {
            w.Write("{\"st\":" + Json.Num(St) + ",\"seconds\":" + Json.Num(Seconds) + ",\"types\":["); bool sep = false;
            for (int i = 0; i < 256; i++) if (Types[i].Any)
            {
                if (sep) w.Write(','); sep = true;
                w.Write("{\"name\":" + Json.Str(name((byte)i)) + ","); Types[i].Write(w); w.Write('}');
            }
            w.Write("],\"categories\":[");
            for (int i = 0; i < 8; i++)
            {
                if (i > 0) w.Write(',');
                w.Write("{\"name\":" + Json.Str(Categories[i]) + ",\"owned\":" + Owned[i] + ","); CategoriesCount[i].Write(w); w.Write('}');
            }
            w.Write("],\"peersDropped\":" + PeersDropped + ",\"peers\":[");
            for (int i = 0; i < PeerCount; i++)
            { if (i > 0) w.Write(','); w.Write("{\"player\":" + Players[i] + ","); Peers[i].Write(w); w.Write('}'); }
            w.Write("]}");
        }
    }

    internal sealed class PeerTrafficTable
    {
        public const int Capacity = 128;
        public readonly uint[] Keys = new uint[Capacity];
        public readonly bool[] Used = new bool[Capacity];
        public readonly TrafficCount[] Counts = new TrafficCount[Capacity];
        public long Dropped;
        public void Clear() { Array.Clear(Used, 0, Capacity); Array.Clear(Counts, 0, Capacity); Dropped = 0; }
        public void Add(uint key, bool sent, int bytes)
        {
            int start = (int)(key * 2654435761u % Capacity);
            for (int probe = 0; probe < 4; probe++)
            {
                int at = (start + probe) % Capacity;
                if (Used[at] && Keys[at] != key) continue;
                Used[at] = true; Keys[at] = key;
                if (sent) { Counts[at].Sent++; Counts[at].OutBytes += bytes; }
                else { Counts[at].Received++; Counts[at].InBytes += bytes; }
                return;
            }
            Dropped++;
        }
    }

    internal struct LongFrame : IHistoryRow
    {
        public double Local, St;
        public float Ms;
        public int Instantiated, Destroyed, Activated, Bodies, BodiesDropped, Moved, Terrain, Roads, Scenery, Buildings;
        public double LocalTime => Local;
    }

    internal sealed class LongHistory
    {
        public const int Capacity = 64, Width = 24;
        internal readonly HistoryRing<LongFrame> Ring;
        private readonly int _capacity;
        internal readonly TrafficCount[][] Network;
        internal readonly float[][] Timers;
        private int _next;
        public LongHistory(int capacity = Capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException();
            _capacity = capacity; Ring = new HistoryRing<LongFrame>(capacity); Network = new TrafficCount[capacity][]; Timers = new float[capacity][];
            for (int i = 0; i < capacity; i++) { Network[i] = new TrafficCount[256]; Timers[i] = new float[Width]; }
        }
        public void Clear() { Ring.Clear(); _next = 0; }
        public void Add(LongFrame frame, TrafficCount[] net, float[] timers)
        {
            Ring.Add(frame); Array.Copy(net, Network[_next], 256); Array.Copy(timers, Timers[_next], Math.Min(Width, timers.Length)); _next = (_next + 1) % _capacity;
        }
        public void CopyTo(LongHistory dst, double now)
        {
            dst.Clear();
            for (int i = 0; i < Ring.Count; i++)
            {
                var r = Ring.Row(i); int slot = (_next - Ring.Count + i + _capacity) % _capacity;
                if (now - r.Local <= 35) dst.Add(r, Network[slot], Timers[slot]);
            }
            Ring.CopyTo(dst.Ring, now);
        }
        public void CopyLastTo(LongHistory dst)
        {
            dst.Clear(); if (Ring.Count == 0) return;
            int slot = (_next - 1 + _capacity) % _capacity;
            dst.Add(Ring.Row(Ring.Count - 1), Network[slot], Timers[slot]);
        }
        public void WriteRow(TextWriter w, int i, Func<byte, string> name, string[] timers)
        {
            var r = Ring.Row(i); int slot = (_next - Ring.Count + i + _capacity) % _capacity;
            w.Write("{\"st\":" + Json.Num(r.St) + ",\"ms\":" + Json.Num(r.Ms) + ",\"instantiated\":" + r.Instantiated + ",\"destroyed\":" + r.Destroyed + ",\"activated\":" + r.Activated + ",\"bodiesAdded\":" + r.Bodies + ",\"bodiesDropped\":" + r.BodiesDropped + ",\"rebaseMoved\":" + r.Moved + ",\"genDone\":{\"terrain\":" + r.Terrain + ",\"roads\":" + r.Roads + ",\"scenery\":" + r.Scenery + ",\"buildings\":" + r.Buildings + "},\"network\":["); bool sep = false;
            for (int t = 0; t < 256; t++) if (Network[slot][t].Any)
            {
                if (sep) w.Write(','); sep = true;
                w.Write("{\"name\":" + Json.Str(name((byte)t)) + ","); Network[slot][t].Write(w); w.Write('}');
            }
            w.Write("],\"timers\":{"); sep = false;
            for (int t = 0; t < Math.Min(Width, timers.Length); t++) if (Timers[slot][t] != 0)
            { if (sep) w.Write(','); sep = true; w.Write(Json.Str(timers[t]) + ":" + Json.Num(Timers[slot][t])); }
            w.Write("}}");
        }
        public void Write(TextWriter w, Func<byte, string> name, string[] timers)
        {
            w.Write("{\"overwritten\":" + Ring.Overwritten + ",\"rows\":[");
            for (int i = 0; i < Ring.Count; i++) { if (i > 0) w.Write(','); WriteRow(w, i, name, timers); }
            w.Write("]}");
        }
    }

    internal sealed class TelemetryHistory
    {
        public readonly MotionTrace Trace = new MotionTrace();
        public readonly LongHistory Long = new LongHistory();
        public readonly TrafficInterval[] Network = new TrafficInterval[8];
        public int NetworkCount;
        public long NetworkOverwritten;
        public double Now;
        public TelemetryHistory() { for (int i = 0; i < Network.Length; i++) Network[i] = new TrafficInterval(); }
        public void Clear() { Trace.Clear(); Long.Clear(); NetworkCount = 0; NetworkOverwritten = 0; }
        public void Add(TrafficInterval interval)
        {
            while (NetworkCount > 0 && interval.Local - Network[0].Local > 35)
            {
                var first = Network[0]; Array.Copy(Network, 1, Network, 0, Network.Length - 1); Network[Network.Length - 1] = first; NetworkCount--;
            }
            if (NetworkCount == Network.Length)
            {
                var first = Network[0]; Array.Copy(Network, 1, Network, 0, Network.Length - 1); Network[Network.Length - 1] = first; NetworkCount--; NetworkOverwritten++;
            }
            interval.CopyTo(Network[NetworkCount++]);
        }
        public void CopyTo(TelemetryHistory dst, double now)
        {
            dst.Clear(); dst.Now = now; Trace.CopyTo(dst.Trace, now); Long.CopyTo(dst.Long, now);
            for (int i = 0; i < NetworkCount; i++) if (now - Network[i].Local <= 35) dst.Add(Network[i]);
            dst.NetworkOverwritten = NetworkOverwritten;
        }
        public void Write(TextWriter w, Func<byte, string> name, string[] timers)
        {
            w.Write(",\"trace\":"); Trace.Write(w, Now); w.Write(",\"long\":"); Long.Write(w, name, timers);
            w.Write(",\"network\":{\"overwritten\":" + NetworkOverwritten + ",\"rows\":[");
            for (int i = 0; i < NetworkCount; i++) { if (i > 0) w.Write(','); Network[i].Write(w, name); }
            w.Write("]}");
        }
    }
}
