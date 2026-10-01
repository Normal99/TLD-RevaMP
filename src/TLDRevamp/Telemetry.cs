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
            float ms = Time.unscaledDeltaTime * 1000f;
            _frameMs[_head] = ms;
            _head = (_head + 1) % Window;
            if (_count < Window) _count++;
            if (ms > HitchMs) Hitches++;
            _hist[Mathf.Clamp((int)(ms * 10f), 0, Buckets - 1)]++;
            if (ms > _maxMs) _maxMs = ms;
            Breakdown.Tick(ms);
            Frames++;
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

        /// Expensive: walks the scene. Call on demand, not per frame.
        public string SceneStatsJson()
        {
            var bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
            int awake = bodies.Count(b => !b.IsSleeping());
            int renderers = UnityEngine.Object.FindObjectsOfType<Renderer>().Length;
            int behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>().Length;
            var player = mainscript.s != null ? mainscript.s.player : null;
            Vector3 pos = player != null ? player.transform.position : Vector3.zero;
            return new StringBuilder()
                .Append("{\"scene\":").Append(Json.Str(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name))
                .Append(",\"focused\":").Append(Application.isFocused ? "true" : "false")
                .Append(",\"rigidbodies\":").Append(bodies.Length)
                .Append(",\"rigidbodiesAwake\":").Append(awake)
                .Append(",\"renderers\":").Append(renderers)
                .Append(",\"monobehaviours\":").Append(behaviours)
                .Append(",\"playerPos\":[").Append(pos.x.ToString("F1")).Append(',').Append(pos.y.ToString("F1")).Append(',').Append(pos.z.ToString("F1")).Append(']')
                .Append('}').ToString();
        }

        public string FrameStatsJson() =>
            "{\"frames\":" + Frames +
            ",\"fps\":" + (RunPercentile(0.5f) > 0 ? (1000f / RunPercentile(0.5f)).ToString("F1") : "0") +
            ",\"p50ms\":" + RunPercentile(0.5f).ToString("F2") +
            ",\"p99ms\":" + RunPercentile(0.99f).ToString("F2") +
            ",\"p999ms\":" + RunPercentile(0.999f).ToString("F2") +
            ",\"maxms\":" + RunPercentile(1f).ToString("F2") +
            ",\"hitches50ms\":" + Hitches +
            ",\"gc0\":" + GcCollections +
            ",\"managedMB\":" + ManagedMB +
            ",\"bridgeMsTotal\":" + DebugBridge.TotalPumpMs.ToString("F1") +
            ",\"bridgeMaxMs\":" + DebugBridge.MaxPumpMs.ToString("F2") +
            ",\"time\":" + Time.realtimeSinceStartup.ToString("F1") + "}";
    }
}
