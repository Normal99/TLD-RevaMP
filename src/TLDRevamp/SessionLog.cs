using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Profiling;

namespace TLDRevamp
{
    /// Local play log and black box. The hot path fills fixed arrays; JSON runs at record boundaries.
    public static class SessionLog
    {
        public static float Interval = 5f;
        public static string Sid { get; internal set; }
        public static string Dir => Path.Combine(FeedbackReporter.Root, "sessions");
        public static string CurrentPath => _path;
        internal static double FileTime => Math.Max(0, Time.realtimeSinceStartupAsDouble - _start);
        public static double St => Net.Mp.Client != null ? Net.ModObjects.ClockTime : Time.realtimeSinceStartupAsDouble;
        private static string Role => Net.Mp.Server != null ? (Net.DedicatedServer.Enabled ? "dedicated" : "host") : Net.Mp.Client != null ? "client" : "solo";
        private static StreamWriter _w;
        private static string _path, _header;
        private static object _context;
        private static bool _disabled;
        private static int _failed;
        private static double _start, _next, _warnAt, _intervalStart, _hitchA = -10, _hitchB = -10;
        private static int _hitches, _dropped;
        private static readonly int[] Hist = new int[10001];
        private static long _frames, _alloc, _heap, _sent, _received;
        private static float _max;
        private static readonly int[] LastGc = new int[3], IntervalGc = new int[3];
        // Black-box values per frame: FrameBreakdown phases, then FrameBreakdown.ExtraNames, then the script timer columns.
        private static int BaseWidth => FrameBreakdown.Names.Length + FrameBreakdown.ExtraNames.Length + ScriptProfiler.CoarseNames.Length;
        private static FrameRing Ring = new FrameRing(32768, BaseWidth);
        private static float[] _cols = new float[BaseWidth];
        private static string[] _tail = FrameBreakdown.ExtraNames.Concat(ScriptProfiler.CoarseNames).ToArray();
        private static bool _resize;
        private static readonly double[] Ext = new double[FrameBreakdown.ExtraNames.Length];
        private static long _gpuFrames;
        /// GC.GetAllocatedBytesForCurrentThread counts here (it returns 0 in the beta's Mono/Boehm runtime).
        internal static bool AllocCounter;
        private static TelemetryCounters _fixes;
        private static long _costTicks, _costFrames, _costBytes, _costMax, _intervalCost, _intervalBytes;
        private static int _terrain = -1, _roads = -1, _scenery = -1, _buildings = -1, _buildingsDeferred = -1, _items = -1;
        private static double _genAt;
        private static string _loadKind;
        private static double _loadStart, _usable = -1, _connect = -1, _worldData = -1, _firstState = -1;
        private static bool _loadWritten, _worldEntered;
        internal static bool Loading => _loadKind != null && !_loadWritten;
        private sealed class CaptureJob { public string Path, Head, SessionId; public double Local; public Action<string> Done; public Exception Error; public FrameRing Snapshot; public string[] Tail; public TelemetryState State; public EventRing Events; }
        private static readonly EventRing Events = new EventRing();
        private static readonly Queue<TelemetryState> States = new Queue<TelemetryState>();
        private static readonly Queue<EventRing> EventSnapshots = new Queue<EventRing>();
        private static readonly double[] LiveSamples = new double[512];
        private static readonly Queue<FrameRing> Snapshots = new Queue<FrameRing>();
        private static readonly List<CaptureJob> Captures = new List<CaptureJob>();
        private static readonly HashSet<string> Reserved = new HashSet<string>();
        private static readonly ConcurrentQueue<CaptureJob> Finished = new ConcurrentQueue<CaptureJob>();
        private static readonly List<Task> Writes = new List<Task>();
        private static int _writing;

