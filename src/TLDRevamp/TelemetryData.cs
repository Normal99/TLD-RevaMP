using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace TLDRevamp
{
    // Kept free of Unity so the buffer and wire-to-disk formats can be exercised offline.
    internal static class Json
    {
        public static string Num(double n, string format = "F3") => double.IsNaN(n) || double.IsInfinity(n) ? "null" : n.ToString(Math.Abs(n) >= 1e15 ? "G17" : format, CultureInfo.InvariantCulture);
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
        public int Width => _phaseCount;
        /// Columns: st, ms, the first phaseNames.Length values, the fixed fields, then the remaining values (tailNames).
        public void Write(TextWriter w, string head, string[] phaseNames, double from = double.MinValue, double to = double.MaxValue, string[] tailNames = null, TelemetryState state = null, EventRing events = null, TelemetryHistory history = null, Func<byte, string> messageName = null, string[] timerNames = null)
        {
            int lead = Math.Min(phaseNames.Length, _phaseCount);
            w.Write("{\"head\":"); w.Write(head); w.Write(",\"overwritten\":"); w.Write(Overwritten);
            w.Write(",\"cols\":[\"st\",\"ms\"");
            for (int p = 0; p < lead; p++) { w.Write(','); w.Write(TelemetryPrivacy.CodeStr(phaseNames[p])); }
            w.Write(",\"gc0\",\"gc1\",\"gc2\",\"rebase\",\"stream\",\"saving\",\"loading\",\"gen\",\"bytesSent\",\"bytesReceived\",\"speedKmh\"");
            for (int p = lead; p < _phaseCount; p++) { w.Write(','); w.Write(TelemetryPrivacy.CodeStr(tailNames != null && p - lead < tailNames.Length ? tailNames[p - lead] : "col" + p)); }
            w.Write("],\"rows\":[");
            bool first = true;
            for (int i = 0; i < Count; i++)
            {
                var r = Row(i); if (r.Local < from || r.Local - r.Ms / 1000.0 > to) continue;
                if (!first) w.Write(','); first = false;
                w.Write('['); w.Write(Json.Num(r.St)); w.Write(','); w.Write(Json.Num(r.Ms));
                for (int p = 0; p < lead; p++) { w.Write(','); w.Write(Json.Num(Phase(i, p))); }
                w.Write(','); w.Write(r.Gc0); w.Write(','); w.Write(r.Gc1); w.Write(','); w.Write(r.Gc2);
                for (int bit = 1; bit <= 8; bit <<= 1) { w.Write(','); w.Write((r.Flags & bit) != 0 ? "1" : "0"); }
                w.Write(','); w.Write(r.Gen); w.Write(','); w.Write(r.Sent); w.Write(','); w.Write(r.Received);
                w.Write(','); w.Write(Json.Num(r.Speed));
                for (int p = lead; p < _phaseCount; p++) { w.Write(','); w.Write(Json.Num(Phase(i, p))); }
                w.Write(']');
            }
            w.Write(']');
            if (state != null) { w.Write(",\"state\":"); state.Write(w); }
            if (events != null) { w.Write(",\"events\":"); events.Write(w); }
            if (history != null) history.Write(w, messageName, timerNames);
            w.Write('}');
        }
    }

    /// Small fixed set; a pathological scene reports truncation instead of timing unlimited calls.
    internal sealed class CoarseClock
    {
        public const int CallLimit = 128;
        private readonly long[] _ticks;
        public readonly float[] Last;
        private int _calls;
        public CoarseClock(int groups) { _ticks = new long[groups]; Last = new float[groups + 1]; }
        public long Start() => ++_calls <= CallLimit ? Stopwatch.GetTimestamp() : 0;
        public void Stop(int group, long start) { if (start != 0) _ticks[group] += Stopwatch.GetTimestamp() - start; }
        public void FrameEnd()
        {
            for (int i = 0; i < _ticks.Length; i++) { Last[i] = (float)(_ticks[i] * (1000.0 / Stopwatch.Frequency)); _ticks[i] = 0; }
            Last[_ticks.Length] = _calls > CallLimit ? 1 : 0; _calls = 0;
        }
    }

    internal struct TelemetryEvent
    {
        public double St, Local;
        public string Kind, Result;
        public uint Net, Other, Epoch;
        public int Item, Slot, From, To, Count;
        public float Value, Dx, Dy, Dz;
    }

    /// Fixed history: bursts overwrite oldest records; quiet sessions expire by local monotonic time.
    internal sealed class EventRing
    {
        private readonly TelemetryEvent[] _rows;
        private int _head;
        public int Count { get; private set; }
        public long Overwritten { get; private set; }
        public EventRing(int capacity = 2000) { if (capacity < 1) throw new ArgumentOutOfRangeException(); _rows = new TelemetryEvent[capacity]; }
        public long Sampled;
        public TelemetryEvent Row(int i) => _rows[(_head - Count + i + _rows.Length) % _rows.Length];
        public void Clear() { Array.Clear(_rows, 0, _rows.Length); _head = Count = 0; Overwritten = Sampled = 0; }
        public void Add(TelemetryEvent row)
        {
            while (Count > 0 && row.Local - Row(0).Local > 35) Count--;
            if (Count == _rows.Length) Overwritten++; else Count++;
            _rows[_head] = row; _head = (_head + 1) % _rows.Length;
        }
        public void CopyTo(EventRing dst, double now)
        {
            dst.Clear();
            for (int i = 0; i < Count; i++) if (now - Row(i).Local <= 35) dst.Add(Row(i));
            dst.Overwritten = Overwritten;
            dst.Sampled = Sampled;
        }
        public void Write(TextWriter w)
        {
            w.Write("{\"overwritten\":" + Overwritten + ",\"sampled\":" + Sampled + ",\"rows\":[");
            for (int i = 0; i < Count; i++)
            {
                var e = Row(i); if (i > 0) w.Write(',');
                w.Write("{\"st\":" + Json.Num(e.St) + ",\"kind\":" + TelemetryPrivacy.Str(TelemetryPrivacy.EventKind(e.Kind)) + ",\"net\":" + e.Net + ",\"other\":" + e.Other +
                    ",\"epoch\":" + e.Epoch + ",\"item\":" + e.Item + ",\"slot\":" + e.Slot + ",\"from\":" + e.From + ",\"to\":" + e.To +
                    ",\"count\":" + e.Count + ",\"value\":" + Json.Num(e.Value) + ",\"dx\":" + Json.Num(e.Dx) + ",\"dy\":" + Json.Num(e.Dy) + ",\"dz\":" + Json.Num(e.Dz) + ",\"result\":" + TelemetryPrivacy.Str(TelemetryPrivacy.EventResult(e.Result)) + "}");
            }
            w.Write("]}");
        }
    }

    internal struct StateEntity
    {
        public uint Net, Epoch, Parent;
        public int Owner, Part, Prefab;
        public string Name;
        public double X, Y, Z, PoseSt;
        public float Yaw, Speed, Vx, Vy, Vz, Fuel, Key, Radio;
        public bool Proxy, Stored, Dormant, Car, Engine, Ignition, RadioOn, RadioAm;
    }
    internal struct StateSlot
    {
        public uint Net, ContentNet, LocalItem;
        public int Item, Index, ContentItem, Prefab, Condition, Condition2, Condition3;
        public string Name, Type, Content;
        public bool Attached, HasMount, Crafted;
        public float MountX, MountY, MountZ, MountRx, MountRy, MountRz;
    }
    internal struct StatePlayer
    {
        public int Id, Seat;
        public string Name;
        public uint Net;
        public double X, Y, Z, St, PoseSt;
        public bool HasPos;
        public bool InventoryComplete;
        public int InventoryCount;
    }
    // Kind 0 = held (equipped or grabbed), 2 = inventory. The wire's Held flag cannot distinguish hands.
    internal struct StateCarry { public int Player, Kind, Item, Prefab, Slot; public uint Net; public string Name; }
    internal struct StateUsable
    {
        public uint Net;
        public int Item, Index, Gen, Turn;
        public double X, Y, Z;
        public string Name;
        public float XRot, YRot, Slide, Tuned;
        public bool Building, BusDoor;
    }
    internal struct StateStorm { public double X, Z; public float Size, Speed, Life, Yaw; public int Prefab; }
    internal struct StateStation { public bool Am; public int Index, Clip; public float Time; }
    internal struct StateSeat { public uint Net; public int Index; public string Name; public bool InUse, InUseMulti; }

    /// Detached values only: worker serialization never touches Unity or mutable network objects.
    internal sealed class TelemetryState
    {
        public const int MaxBytes = 524288;
        public readonly StateEntity[] Entities = new StateEntity[512];
        public readonly StateSlot[] Slots = new StateSlot[2048];
        public readonly StatePlayer[] Players = new StatePlayer[128];
        public readonly StateStorm[] Storms = new StateStorm[64];
        public readonly StateStation[] Stations = new StateStation[128];
        public readonly StateSeat[] Seats = new StateSeat[256];
        public readonly StateCarry[] Carried = new StateCarry[1024];
        public readonly StateUsable[] Usables = new StateUsable[2048];
        public int CarryCount, UsableCount;
        public int EntityCount, SlotCount, PlayerCount, StormCount, StationCount, SeatCount, Dropped, Outside, Bytes, LocalPlayer;
        public double St, WorldTime, Day, X, Y, Z, CaptureMs;
        public bool HasCenter, Failed;
        public bool AllOwnedPhysics;
        public long PhysicsSkipped;
        public float Cloud, Sand;
        public TelemetryState() => Clear();
        public void Clear()
        {
            Array.Clear(Entities, 0, EntityCount); Array.Clear(Slots, 0, SlotCount); Array.Clear(Players, 0, PlayerCount);
            Array.Clear(Seats, 0, SeatCount);
            Array.Clear(Carried, 0, CarryCount); Array.Clear(Usables, 0, UsableCount); CarryCount = UsableCount = 0;
            EntityCount = SlotCount = PlayerCount = StormCount = StationCount = SeatCount = Dropped = Outside = 0;
            Bytes = 2048; LocalPlayer = -1; Failed = HasCenter = false; St = WorldTime = Day = X = Y = Z = CaptureMs = 0; Cloud = Sand = 0;
            AllOwnedPhysics = false; PhysicsSkipped = 0;
        }
        public static string Text(string value)
        {
            return TelemetryPrivacy.ObjectName(value);
        }
        private bool Take(int count, int capacity, int bytes)
        {
            if (count >= capacity || Bytes + bytes > MaxBytes) { Dropped++; return false; }
            Bytes += bytes; return true;
        }
        public bool Add(StateEntity e) { if (!Take(EntityCount, Entities.Length, 2048)) return false; e.Name = Text(e.Name); Entities[EntityCount++] = e; return true; }
        public bool Add(StateSlot s) { if (!Take(SlotCount, Slots.Length, 2048)) return false; s.Name = Text(s.Name); s.Type = Text(s.Type); s.Content = Text(s.Content); Slots[SlotCount++] = s; return true; }
        public bool Add(StatePlayer p) { if (!Take(PlayerCount, Players.Length, 1024)) return false; p.Name = ""; Players[PlayerCount++] = p; return true; }
        public void Add(StateStorm s) { if (Take(StormCount, Storms.Length, 512)) Storms[StormCount++] = s; }
        public void Add(StateStation s) { if (Take(StationCount, Stations.Length, 256)) Stations[StationCount++] = s; }
        public void Add(StateSeat s) { if (Take(SeatCount, Seats.Length, 512 + PlayerCount * 16)) { s.Name = Text(s.Name); Seats[SeatCount++] = s; } }
        public void Add(StateCarry s) { if (Take(CarryCount, Carried.Length, 640)) { s.Name = Text(s.Name); s.Kind = s.Kind == 2 ? 2 : 0; Carried[CarryCount++] = s; } }
        public void Add(StateUsable s) { if (Take(UsableCount, Usables.Length, 1024)) { s.Name = Text(s.Name); Usables[UsableCount++] = s; } }
        private static string Pos(double x, double y, double z) => "[" + Json.Num(x * 100, "F0") + "," + Json.Num(y * 100, "F0") + "," + Json.Num(z * 100, "F0") + "]";
        private static string Bool(bool b) => b ? "true" : "false";
        public void Write(TextWriter w)
        {
            w.Write("{\"st\":" + Json.Num(St) + ",\"localPlayer\":" + LocalPlayer + ",\"captureMs\":" + Json.Num(CaptureMs) + ",\"radiusM\":300,\"maxBytes\":" + MaxBytes + ",\"budgetBytes\":" + Bytes +
                ",\"dropped\":" + Dropped + ",\"outside\":" + Outside + ",\"failed\":" + Bool(Failed) + ",\"hasCenter\":" + Bool(HasCenter) + ",\"centerCm\":" + Pos(X, Y, Z) +
                ",\"time\":" + Json.Num(WorldTime) + ",\"day\":" + Json.Num(Day, "F0") + ",\"cloud\":" + Json.Num(Cloud) + ",\"sand\":" + Json.Num(Sand) + ",\"physicsAllOwned\":" + Bool(AllOwnedPhysics) + ",\"physicsSkipped\":" + PhysicsSkipped + ",\"players\":[");
            for (int i = 0; i < PlayerCount; i++)
            {
                var p = Players[i]; if (i > 0) w.Write(',');
                w.Write("{\"id\":" + p.Id + ",\"name\":" + TelemetryPrivacy.Str(TelemetryPrivacy.Player(p.Id)) + ",\"hasPos\":" + Bool(p.HasPos) + ",\"posCm\":" + Pos(p.X, p.Y, p.Z) + ",\"st\":" + Json.Num(p.St) + ",\"poseSt\":" + Json.Num(p.PoseSt) + ",\"net\":" + p.Net + ",\"seat\":" + p.Seat + ",\"inventoryComplete\":" + Bool(p.InventoryComplete) + ",\"inventoryCount\":" + p.InventoryCount);
                for (int kind = 0; kind < 2; kind++)
                {
                    w.Write(kind == 0 ? ",\"held\":[" : ",\"inventory\":["); bool sep = false;
                    for (int j = 0; j < CarryCount; j++)
                    {
                        var c = Carried[j]; if (c.Player != p.Id || (c.Kind == 2 ? 1 : 0) != kind) continue;
                        if (sep) w.Write(','); sep = true;
                        w.Write("{\"net\":" + c.Net + ",\"item\":" + c.Item + ",\"prefab\":" + c.Prefab + ",\"name\":" + TelemetryPrivacy.Str(Text(c.Name)) + ",\"kind\":" + c.Kind + ",\"slot\":" + c.Slot + "}");
                    }
                    w.Write(']');
                }
                w.Write('}');
            }
            w.Write("],\"usables\":[");
            for (int i = 0; i < UsableCount; i++)
            {
                var u = Usables[i]; if (i > 0) w.Write(',');
                w.Write("{\"net\":" + u.Net + ",\"item\":" + u.Item + ",\"index\":" + u.Index + ",\"name\":" + TelemetryPrivacy.Str(Text(u.Name)) + ",\"busDoor\":" + Bool(u.BusDoor) + ",\"building\":" + Bool(u.Building) + ",\"gen\":" + u.Gen + ",\"poi\":[" + Json.Num(u.X, "G17") + "," + Json.Num(u.Y, "G17") + "," + Json.Num(u.Z, "G17") + "],\"x\":" + Json.Num(u.XRot) + ",\"y\":" + Json.Num(u.YRot) + ",\"slide\":" + Json.Num(u.Slide) + ",\"turn\":" + u.Turn + ",\"tuned\":" + Json.Num(u.Tuned) + ",\"locked\":null}");
            }
            w.Write("],\"entities\":[");
            for (int i = 0; i < EntityCount; i++)
            {
                var e = Entities[i]; if (i > 0) w.Write(',');
                w.Write("{\"net\":" + e.Net + ",\"epoch\":" + e.Epoch + ",\"owner\":" + e.Owner + ",\"proxy\":" + Bool(e.Proxy) + ",\"prefab\":" + e.Prefab + ",\"name\":" + TelemetryPrivacy.Str(Text(e.Name)) +
                    ",\"posCm\":" + Pos(e.X, e.Y, e.Z) + ",\"poseSt\":" + Json.Num(e.PoseSt) + ",\"vel\":[" + Json.Num(e.Vx) + "," + Json.Num(e.Vy) + "," + Json.Num(e.Vz) + "]" +
                    ",\"yaw\":" + Json.Num(e.Yaw) + ",\"speed\":" + Json.Num(e.Speed) + ",\"stored\":" + Bool(e.Stored) + ",\"dormant\":" + Bool(e.Dormant) + ",\"parent\":" + e.Parent + ",\"part\":" + e.Part + ",\"car\":" + Bool(e.Car));
                if (e.Car && !e.Dormant) w.Write(",\"fuel\":" + Json.Num(e.Fuel) + ",\"engine\":" + Bool(e.Engine) + ",\"ignition\":" + Bool(e.Ignition) + ",\"key\":" + Json.Num(e.Key) + ",\"radio\":" + Json.Num(e.Radio) + ",\"radioOn\":" + Bool(e.RadioOn) + ",\"radioAm\":" + Bool(e.RadioAm));
                w.Write('}');
            }
            w.Write("],\"slots\":[");
            for (int i = 0; i < SlotCount; i++)
            {
                var s = Slots[i]; if (i > 0) w.Write(',');
                w.Write("{\"net\":" + s.Net + ",\"item\":" + s.Item + ",\"index\":" + s.Index + ",\"name\":" + TelemetryPrivacy.Str(Text(s.Name)) + ",\"type\":" + TelemetryPrivacy.Str(Text(s.Type)) + ",\"contentNet\":" + s.ContentNet +
                    ",\"contentItem\":" + s.ContentItem + ",\"localItem\":" + s.LocalItem + ",\"prefab\":" + s.Prefab + ",\"content\":" + TelemetryPrivacy.Str(Text(s.Content)) + ",\"attached\":" + Bool(s.Attached) + ",\"condition\":" + s.Condition + ",\"condition2\":" + s.Condition2 + ",\"condition3\":" + s.Condition3);
                if (s.HasMount) w.Write(",\"mountPosCm\":[" + Json.Num(s.MountX * 100) + "," + Json.Num(s.MountY * 100) + "," + Json.Num(s.MountZ * 100) + "],\"mountRot\":[" + Json.Num(s.MountRx) + "," + Json.Num(s.MountRy) + "," + Json.Num(s.MountRz) + "],\"crafted\":" + Bool(s.Crafted));
                w.Write('}');
            }
            w.Write("],\"seats\":[");
            for (int i = 0; i < SeatCount; i++)
            {
                var seat = Seats[i]; if (i > 0) w.Write(',');
                w.Write("{\"net\":" + seat.Net + ",\"index\":" + seat.Index + ",\"name\":" + TelemetryPrivacy.Str(Text(seat.Name)) + ",\"inUse\":" + Bool(seat.InUse) + ",\"inUseMulti\":" + Bool(seat.InUseMulti) + ",\"players\":[");
                bool first = true;
                for (int p = 0; p < PlayerCount; p++) if (Players[p].Net == seat.Net && Players[p].Seat == seat.Index)
                { if (!first) w.Write(','); first = false; w.Write(Players[p].Id); }
                w.Write("]}");
            }
            w.Write("],\"storms\":[");
            for (int i = 0; i < StormCount; i++)
            {
                var s = Storms[i]; if (i > 0) w.Write(',');
                w.Write("{\"prefab\":" + s.Prefab + ",\"posCm\":" + Pos(s.X, 0, s.Z) + ",\"yaw\":" + Json.Num(s.Yaw) + ",\"size\":" + Json.Num(s.Size) + ",\"speed\":" + Json.Num(s.Speed) + ",\"life\":" + Json.Num(s.Life) + "}");
            }
            w.Write("],\"stations\":[");
            for (int i = 0; i < StationCount; i++)
            {
                var s = Stations[i]; if (i > 0) w.Write(',');
                w.Write("{\"am\":" + Bool(s.Am) + ",\"index\":" + s.Index + ",\"clip\":" + s.Clip + ",\"time\":" + Json.Num(s.Time) + "}");
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
                b.Append("{\"kind\":").Append(TelemetryPrivacy.Str(TelemetryPrivacy.EventKind(e.Kind))).Append(",\"msg\":").Append(TelemetryPrivacy.Str(TelemetryPrivacy.FreeText(e.Msg)))
                 .Append(",\"at\":").Append(TelemetryPrivacy.Str(TelemetryPrivacy.FreeText(e.At))).Append(",\"n\":").Append(e.N).Append('}');
            }
            return b.Append(']').ToString();
        }
        public void Clear() { _entries.Clear(); Dropped = 0; }
    }
}
