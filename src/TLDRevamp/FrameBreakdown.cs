using System;
using System.Text;
using Unity.Profiling;

namespace TLDRevamp
{
    /// What's in the slow frames? Per frame: did a GC run, and how long did Unity's built-in phases take
    /// (via ProfilerRecorder, where the release player exposes them). Accumulated separately for all frames and for
    /// slow frames (> SlowMs) so we can see which phase grows in the slow ones. Allocation-free per frame.
    public class FrameBreakdown
    {
        public static float SlowMs = 25f; // bridge `set TLDRevamp.FrameBreakdown.SlowMs`

        private static readonly (ProfilerCategory cat, string name)[] Markers =
        {
            (ProfilerCategory.Internal, "PlayerLoop"),
            (ProfilerCategory.Scripts, "BehaviourUpdate"),
            (ProfilerCategory.Scripts, "LateBehaviourUpdate"),
            (ProfilerCategory.Scripts, "FixedBehaviourUpdate"),
            (ProfilerCategory.Physics, "Physics.Simulate"),
            (ProfilerCategory.Physics, "Physics.Processing"),
            (ProfilerCategory.Physics, "Physics.BakeCollisionMeshes"),
            (ProfilerCategory.Render, "Camera.Render"),
            (ProfilerCategory.Render, "RenderPipelineManager.DoRenderLoop_Internal"),
            (ProfilerCategory.Render, "Gfx.WaitForPresentOnGfxThread"),
            (ProfilerCategory.Render, "Gfx.PresentFrame"),
            (ProfilerCategory.Render, "Mesh.UploadMeshData"),
            (ProfilerCategory.Memory, "GC.Collect"),
            (ProfilerCategory.Loading, "Loading.UpdatePreloading"),
            (ProfilerCategory.Internal, "Main Thread"),
        };

        /// Counters the shipped player fills (checked with bridge `counters`, 2026-10-18): render statistics of the
        /// previous frame and FrameTimingManager's thread/GPU times (nanoseconds). No physics or allocation counters exist
        /// outside development builds.
        private static readonly (ProfilerCategory cat, string name)[] CounterDefs =
        {
            (ProfilerCategory.Render, "CPU Main Thread Frame Time"),
            (ProfilerCategory.Render, "CPU Render Thread Frame Time"),
            (ProfilerCategory.Render, "GPU Frame Time"),
            (ProfilerCategory.Render, "Draw Calls Count"),
            (ProfilerCategory.Render, "Batches Count"),
            (ProfilerCategory.Render, "SetPass Calls Count"),
            (ProfilerCategory.Render, "Triangles Count"),
            (ProfilerCategory.Render, "Shadow Casters Count"),
        };
        /// Per-frame derived values (black-box columns after the fixed fields, and tick averages):
        /// busyMs = frame minus the wait for the GPU (the larger of the Gfx.WaitForPresentOnGfxThread marker and, when the
        /// player-loop phases run, TimeUpdate.WaitForLastPresentationAndUpdateTime: in this player the marker stays near 0
        /// while the main thread waits for the previous frame's present in that phase);
        /// gpuBound = 1 when the GPU's frame time (FrameTimingManager) is at least 85% of the frame, or, without a GPU time,
        /// when the GPU wait exceeds 0.5 ms and 20% of the frame; else 0 (CPU-bound: main or render thread).
        internal static readonly string[] ExtraNames = { "busyMs", "mainThreadMs", "renderThreadMs", "gpuMs", "gpuBound", "drawCalls", "batches", "setPass", "trianglesK", "shadowCasters" };
        internal const int GpuBoundIndex = 4;
        internal readonly float[] Extra = new float[ExtraNames.Length];
        private readonly ProfilerRecorder[] _counters = new ProfilerRecorder[CounterDefs.Length];
        private double[] _extraAll = new double[ExtraNames.Length], _extraSlow = new double[ExtraNames.Length];
        private long _gpuBound, _slowGpuBound;
        private bool _countersValid;

        private readonly ProfilerRecorder[] _rec = new ProfilerRecorder[Markers.Length];
        internal float GcMs => LastMs[12];
        internal readonly float[] LastMs = new float[Markers.Length];
        internal static readonly string[] Names = Array.ConvertAll(Markers, m => m.name);
        private double[] _sumAll = new double[Markers.Length], _sumSlow = new double[Markers.Length];
        private bool[] _everValid = new bool[Markers.Length];
        private long _frames, _slow, _gcFrames, _slowWithGc;
        private double _msGcFrames, _msNoGcFrames;
        private int _lastGc;

        public FrameBreakdown()
        {
            for (int i = 0; i < Markers.Length; i++)
            {
                try { _rec[i] = ProfilerRecorder.StartNew(Markers[i].cat, Markers[i].name, 1); }
                catch { }
            }
            for (int i = 0; i < CounterDefs.Length; i++)
            {
                try { _counters[i] = ProfilerRecorder.StartNew(CounterDefs[i].cat, CounterDefs[i].name, 1); }
                catch { }
            }
            _lastGc = GC.CollectionCount(0);
        }

        /// Runtime switches for the counter groups (bridge `set`, -tldset): FrameTiming = the three thread/GPU frame times,
        /// Render = the five render statistics. Applied at the next frame.
        public static bool FrameTiming = true, Render = true;
        private bool _timingOn = true, _renderOn = true; // what the recorders were started with
        private void ApplySwitches()
        {
            for (int i = 0; i < CounterDefs.Length; i++)
            {
                bool want = i < 3 ? FrameTiming : Render;
                if (want == (i < 3 ? _timingOn : _renderOn)) continue;
                if (!want) { _counters[i].Dispose(); _counters[i] = default; }
                else try { _counters[i] = ProfilerRecorder.StartNew(CounterDefs[i].cat, CounterDefs[i].name, 1); } catch { }
            }
            _timingOn = FrameTiming; _renderOn = Render;
        }

