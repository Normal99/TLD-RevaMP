using System;
using System.Linq;
using System.Text;
using UnityEngine;

namespace TLDRevamp
{
    /// Frame timing + GC + scene counters. Allocation-free per frame.
    public class Telemetry
    {
        private const int Window = 600; // ~10s at 60fps
        private const float HitchMs = 50f;

        private readonly float[] _frameMs = new float[Window];
        private readonly float[] _sorted = new float[Window];
        private int _head, _count;
        private int _gcBaseline;

        private const int Buckets = 10000; // 0.1ms buckets up to 1000ms
        private readonly int[] _hist = new int[Buckets];

        public int Hitches { get; private set; }
        public long Frames { get; private set; }

        public readonly FrameBreakdown Breakdown = new FrameBreakdown();

        public Telemetry() => _gcBaseline = GC.CollectionCount(0);

        public void Tick()
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            try { Sample(); }
            catch (Exception e) { SessionLog.Fail("frame", e); }
            finally { SessionLog.Cost(System.Diagnostics.Stopwatch.GetTimestamp() - started, GC.GetAllocatedBytesForCurrentThread() - allocated); }
        }

        private void Sample()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            _frameMs[_head] = ms;
            _head = (_head + 1) % Window;
            if (_count < Window) _count++;
            if (ms > HitchMs) Hitches++;
            _hist[Mathf.Clamp((int)(ms * 10f), 0, Buckets - 1)]++;
            if (ms > _maxMs) _maxMs = ms;
            Breakdown.Tick(ms);
            Frames++;
            SessionLog.Frame(ms, Breakdown);
        }

        public void Reset()
        {
            _head = _count = 0;
            Hitches = 0;
            _maxMs = 0;
            Array.Clear(_hist, 0, Buckets);
            Frames = 0;
            Breakdown.Reset();
            DebugBridge.TotalPumpMs = 0;
            DebugBridge.MaxPumpMs = 0;
            _gcBaseline = GC.CollectionCount(0);
        }

        public float Percentile(float p)
        {
            if (_count == 0) return 0f;
            Array.Copy(_frameMs, _sorted, _count);
            Array.Sort(_sorted, 0, _count);
            return _sorted[Mathf.Clamp((int)(p * (_count - 1)), 0, _count - 1)];
        }

        private float _maxMs;

        /// Percentile over every frame since the last Reset (not just the ring window).
        public float RunPercentile(float p)
        {
            if (Frames == 0) return 0f;
            if (p >= 1f) return _maxMs;
            long target = (long)(p * Frames), seen = 0;
            for (int i = 0; i < Buckets; i++)
            {
                seen += _hist[i];
                if (seen > target) return (i + 0.5f) / 10f;
            }
            return _maxMs;
        }

        public float Last => _count == 0 ? 0f : _frameMs[(_head - 1 + Window) % Window];
        public int GcCollections => GC.CollectionCount(0) - _gcBaseline;
        public long ManagedMB => GC.GetTotalMemory(false) / (1024 * 1024);

        /// Samples the frame history into a small fixed buffer for graphing.
        public int CopyHistory(float[] dst)
        {
            int n = Math.Min(dst.Length, _count);
            for (int i = 0; i < n; i++)
                dst[i] = _frameMs[(_head - n + i + Window) % Window];
            return n;
        }

        private string _sceneJson;
        private double _sceneAt = -100;

        /// Expensive: walks the scene. Call on demand, not per frame.
        public string SceneStatsJson()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (_sceneJson != null && now - _sceneAt < 60) return _sceneJson;
            long scanStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
            int awake = bodies.Count(b => !b.IsSleeping());
            int renderers = UnityEngine.Object.FindObjectsOfType<Renderer>().Length;
            int behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>().Length;
            var player = mainscript.s != null ? mainscript.s.player : null;
            Vector3 pos = player != null ? player.transform.position : Vector3.zero;
            double scanMs = (System.Diagnostics.Stopwatch.GetTimestamp() - scanStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _sceneAt = now;
            _sceneJson = new StringBuilder()
                .Append("{\"scene\":").Append(Json.Str(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name))
                .Append(",\"focused\":").Append(Application.isFocused ? "true" : "false")
                .Append(",\"rigidbodies\":").Append(bodies.Length)
                .Append(",\"rigidbodiesAwake\":").Append(awake)
                .Append(",\"renderers\":").Append(renderers)
                .Append(",\"monobehaviours\":").Append(behaviours)
                .Append(",\"scanMs\":").Append(Json.Num(scanMs)).Append(",\"sampleSt\":").Append(Json.Num(SessionLog.St))
                .Append(",\"playerPos\":[").Append(pos.x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(pos.y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(pos.z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(']')
                .Append('}').ToString();
            return _sceneJson;
        }

        public string FrameStatsJson() =>
            "{\"frames\":" + Frames +
            ",\"fps\":" + (RunPercentile(0.5f) > 0 ? (1000f / RunPercentile(0.5f)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "0") +
            ",\"p50ms\":" + RunPercentile(0.5f).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"p99ms\":" + RunPercentile(0.99f).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"p999ms\":" + RunPercentile(0.999f).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"maxms\":" + RunPercentile(1f).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"hitches50ms\":" + Hitches +
            ",\"gc0\":" + GcCollections +
            ",\"managedMB\":" + ManagedMB +
            ",\"bridgeMsTotal\":" + DebugBridge.TotalPumpMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"bridgeMaxMs\":" + DebugBridge.MaxPumpMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"time\":" + Time.realtimeSinceStartup.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}";
    }
}
