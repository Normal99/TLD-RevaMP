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

        private readonly ProfilerRecorder[] _rec = new ProfilerRecorder[Markers.Length];
        private readonly double[] _sumAll = new double[Markers.Length], _sumSlow = new double[Markers.Length];
        private readonly bool[] _everValid = new bool[Markers.Length];
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
            _lastGc = GC.CollectionCount(0);
        }

        /// Call once per frame with the duration of the frame that just finished.
        public void Tick(float frameMs)
        {
            int gc = GC.CollectionCount(0);
            bool hadGc = gc != _lastGc;
            _lastGc = gc;
            bool slow = frameMs > SlowMs;

            _frames++;
            if (hadGc) { _gcFrames++; _msGcFrames += frameMs; } else _msNoGcFrames += frameMs;
            if (slow) { _slow++; if (hadGc) _slowWithGc++; }

            for (int i = 0; i < _rec.Length; i++)
            {
                if (!_rec[i].Valid) continue;
                _everValid[i] = true;
                double ms = _rec[i].LastValue / 1e6; // ns
                _sumAll[i] += ms;
                if (slow) _sumSlow[i] += ms;
            }
        }

        public void Reset()
        {
            _frames = _slow = _gcFrames = _slowWithGc = 0;
            _msGcFrames = _msNoGcFrames = 0;
            Array.Clear(_sumAll, 0, _sumAll.Length);
            Array.Clear(_sumSlow, 0, _sumSlow.Length);
            _lastGc = GC.CollectionCount(0);
        }

        public string Json()
        {
            var sb = new StringBuilder();
            long noGc = _frames - _gcFrames;
            sb.Append("{\"frames\":").Append(_frames)
              .Append(",\"slowFrames\":").Append(_slow)
              .Append(",\"slowThresholdMs\":").Append(SlowMs)
              .Append(",\"slowWithGc\":").Append(_slowWithGc)
              .Append(",\"gcFrames\":").Append(_gcFrames)
              .Append(",\"avgMsGcFrames\":").Append(_gcFrames > 0 ? (_msGcFrames / _gcFrames).ToString("F2") : "0")
              .Append(",\"avgMsNoGcFrames\":").Append(noGc > 0 ? (_msNoGcFrames / noGc).ToString("F2") : "0")
              .Append(",\"markers\":[");
            for (int i = 0; i < Markers.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"m\":").Append(TLDRevamp.Json.Str(Markers[i].name))
                  .Append(",\"valid\":").Append(_everValid[i] ? "true" : "false")
                  .Append(",\"avgMsAll\":").Append(_frames > 0 ? (_sumAll[i] / _frames).ToString("F3") : "0")
                  .Append(",\"avgMsSlow\":").Append(_slow > 0 ? (_sumSlow[i] / _slow).ToString("F3") : "0")
                  .Append('}');
            }
            return sb.Append("]}").ToString();
        }
    }
}
