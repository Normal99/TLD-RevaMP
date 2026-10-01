using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine.LowLevel;

namespace TLDRevamp
{
    /// Times every phase of Unity's frame (physics, script updates, animation, rendering, cleanup…) in the release
    /// player, where Unity's own profiler markers aren't available. It inserts a timestamp probe before every
    /// second-level PlayerLoop subsystem and after the last one. The time between consecutive probes is that
    /// subsystem's cost. Frames whose summed main-thread time exceeds SlowMs are tallied separately.
    /// Off by default; `loopprof start|stop` on the bridge. Allocation-free while running.
    public static class PlayerLoopProfiler
    {
        public static double SlowMs = 25.0; // bridge `set TLDRevamp.PlayerLoopProfiler.SlowMs`

        private static PlayerLoopSystem _original;
        private static bool _running;
        private static string[] _names;
        private static long[] _stamps;       // probe timestamps for the current frame
        private static double[] _sumAll, _sumSlow, _max;
        private static long _frames, _slowFrames;
        private static int _nextProbe;
        private static int _finishIdx = -1;
        private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        /// Hitch log: every frame whose wall time (end of the previous frame to the end of this one) exceeds HitchMs,
        /// with where the time went — the main thread's PlayerLoop total, its 3 costliest phases, a GC collection that
        /// frame, the bridge's own work that frame. Wall time far above the loop total = time spent outside the
        /// PlayerLoop (driver/present, the OS, Proton). Fixed ring, allocation-free.
        public static double HitchMs = 50.0; // bridge `set TLDRevamp.PlayerLoopProfiler.HitchMs`
        private const int HitchCap = 256;
        private static readonly float[] _hT = new float[HitchCap];
        private static readonly double[] _hWall = new double[HitchCap], _hLoop = new double[HitchCap], _hBridge = new double[HitchCap];
        private static readonly int[] _hGc = new int[HitchCap], _hFrame = new int[HitchCap];
        private static readonly int[,] _hTop = new int[HitchCap, 3];
        private static readonly double[,] _hTopMs = new double[HitchCap, 3];
        private static int _hitches;
        private static long _lastEnd;
        private static int _lastGc;
        private static double[] _frameMs;

        public static string Start()
        {
            if (_running) return "{\"error\":\"already running\"}";
            _original = PlayerLoop.GetCurrentPlayerLoop();
            var names = new List<string>();
            var root = _original;
            var tops = root.subSystemList.ToArray();
            int probe = 0;
            for (int t = 0; t < tops.Length; t++)
            {
                var subs = tops[t].subSystemList ?? new PlayerLoopSystem[0];
                var list = new List<PlayerLoopSystem>();
                string topName = tops[t].type != null ? tops[t].type.Name : "top" + t;
                if (subs.Length == 0)
                {
                    // Top-level system with no children: probe before it only
                    list.Add(Probe(probe++));
                    names.Add(topName);
                }
                foreach (var sub in subs)
                {
                    list.Add(Probe(probe++));
                    names.Add(topName + "." + (sub.type != null ? sub.type.Name : "?"));
                    list.Add(sub);
                }
                tops[t].subSystemList = list.ToArray();
            }
            // Final probe at the very end closes the last interval and finishes the frame
            var end = new PlayerLoopSystem { type = typeof(PlayerLoopProfiler), updateDelegate = EndFrame };
            var lastSubs = tops[tops.Length - 1].subSystemList.ToList();
            lastSubs.Add(end);
            tops[tops.Length - 1].subSystemList = lastSubs.ToArray();
            root.subSystemList = tops;

            _names = names.ToArray();
            _stamps = new long[_names.Length + 1];
            _sumAll = new double[_names.Length];
            _sumSlow = new double[_names.Length];
            _max = new double[_names.Length];
            _frames = _slowFrames = 0;
            _nextProbe = 0;
            _frameMs = new double[_names.Length];
            _hitches = 0; _lastEnd = 0; _lastGc = GC.CollectionCount(0);
            PlayerLoop.SetPlayerLoop(root);
            _running = true;
            return "{\"probes\":" + _names.Length + "}";
        }

        private static PlayerLoopSystem Probe(int index)
        {
            return new PlayerLoopSystem
            {
                type = typeof(PlayerLoopProfiler),
                updateDelegate = () =>
                {
                    // FixedUpdate subsystems can run 0..n times per frame; only the first run's stamp counts,
                    // extra runs land in the following interval (documented limitation).
                    _stamps[index] = Stopwatch.GetTimestamp();
                    _nextProbe = index + 1;
                }
            };
        }