        internal static void Install()
        {
            for (int i = 0; i < 4; i++) { Snapshots.Enqueue(new FrameRing(32768, BaseWidth)); States.Enqueue(new TelemetryState()); EventSnapshots.Enqueue(new EventRing()); }
            long before = GC.GetAllocatedBytesForCurrentThread(); var probe = new byte[8192]; GC.KeepAlive(probe);
            AllocCounter = GC.GetAllocatedBytesForCurrentThread() - before >= probe.Length;
            try { _fixes = new TelemetryCounters(); } catch (Exception e) { Fail("counter discovery", e); }
            for (int i = 0; i < 3; i++) LastGc[i] = GC.CollectionCount(i);
            _heap = GC.GetTotalMemory(false);
        }
        // Telemetry.Tick's stopwatch covers this work and its original frame/profiler sampling.
        /// The script timer columns changed: the black box is rebuilt with the new width once no capture is pending.
        internal static void TimerColumnsChanged() => _resize = true;
        private static void Resize()
        {
            int width = BaseWidth + ScriptProfiler.ColumnCount;
            var tail = FrameBreakdown.ExtraNames.Concat(ScriptProfiler.CoarseNames).Concat(ScriptProfiler.ColumnNames).ToArray();
            _resize = false;
            if (width == Ring.Width && tail.SequenceEqual(_tail)) return;
            // other methods in the columns: old rows would carry the wrong names, so the history restarts
            Ring = new FrameRing(32768, width);
            Snapshots.Clear();
            for (int i = 0; i < 4; i++) Snapshots.Enqueue(new FrameRing(32768, width));
            _cols = new float[width];
            _tail = tail;
        }
        internal static void Frame(float ms, FrameBreakdown breakdown)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            for (int i = Writes.Count - 1; i >= 0; i--) if (Writes[i].IsCompleted) Writes.RemoveAt(i);
            while (Finished.TryDequeue(out var finished))
            {
                _writing--; Snapshots.Enqueue(finished.Snapshot); States.Enqueue(finished.State); EventSnapshots.Enqueue(finished.Events);
                Reserved.Remove(finished.Path);
                if (finished.Error != null) Fail("black box", finished.Error);
                else if (finished.SessionId == Sid) finished.Done?.Invoke(finished.Path);
            }
            EnsureContext(now);
            TelemetryHooks.DrainSaves();
            if (_resize && Captures.Count == 0 && _writing == 0) Resize();
            if (_context == null) { FinishCaptures(now, false); return; }
            if (Net.Mp.Client != null && Sid == null) return;
            RecordFrame(now, ms, breakdown);
        }
        private static void EnsureContext(double now)
        {
            object ctx = _loadKind != null && _loadKind != "join" && !_worldEntered ? null : Net.Mp.Server != null ? (object)Net.Mp.Server : Net.Mp.Client != null ? Net.Mp.Client :
                !(_loadKind != null && !_worldEntered) && DataFromMenuScript.s != null && !DataFromMenuScript.s.mainmenu && mainscript.s != null && mainscript.s.started && menuhandler.s != null ? (object)menuhandler.s.currentMainMap : null;
            if (!ReferenceEquals(ctx, _context))
            {
                Close(); FinishCaptures(now, true); Ring.Clear(); Events.Clear(); Array.Clear(LiveSamples, 0, LiveSamples.Length); _context = ctx; _disabled = false;
                _sent = _received = 0;
                _costTicks = _costFrames = _costBytes = _costMax = 0;
                _terrain = _roads = _scenery = _buildings = _buildingsDeferred = _items = -1;
                for (int i = 0; i < 3; i++) LastGc[i] = GC.CollectionCount(i);
            }
            if (ctx == null) return;
            // Welcome supplies sid and an initial host clock estimate before the client's file opens.
            if (_w == null && !_disabled && (Net.Mp.Client == null || Sid != null)) Open(now);
        }
        private static void RecordFrame(double now, float ms, FrameBreakdown breakdown)
        {
            var ev = TelemetryHooks.Previous;
            long heap = GC.GetTotalMemory(false); _alloc += Math.Max(0, heap - _heap); _heap = heap;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            int d0 = g0 - LastGc[0], d1 = g1 - LastGc[1], d2 = g2 - LastGc[2];
            LastGc[0] = g0; LastGc[1] = g1; LastGc[2] = g2;
            var transport = Net.Mp.Server != null ? Net.Mp.Server.T : Net.Mp.Client?.T;
            long sent = transport?.BytesOut ?? 0, received = transport?.BytesIn ?? 0;
            var row = new TelemetryFrame { St = St, Local = now, Ms = ms, Speed = Speed(out _),
                Gc0 = d0, Gc1 = d1, Gc2 = d2, Flags = ev.Flags | (TelemetryHooks.Saving ? 4 : 0) | (Loading ? 8 : 0),
                Gen = GenTotal, Sent = Math.Max(0, sent - _sent), Received = Math.Max(0, received - _received) };
            row.ObserveGc(breakdown.GcMs);
            IntervalGc[0] += row.Gc0; IntervalGc[1] += row.Gc1; IntervalGc[2] += row.Gc2;
            _sent = sent; _received = received;
            int n = breakdown.LastMs.Length, x = breakdown.Extra.Length;
            Array.Copy(breakdown.LastMs, 0, _cols, 0, n);
            Array.Copy(breakdown.Extra, 0, _cols, n, x);
            Array.Copy(ScriptProfiler.Coarse.Last, 0, _cols, n + x, ScriptProfiler.CoarseNames.Length);
            for (int c = BaseWidth; c < _cols.Length; c++) _cols[c] = c - BaseWidth < ScriptProfiler.ColumnCount ? ScriptProfiler.Column(c - BaseWidth) : 0;
            for (int i = 0; i < x; i++) Ext[i] += breakdown.Extra[i];
            if (breakdown.Extra[FrameBreakdown.GpuBoundIndex] > 0) _gpuFrames++;
            Ring.Add(row, _cols, 40); // 30 before + 5 after, with margin for the finishing frame
            _frames++; Hist[Math.Max(0, Math.Min(Hist.Length - 1, (int)(ms * 10)))]++;
            _max = Math.Max(_max, ms);
            if (ms > 50)
            {
                _hitches++;
                if (now - _hitchA >= 1) { _hitchA = _hitchB; _hitchB = now; Hitch(row, ev, breakdown); }
                else _dropped++;
            }
            LoadMilestones();
            if (_w != null && now >= _next)
            {
                _next = now + Interval;
                try { Write(TickLine(now)); } catch (Exception e) { Fail("tick", e); }
                ResetInterval(now);
            }
            if (_w != null && now >= _warnAt)
            {
                _warnAt = now + 60;
                Write(Prefix("warn") + ",\"warnings\":" + Diagnostics.ErrorLog.IntervalJson() + "}");
            }
            FinishCaptures(now, false);
        }
        internal static void Cost(long ticks, long bytes)
        {
            _costTicks += ticks; _costFrames++; _costBytes += Math.Max(0, bytes); _costMax = Math.Max(_costMax, ticks);
            _intervalCost += ticks; _intervalBytes += Math.Max(0, bytes);
        }
        public static string Stats() => "{\"selfMs\":" + Json.Num(_costFrames > 0 ? _costTicks * 1000.0 / Stopwatch.Frequency / _costFrames : 0, "F6") +
            ",\"maxMs\":" + Json.Num(_costMax * 1000.0 / Stopwatch.Frequency) + ",\"allocBytesPerFrame\":" + (AllocCounter ? Json.Num(_costFrames > 0 ? (double)_costBytes / _costFrames : 0) : "null") +
            ",\"allocCounter\":" + (AllocCounter ? "true" : "false") + ",\"scriptTimers\":" + (ScriptProfiler.TimersOn ? "true" : "false") + ",\"columns\":" + Ring.Width +
            ",\"frames\":" + _costFrames + ",\"blackboxFrames\":" + Ring.Count + ",\"overwritten\":" + Ring.Overwritten + ",\"pending\":" + (Captures.Count + _writing) + "}";
        private static void Open(double now)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                _path = Unique(Dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Role, ".jsonl");
                _start = now; ResetInterval(now); _next = now + Interval; _warnAt = now + 60;
                _header = Head();
                TelemetryPrivacy.Name(Net.Session.MyName());
                _fixes?.Read(true);
                for (int i = 0; i < 3; i++) LastGc[i] = GC.CollectionCount(i);
                var transport = Net.Mp.Server != null ? Net.Mp.Server.T : Net.Mp.Client?.T;
                _sent = transport?.BytesOut ?? 0; _received = transport?.BytesIn ?? 0;
                _w = new StreamWriter(new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite));
                Write(_header);
                foreach (string f in Directory.GetFiles(Dir, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Skip(20))
                    try { File.Delete(f); } catch (Exception e) { Fail("prune", e); }
            }
            catch (Exception e) { _disabled = true; Fail("open", e); Close(); }
        }
        private static void ResetInterval(double now)
        {
            Array.Clear(Hist, 0, Hist.Length); _frames = _alloc = 0; _hitches = _dropped = 0; _max = 0; _intervalStart = now;
            _heap = GC.GetTotalMemory(false); _intervalCost = _intervalBytes = 0;
            Array.Clear(Ext, 0, Ext.Length); _gpuFrames = 0;
            for (int i = 0; i < 3; i++) IntervalGc[i] = 0;
        }
        private static float Percentile(double p)
        {
            long target = (long)(p * Math.Max(0, _frames - 1)), seen = 0;
            for (int i = 0; i < Hist.Length; i++) if ((seen += Hist[i]) > target) return i == Hist.Length - 1 ? _max : (i + .5f) / 10;
            return 0;
        }
        private static string TickLine(double now)
        {
            SampleGen();
            var p = mainscript.s != null ? mainscript.s.player : null;
            Vector3d pos = p != null ? mainscript.GlobalFromUnityPos(p.transform.position) : default;
            float speed = Speed(out bool driving);
            int cars = 0, awake = 0;
            if (mainscript.s?.Cars != null) foreach (var car in mainscript.s.Cars)
                if (car != null && p != null && (car.transform.position - p.transform.position).sqrMagnitude <= 1000000) cars++;
            if (savedatascript.s != null) foreach (var it in savedatascript.s.items.Values)
                if (it != null && it.RB != null && !it.RB.isKinematic && !it.RB.IsSleeping()) awake++;
            var b = new StringBuilder(Prefix("tick"));
            b.Append(",\"p50ms\":").Append(Json.Num(Percentile(.5))).Append(",\"p95ms\":").Append(Json.Num(Percentile(.95)))
             .Append(",\"p99ms\":").Append(Json.Num(Percentile(.99))).Append(",\"maxms\":").Append(Json.Num(_max))
             .Append(",\"frames\":").Append(_frames).Append(",\"hitches50\":").Append(_hitches).Append(",\"hitchesDropped\":").Append(_dropped);
            for (int i = 0; i < 3; i++) b.Append(",\"gc").Append(i).Append("\":").Append(IntervalGc[i]);
            b.Append(",\"allocKBps\":").Append(Json.Num(_alloc / 1024.0 / Math.Max(.001, now - _intervalStart)))
             .Append(",\"managedMB\":").Append(Json.Num(_heap / 1048576.0))
             .Append(",\"unityAllocMB\":").Append(Json.Num(Profiler.GetTotalAllocatedMemoryLong() / 1048576.0))
             .Append(",\"unityReservedMB\":").Append(Json.Num(Profiler.GetTotalReservedMemoryLong() / 1048576.0))
             .Append(",\"pos\":[").Append(Json.Num(Math.Round(pos.x / 10) * 10, "F0")).Append(',').Append(Json.Num(Math.Round(pos.z / 10) * 10, "F0")).Append(']')
             .Append(",\"speedKmh\":").Append(Json.Num(speed)).Append(",\"driving\":").Append(driving ? "true" : "false")
             .Append(",\"gen\":").Append(GenJson()).Append(",\"counts\":{\"items\":").Append(savedatascript.s != null ? savedatascript.s.items.Count : -1)
             .Append(",\"carsNearby\":").Append(cars).Append(",\"awakeRigidbodies\":").Append(awake).Append('}');
            // Where the frame went: CPU- vs GPU-bound frames, thread/GPU times and render statistics (interval averages).
            double f = Math.Max(1, _frames);
            b.Append(",\"bound\":{\"cpu\":").Append(_frames - _gpuFrames).Append(",\"gpu\":").Append(_gpuFrames).Append('}').Append(",\"frame\":{");
            for (int i = 0; i < FrameBreakdown.GpuBoundIndex; i++) b.Append(i > 0 ? "," : "").Append(Json.Str(FrameBreakdown.ExtraNames[i])).Append(':').Append(Json.Num(Ext[i] / f));
            b.Append("},\"render\":{");
            for (int i = FrameBreakdown.GpuBoundIndex + 1; i < Ext.Length; i++) b.Append(i > FrameBreakdown.GpuBoundIndex + 1 ? "," : "").Append(Json.Str(FrameBreakdown.ExtraNames[i])).Append(':').Append(Json.Num(Ext[i] / f, "F1"));
            b.Append('}');
            if (ScriptProfiler.TimersOn) b.Append(",\"scripts\":").Append(ScriptProfiler.TimersInterval(ScriptProfiler.TickTop));
            if (PlayerLoopProfiler.Running) b.Append(",\"loop\":").Append(PlayerLoopProfiler.IntervalJson(12));
            b
             .Append(",\"selfMs\":").Append(Json.Num(_frames > 0 ? _intervalCost * 1000.0 / Stopwatch.Frequency / _frames : 0, "F6"))
             .Append(",\"selfAllocBytesPerFrame\":").Append(AllocCounter ? Json.Num(_frames > 0 ? (double)_intervalBytes / _frames : 0) : "null")
             .Append(",\"fixes\":").Append(Fixes(true));
            if (Net.Mp.Server != null || Net.Mp.Client != null)
                b.Append(",\"net\":").Append(Safe(Net.Mp.NetStats))
                 .Append(",\"mp\":{\"remotePlayers\":").Append(Net.RemotePlayers.Count).Append(",\"statesIn\":").Append(Net.Entities.StatesIn)
                 .Append(",\"statesSent\":").Append(Net.Entities.StatesSent).Append(",\"stale\":").Append(Net.Entities.StatesStale)
                 .Append(",\"spawned\":").Append(Net.Entities.Spawned).Append(",\"ownerChanges\":").Append(Net.Entities.OwnerChanges).Append('}')
                 .Append(",\"ai\":").Append(Safe(Net.Entities.AiStats)).Append(",\"combat\":").Append(Safe(Net.PlayerCombat.Stats)).Append(",\"handoffs\":").Append(Net.Entities.Handoffs);
            return b.Append('}').ToString();
        }
        private static void Hitch(TelemetryFrame r, TelemetryHooks.Events ev, FrameBreakdown breakdown)
        {
            try
            {
                SampleGen();
                var b = new StringBuilder(Prefix("hitch")).Append(",\"ms\":").Append(Json.Num(r.Ms)).Append(",\"phases\":{"); bool first = true;
                for (int i = 0; i < breakdown.LastMs.Length; i++) if (breakdown.LastMs[i] != 0)
                {
                    if (!first) b.Append(','); first = false;
                    b.Append(Json.Str(FrameBreakdown.Names[i])).Append(':').Append(Json.Num(breakdown.LastMs[i]));
                }
                b.Append("},\"gc\":[").Append(r.Gc0).Append(',').Append(r.Gc1).Append(',').Append(r.Gc2).Append(']');
                string[] flags = { "rebase", "stream", "saving", "loading" };
                for (int i = 0; i < 4; i++) b.Append(',').Append(Json.Str(flags[i])).Append(':').Append((r.Flags & (1 << i)) != 0 ? "true" : "false");
                b.Append(",\"gen\":").Append(GenJson()).Append(",\"genDone\":{\"terrain\":").Append(ev.Terrain).Append(",\"roads\":").Append(ev.Roads)
                    .Append(",\"scenery\":").Append(ev.Scenery).Append(",\"buildings\":").Append(ev.Buildings).Append("},\"speedKmh\":").Append(Json.Num(r.Speed));
                var e = breakdown.Extra;
                b.Append(",\"gpuBound\":").Append(e[FrameBreakdown.GpuBoundIndex] > 0 ? "true" : "false").Append(",\"frame\":{");
                for (int i = 0; i < FrameBreakdown.GpuBoundIndex; i++) b.Append(i > 0 ? "," : "").Append(Json.Str(FrameBreakdown.ExtraNames[i])).Append(':').Append(Json.Num(e[i]));
                b.Append("},\"render\":{");
                for (int i = FrameBreakdown.GpuBoundIndex + 1; i < e.Length; i++) b.Append(i > FrameBreakdown.GpuBoundIndex + 1 ? "," : "").Append(Json.Str(FrameBreakdown.ExtraNames[i])).Append(':').Append(Json.Num(e[i], "F1"));
                b.Append('}');
                string top = ScriptProfiler.LastFrameTop();
                if (top != null) b.Append(",\"scripts\":").Append(top);
                string loop = PlayerLoopProfiler.LastFrameTop();
                if (loop != null) b.Append(",\"loop\":").Append(loop);
                Write(b.Append(",\"scene\":").Append(SceneCounts.OnHitch(Time.realtimeSinceStartupAsDouble)).Append('}').ToString());
            }
            catch (Exception e) { Fail("hitch", e); }
        }
        internal static void SampleGen()
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            _terrain = _roads = _scenery = _buildings = _buildingsDeferred = _items = -1; _genAt = Time.realtimeSinceStartupAsDouble;
            if (map == null) return;
            if (map.terrainGen != null)
            {
                _terrain = 0;
                foreach (var g in map.terrainGen) if (g != null && g.coordsNeeded != null && g.terrains != null)
                    foreach (var key in g.coordsNeeded) if (!g.terrains.ContainsKey(key)) _terrain++;
            }
            if (map.roadGens != null)
            {
                _roads = 0;
                foreach (var g in map.roadGens) if (g != null)
                    foreach (var key in g.chunksNeeded)
                        if (!g.chunks.TryGetValue(key, out var chunk) || !chunk.Done()) _roads++;
            }
            if (map.poiGens != null)
            {
                _buildings = TLDRevamp.Fixes.PoiBacklog.Sample(map.poiGens);
                _buildingsDeferred = TLDRevamp.Fixes.PoiBacklog.Deferred;
            }
            if (map.objGens != null)
            {
                _scenery = 0;
                foreach (var g in map.objGens) if (g != null && !g.disabled && g.categories != null)
                    foreach (var c in g.categories) if (c != null && !c.disable)
                        foreach (var key in c.chunksNeeded)
                            if (!c.generatedChunks.TryGetValue(key, out var chunk) || !chunk.generated) _scenery++;
            }
            // addFromFar remains populated after placement: it is not a pending queue.
        }
        private static int GenTotal => _terrain < 0 && _roads < 0 && _scenery < 0 && _buildings < 0 ? -1 : Math.Max(0, _terrain) + Math.Max(0, _roads) + Math.Max(0, _scenery) + Math.Max(0, _buildings);
        private static string GenJson() => "{\"terrain\":" + _terrain + ",\"roads\":" + _roads + ",\"buildings\":" + _buildings + ",\"buildingsDeferred\":" + _buildingsDeferred + ",\"scenery\":" + _scenery + ",\"items\":" + _items + ",\"sampleAgeS\":" + Json.Num(Time.realtimeSinceStartupAsDouble - _genAt) + "}";
        private static float Speed(out bool driving)
        {
            var p = mainscript.s != null ? mainscript.s.player : null;
            driving = p != null && p.Bsitting && p.seat != null && p.seat.driverSeat0;
            if (p == null) return 0;
            if (p.Bsitting && p.seat != null)
            {
                var rb = p.seat.Car != null ? p.seat.Car.RB : p.seat.RB;
                if (rb != null) return rb.velocity.magnitude * 3.6f;
            }
            return p.RB != null ? p.RB.velocity.magnitude * 3.6f : 0;
        }
        internal static string Head()
        {
            var b = new StringBuilder(Prefix("head")).Append(",\"start\":").Append(Json.Str(DateTime.Now.ToString("o")))
                .Append(",\"mod\":").Append(Json.Str(Plugin.Version)).Append(",\"build\":").Append(Json.Str(Plugin.BuildHash)).Append(",\"game\":").Append(Json.Str(Plugin.GameBuild))
                .Append(",\"role\":").Append(Json.Str(Role)).Append(",\"seed\":").Append(Net.Mp.Client != null ? Net.Mp.Client.ServerSeed : Net.Mp.Seed()).Append(",\"sid\":").Append(Role == "solo" || Sid == null ? "null" : Json.Str(Sid))
                .Append(",\"format\":2,\"stSource\":").Append(Json.Str(Role == "solo" ? "local" : "host"))
                .Append(",\"settings\":{\"width\":").Append(Screen.width).Append(",\"height\":").Append(Screen.height).Append(",\"fullscreen\":").Append(Screen.fullScreen ? "true" : "false")
                .Append(",\"quality\":").Append(Json.Str(QualitySettings.names[QualitySettings.GetQualityLevel()])).Append(",\"vsync\":").Append(QualitySettings.vSyncCount).Append(",\"targetFps\":").Append(Application.targetFrameRate);
            if (settingsscript.s?.S != null && settingsscript.s.S.GetDrawDistance() > 0 && !float.IsInfinity(settingsscript.s.S.GetDrawDistance())) b.Append(",\"viewDistance\":").Append(Json.Num(settingsscript.s.S.GetDrawDistance()));
            b.Append(",\"revamp\":{"); bool first = true;
            foreach (var e in RevampSettings.Entries) if (e.Key != null && e.Get != null)
            { if (!first) b.Append(','); first = false; b.Append(Json.Str(e.Key)).Append(':').Append(e.Get()); }
            b.Append("}},\"hw\":{\"cpu\":").Append(Json.Str(SystemInfo.processorType)).Append(",\"cores\":").Append(SystemInfo.processorCount)
             .Append(",\"ramMB\":").Append(SystemInfo.systemMemorySize).Append(",\"gpu\":").Append(Json.Str(SystemInfo.graphicsDeviceName))
             .Append(",\"vramMB\":").Append(SystemInfo.graphicsMemorySize).Append(",\"os\":").Append(Json.Str(SystemInfo.operatingSystem)).Append('}')
             .Append(",\"telemetry\":{\"scriptTimers\":").Append(ScriptProfiler.TimersOn ? "true" : "false").Append(",\"timerAlloc\":").Append(ScriptProfiler.TimerAlloc ? "true" : "false")
             .Append(",\"timerColumns\":").Append(ScriptProfiler.ColumnCount).Append(",\"coarseTimers\":").Append(ScriptProfiler.CoarseOn ? "true" : "false").Append(",\"coarseCallLimit\":").Append(CoarseClock.CallLimit).Append(",\"allocCounter\":").Append(AllocCounter ? "true" : "false").Append("}}");
            return b.ToString();
        }
        private static string Prefix(string kind) => "{\"k\":" + Json.Str(kind) + ",\"st\":" + Json.Num(St) + ",\"t\":" + Json.Num(Math.Max(0, Time.realtimeSinceStartupAsDouble - _start));
        internal static void Mark(string note, string by, double st)
        {
            EnsureContext(Time.realtimeSinceStartupAsDouble);
            Write("{\"k\":\"mark\",\"t\":" + Json.Num(Time.realtimeSinceStartupAsDouble - _start) + ",\"st\":" + Json.Num(st) + ",\"note\":" + Json.Str(note) + ",\"by\":" + Json.Str(by) + "}");
            try
            {
                string dir = Path.Combine(FeedbackReporter.Root, "marks"); Directory.CreateDirectory(dir);
                string basename = Net.ReportTransfer.MomentName(Sid, st, note);
                Capture(Unique(dir, basename, ".json"), st, Net.Reports.MarkCompletion(basename, note, st));
            }
            catch (Exception e) { Fail("mark", e); }
        }
        internal static bool Capture(string path, double moment, Action<string> done = null)
        {
            try
            {
                if (Snapshots.Count == 0) { Fail("capture capacity", new InvalidOperationException()); return false; }
                EnsureContext(Time.realtimeSinceStartupAsDouble);
                string head = _header ?? Head();
                head = head.Substring(0, head.Length - 1) + ",\"momentSt\":" + Json.Num(moment) + "}";
                Reserved.Add(path);
                var c = new CaptureJob { Path = path, Head = head, SessionId = Sid, Local = Time.realtimeSinceStartupAsDouble - Math.Max(0, St - moment), Done = done,
                    Snapshot = Snapshots.Dequeue(), State = States.Dequeue(), Events = EventSnapshots.Dequeue() };
                c.State.Clear();
                long start = Stopwatch.GetTimestamp();
                try { Net.Entities.CaptureTelemetry(c.State); } catch (Exception e) { c.State.Failed = true; Fail("state capture", e); }
                c.State.CaptureMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                Events.CopyTo(c.Events, Time.realtimeSinceStartupAsDouble);
                Captures.Add(c); return true;
            }
            catch (Exception e) { Fail("capture", e); return false; }
        }
        private static void FinishCaptures(double now, bool leaving)
        {
            for (int i = Captures.Count - 1; i >= 0; i--)
            {
                var c = Captures[i]; if (!leaving && now < c.Local + 5) continue;
                Captures.RemoveAt(i);
                try
                {
                    string head = c.Head.Substring(0, c.Head.Length - 1) + ",\"complete\":" + (!leaving ? "true" : "false") + "}";
                    Ring.CopyTo(c.Snapshot);
                    c.Tail = _tail;
                    _writing++;
                    Writes.Add(Task.Run(() =>
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(c.Path));
                            // The final name signals completion to the report zipper.
                            using (var w = new StreamWriter(c.Path + ".tmp", false, new UTF8Encoding(false))) c.Snapshot.Write(w, head, FrameBreakdown.Names, c.Local - 30, c.Local + 5, c.Tail, c.State, c.Events);
                            File.Move(c.Path + ".tmp", c.Path);
                        }
                        catch (Exception e) { c.Error = e; }
                        Finished.Enqueue(c);
                    }));
                }
                catch (Exception e) { Snapshots.Enqueue(c.Snapshot); States.Enqueue(c.State); EventSnapshots.Enqueue(c.Events); Reserved.Remove(c.Path); Fail("black box", e); }
            }
        }
        public static string Blackbox()
        {
            try
            {
                string dir = Path.Combine(FeedbackReporter.Root, "marks");
                string path = Unique(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-bridge", ".json");
                if (!Capture(path, St)) return "{\"error\":\"capture failed\"}";
                return "{\"path\":" + Json.Str(path) + ",\"readyAfterS\":5,\"frames\":" + Ring.Count + "}";
            }
            catch (Exception e) { Fail("bridge black box", e); return "{\"error\":\"capture failed\"}"; }
        }
        /// Bridge `telemetrytest [n]`: the per-frame sampling path (FrameBreakdown.Tick, the column copy and the black-box
        /// ring) run n times on scratch objects. Time per frame from Stopwatch; allocations from heap growth over the
        /// whole loop (best of three, so worker threads' allocations don't count), since this runtime has no per-thread counter.
        internal static string SelfTest(int n)
        {
            var bd = new FrameBreakdown();
            var ring = new FrameRing(32768, Ring.Width); var cols = new float[Ring.Width];
            double bestMs = double.MaxValue; long bestBytes = long.MaxValue;
            for (int run = 0; run < 3; run++)
            {
                long heap = GC.GetTotalMemory(false), t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < n; i++)
                {
                    bd.Tick(5f);
                    var row = new TelemetryFrame { St = i, Local = i * 0.001, Ms = 5f, Speed = Speed(out _), Flags = TelemetryHooks.Previous.Flags, Gen = GenTotal };
                    row.ObserveGc(bd.GcMs);
                    int a = bd.LastMs.Length, x = bd.Extra.Length;
                    Array.Copy(bd.LastMs, 0, cols, 0, a); Array.Copy(bd.Extra, 0, cols, a, x);
                    Array.Copy(ScriptProfiler.Coarse.Last, 0, cols, a + x, ScriptProfiler.CoarseNames.Length);
                    for (int c = BaseWidth; c < cols.Length; c++) cols[c] = c - BaseWidth < ScriptProfiler.ColumnCount ? ScriptProfiler.Column(c - BaseWidth) : 0;
                    ring.Add(row, cols, 40);
                }
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                long grew = GC.GetTotalMemory(false) - heap;
                bestMs = Math.Min(bestMs, ms); bestBytes = Math.Min(bestBytes, Math.Max(0, grew));
            }
            bd.Dispose();
            return "{\"frames\":" + n + ",\"msPerFrame\":" + Json.Num(bestMs / n, "F6") + ",\"heapGrowthBytes\":" + bestBytes +
                ",\"bytesPerFrame\":" + Json.Num((double)bestBytes / n, "F3") + ",\"columns\":" + Ring.Width + ",\"live\":" + Stats() + "}";
        }
        internal static Func<string> FixesSnapshot() => _fixes?.ReportSnapshot() ?? (() => "{}");
        internal static string Fixes(bool delta = false) => _fixes?.Read(delta) ?? "{}";
        internal static void BeginLoad(string kind)
        {
            EndWorld(); _loadKind = kind; _loadStart = Time.realtimeSinceStartupAsDouble;
            _usable = _connect = _worldData = _firstState = -1; _loadWritten = false; _worldEntered = false;
        }
        internal static void JoinStage(string stage)
        {
            if (_loadKind != "join") return;
            double elapsed = Time.realtimeSinceStartupAsDouble - _loadStart;
            if (stage == "connect" && _connect < 0) _connect = elapsed;
            if (stage == "worldData" && _worldData < 0) _worldData = elapsed;
            if (stage == "firstState" && _firstState < 0) { _firstState = elapsed; if (_loadWritten) WriteLoad("firstState"); }
        }
        internal static void WorldEntered()
        {
            _worldEntered = true;
        }
        internal static void PlayerUpdated(fpscontroller player)
        {
            if (!Loading || !_worldEntered || _usable >= 0 || mainscript.s?.player != player) return;
            var map = menuhandler.s?.currentMainMap;
            if (map == null || !map.firstRun || !player.loadedmove || player.RB == null || player.RB.isKinematic || DataFromMenuScript.s.load ||
                !mainscript.s.crsrLocked || mainscript.s.pauseMenuOpen || Time.timeScale <= 0 ||
                (Net.Mp.Client != null && !Net.Mp.Client.Ready)) return;
            _usable = Time.realtimeSinceStartupAsDouble - _loadStart;
        }
        private static void LoadMilestones()
        {
            if (!Loading || _w == null || _usable < 0) return;
            WriteLoad("firstUsable"); _loadWritten = true;
            // No persistent streaming queue or starting-area completion signal exists in this beta.
        }
        private static void WriteLoad(string stage)
        {
            string line = Prefix("load") + ",\"kind\":" + Json.Str(_loadKind) + ",\"stage\":" + Json.Str(stage) + ",\"firstUsableS\":" + Known(_usable);
            if (_loadKind == "join") line += ",\"connectS\":" + Known(_connect) + ",\"worldDataS\":" + Known(_worldData) + ",\"firstStateS\":" + Known(_firstState);
            Write(line + "}");
        }
        private static string Known(double value) => value < 0 ? "null" : Json.Num(value);
        internal static void Save(double ms, bool auto, long bytes, string playLog, double st, double t)
        {
            if (playLog == null) return;
            string line = "{\"k\":\"save\",\"st\":" + Json.Num(st) + ",\"t\":" + Json.Num(t) + ",\"ms\":" + Json.Num(ms) +
                ",\"auto\":" + (auto ? "true" : "false") + (bytes >= 0 ? ",\"bytes\":" + bytes : "") + "}";
            if (playLog == _path) Write(line);
            else try
            {
                // Async completion can arrive after leaving. Append to its original file, never the new world's log.
                if (File.Exists(playLog)) using (var w = new StreamWriter(playLog, true)) w.WriteLine(line);
            }
            catch (Exception e) { Fail("late save", e); }
        }
        internal static void EndWorld(bool sceneStop = false)
        {
            Close(); _context = null;
            // Deferred MP teardown must not erase the menu action that started the next world.
            if (!sceneStop || _worldEntered || _loadKind == "join") { _loadKind = null; _worldEntered = false; }
            FinishCaptures(Time.realtimeSinceStartupAsDouble, true); Ring.Clear(); Events.Clear(); Array.Clear(LiveSamples, 0, LiveSamples.Length);
        }
        internal static void Shutdown()
        {
            TelemetryHooks.DrainSaves(); EndWorld();
            // Unity is about to exit: give already-created local snapshots a bounded chance to reach disk.
            try { if (Writes.Count > 0 && !Task.WaitAll(Writes.ToArray(), 2000)) Fail("shutdown", new TimeoutException()); }
            catch (Exception e) { Fail("shutdown", e); }
        }
        internal static bool CapturePending(string path) => Reserved.Contains(path);
        /// Literal kinds/reasons and value fields keep gameplay hooks allocation-free.
        internal static void Event(string kind, uint net = 0, uint other = 0, int item = -1, int slot = -1, int from = -1, int to = -1, string result = "ok", uint epoch = 0, int count = 0, bool sample = false)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (sample)
            {
                int key = (int)((net * 397u ^ (uint)item * 31u ^ (uint)slot ^ (kind == "live-sent" ? 1u : 0u)) % (uint)LiveSamples.Length);
                if (now < LiveSamples[key]) return;
                LiveSamples[key] = now + .5;
            }
            var e = new TelemetryEvent { St = St, Local = now, Kind = kind, Net = net, Other = other, Item = item, Slot = slot, From = from, To = to, Result = result, Epoch = epoch, Count = count };
            Events.Add(e);
            for (int i = 0; i < Captures.Count; i++) if (now <= Captures[i].Local + 5) Captures[i].Events.Add(e);
        }
        public static void Flush() { try { _w?.Flush(); } catch (Exception e) { Fail("flush", e); } }
        private static void Write(string line)
        {
            if (_w == null) return;
            try { _w.WriteLine(line); _w.Flush(); }
            catch (Exception e) { _disabled = true; Fail("write", e); Close(); }
        }
        private static void Close()
        {
            try { _w?.Dispose(); } catch (Exception e) { Fail("close", e); }
            _w = null; _path = _header = null;
        }
        public static string[] Recent(int n)
        {
            try { return Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Take(n).ToArray() : Array.Empty<string>(); }
            catch (Exception e) { Fail("list", e); return Array.Empty<string>(); }
        }
        internal static string Unique(string dir, string name, string extension)
        {
            string path = Path.Combine(dir, name + extension); int i = 1;
            while (File.Exists(path) || Reserved.Contains(path)) path = Path.Combine(dir, name + "-" + i++ + extension);
            return path;
        }
        internal static void Fail(string operation, Exception e)
        {
            if (System.Threading.Interlocked.Exchange(ref _failed, 1) != 0) return;
            Plugin.Log.LogWarning("Telemetry " + operation + " failed (further failures suppressed): " + e.GetType().Name);
        }
        private static string Safe(Func<string> f) { try { return f() ?? "null"; } catch { return "null"; } }
    }
}