        private float Counter(int i, double scale)
        {
            if (!_counters[i].Valid) return 0;
            _countersValid = true;
            return (float)(_counters[i].LastValue * scale);
        }

        /// Call once per frame with the duration of the frame that just finished.
        public void Tick(float frameMs)
        {
            int gc = GC.CollectionCount(0);
            bool hadGc = gc != _lastGc;
            _lastGc = gc;
            bool slow = frameMs > SlowMs;

            for (int i = 0; i < _rec.Length; i++)
            {
                LastMs[i] = 0;
                if (!_rec[i].Valid) continue;
                _everValid[i] = true;
                double ms = _rec[i].LastValue / 1e6; // ns
                LastMs[i] = (float)ms;
                _sumAll[i] += ms;
                if (slow) _sumSlow[i] += ms;
            }
            if (FrameTiming != _timingOn || Render != _renderOn) ApplySwitches();
            float present = Math.Max(LastMs[9], PlayerLoopProfiler.LastPresentWaitMs); // Gfx.WaitForPresentOnGfxThread / loop phase
            Extra[0] = Math.Max(0f, frameMs - present);
            Extra[1] = Counter(0, 1e-6); Extra[2] = Counter(1, 1e-6); Extra[3] = Counter(2, 1e-6);
            bool gpu = Extra[3] > 0 ? Extra[3] >= 0.85f * frameMs : present > 0.5f && present > 0.2f * frameMs;
            Extra[GpuBoundIndex] = gpu ? 1 : 0;
            Extra[5] = Counter(3, 1); Extra[6] = Counter(4, 1); Extra[7] = Counter(5, 1); Extra[8] = Counter(6, 1e-3); Extra[9] = Counter(7, 1);
            for (int i = 0; i < Extra.Length; i++) { _extraAll[i] += Extra[i]; if (slow) _extraSlow[i] += Extra[i]; }
            if (gpu) { _gpuBound++; if (slow) _slowGpuBound++; }
            hadGc |= GcMs > 0;
            _frames++;
            if (hadGc) { _gcFrames++; _msGcFrames += frameMs; } else _msNoGcFrames += frameMs;
            if (slow) { _slow++; if (hadGc) _slowWithGc++; }
        }

        public void Dispose()
        {
            foreach (var r in _rec) r.Dispose();
            foreach (var r in _counters) r.Dispose();
        }

        public void Reset()
        {
            _frames = _slow = _gcFrames = _slowWithGc = 0;
            _gpuBound = _slowGpuBound = 0;
            Array.Clear(_extraAll, 0, _extraAll.Length);
            Array.Clear(_extraSlow, 0, _extraSlow.Length);
            _msGcFrames = _msNoGcFrames = 0;
            Array.Clear(_sumAll, 0, _sumAll.Length);
            Array.Clear(_sumSlow, 0, _sumSlow.Length);
            _lastGc = GC.CollectionCount(0);
        }

        internal Func<string> ReportSnapshot()
        {
            var copy = (FrameBreakdown)MemberwiseClone();
            copy._sumAll = (double[])_sumAll.Clone(); copy._sumSlow = (double[])_sumSlow.Clone();
            copy._everValid = (bool[])_everValid.Clone();
            copy._extraAll = (double[])_extraAll.Clone(); copy._extraSlow = (double[])_extraSlow.Clone();
            return copy.Json;
        }
        public string Json()
        {
            var sb = new StringBuilder();
            long noGc = _frames - _gcFrames;
            sb.Append("{\"frames\":").Append(_frames)
              .Append(",\"slowFrames\":").Append(_slow)
              .Append(",\"slowThresholdMs\":").Append(TLDRevamp.Json.Num(SlowMs))
              .Append(",\"slowWithGc\":").Append(_slowWithGc)
              .Append(",\"gcFrames\":").Append(_gcFrames)
              .Append(",\"avgMsGcFrames\":").Append(_gcFrames > 0 ? (_msGcFrames / _gcFrames).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "0")
              .Append(",\"avgMsNoGcFrames\":").Append(noGc > 0 ? (_msNoGcFrames / noGc).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "0")
              .Append(",\"gpuBoundFrames\":").Append(_gpuBound)
              .Append(",\"slowGpuBound\":").Append(_slowGpuBound)
              .Append(",\"countersValid\":").Append(_countersValid ? "true" : "false")
              .Append(",\"perFrame\":[");
            for (int i = 0; i < ExtraNames.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"m\":").Append(TLDRevamp.Json.Str(ExtraNames[i]))
                  .Append(",\"avgAll\":").Append(TLDRevamp.Json.Num(_frames > 0 ? _extraAll[i] / _frames : 0))
                  .Append(",\"avgSlow\":").Append(TLDRevamp.Json.Num(_slow > 0 ? _extraSlow[i] / _slow : 0)).Append('}');
            }
            sb.Append("],\"markers\":[");
            for (int i = 0; i < Markers.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"m\":").Append(TLDRevamp.Json.Str(Markers[i].name))
                  .Append(",\"valid\":").Append(_everValid[i] ? "true" : "false")
                  .Append(",\"avgMsAll\":").Append(_frames > 0 ? (_sumAll[i] / _frames).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "0")
                  .Append(",\"avgMsSlow\":").Append(_slow > 0 ? (_sumSlow[i] / _slow).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "0")
                  .Append('}');
            }
            return sb.Append("]}").ToString();
        }
    }
}