        private static void EndFrame()
        {
            long now = Stopwatch.GetTimestamp();
            _stamps[_names.Length] = now;
            int n = _names.Length;
            if (_finishIdx < 0) _finishIdx = Array.IndexOf(_names, "PostLateUpdate.FinishFrameRendering");
            if (_finishIdx >= 0 && _stamps[_finishIdx] != 0 && _stamps[_finishIdx + 1] != 0)
                RenderLab.FinishFrameBounds(_stamps[_finishIdx], _stamps[_finishIdx + 1]);
            double total = 0;
            // Interval i = stamps[i+1] - stamps[i]; skip intervals where a probe didn't fire this frame (stamp 0)
            for (int i = 0; i < n; i++)
            {
                if (_stamps[i] == 0 || _stamps[i + 1] == 0) continue;
                total += (_stamps[i + 1] - _stamps[i]) * TickMs;
            }
            bool slow = total > SlowMs;
            _frames++;
            if (slow) _slowFrames++;
            for (int i = 0; i < n; i++)
            {
                if (_stamps[i] == 0 || _stamps[i + 1] == 0) continue;
                double ms = (_stamps[i + 1] - _stamps[i]) * TickMs;
                _sumAll[i] += ms;
                if (slow) _sumSlow[i] += ms;
                if (ms > _max[i]) _max[i] = ms;
            }
            for (int i = 0; i < n; i++) _frameMs[i] = _stamps[i] == 0 || _stamps[i + 1] == 0 ? 0 : (_stamps[i + 1] - _stamps[i]) * TickMs;
            int gc = GC.CollectionCount(0);
            double wall = _lastEnd != 0 ? (now - _lastEnd) * TickMs : 0;
            if (wall > HitchMs)
            {
                int h = _hitches % HitchCap;
                _hT[h] = UnityEngine.Time.realtimeSinceStartup; _hFrame[h] = UnityEngine.Time.frameCount;
                _hWall[h] = wall; _hLoop[h] = total; _hGc[h] = gc - _lastGc; _hBridge[h] = DebugBridge.LastPumpMs;
                for (int k = 0; k < 3; k++)
                {
                    int best = -1;
                    for (int i = 0; i < n; i++)
                    {
                        if (k > 0 && (i == _hTop[h, 0] || (k > 1 && i == _hTop[h, 1]))) continue;
                        if (best < 0 || _frameMs[i] > _frameMs[best]) best = i;
                    }
                    _hTop[h, k] = best; _hTopMs[h, k] = best >= 0 ? _frameMs[best] : 0;
                }
                _hitches++;
            }
            _lastEnd = now; _lastGc = gc;
            DebugBridge.LastPumpMs = 0;   // so a hitch frame's value is that frame's own bridge work
            Array.Clear(_stamps, 0, _stamps.Length);
        }

        private static void AppendHitches(StringBuilder sb)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            sb.Append(",\"hitchMs\":").Append(HitchMs.ToString(ic)).Append(",\"hitchCount\":").Append(_hitches).Append(",\"hitches\":[");
            int from = Math.Max(0, _hitches - HitchCap);
            for (int j = from; j < _hitches; j++)
            {
                int h = j % HitchCap;
                if (j > from) sb.Append(',');
                sb.Append("{\"t\":").Append(_hT[h].ToString("F2", ic)).Append(",\"frame\":").Append(_hFrame[h])
                  .Append(",\"wall\":").Append(_hWall[h].ToString("F1", ic)).Append(",\"loop\":").Append(_hLoop[h].ToString("F1", ic))
                  .Append(",\"gc\":").Append(_hGc[h]).Append(",\"bridge\":").Append(_hBridge[h].ToString("F1", ic)).Append(",\"top\":[");
                for (int k = 0; k < 3; k++)
                {
                    if (k > 0) sb.Append(',');
                    int i = _hTop[h, k];
                    sb.Append('[').Append(Json.Str(i >= 0 ? _names[i] : "?")).Append(',').Append(_hTopMs[h, k].ToString("F1", ic)).Append(']');
                }
                sb.Append("]}");
            }
            sb.Append(']');
        }

        public static string Stop(int top)
        {
            if (!_running) return "{\"error\":\"not running\"}";
            PlayerLoop.SetPlayerLoop(_original);
            _running = false;
            var idx = Enumerable.Range(0, _names.Length).OrderByDescending(i => _sumSlow[i]).Take(top);
            var sb = new StringBuilder();
            sb.Append("{\"frames\":").Append(_frames).Append(",\"slowFrames\":").Append(_slowFrames)
              .Append(",\"slowThresholdMs\":").Append(SlowMs).Append(",\"phases\":[");
            bool first = true;
            foreach (int i in idx)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"p\":").Append(Json.Str(_names[i]))
                  .Append(",\"avgMsAll\":").Append((_frames > 0 ? _sumAll[i] / _frames : 0).ToString("F3"))
                  .Append(",\"avgMsSlow\":").Append((_slowFrames > 0 ? _sumSlow[i] / _slowFrames : 0).ToString("F3"))
                  .Append(",\"maxMs\":").Append(_max[i].ToString("F2")).Append('}');
            }
            sb.Append(']');
            AppendHitches(sb);
            return sb.Append('}').ToString();
        }
    }
}
