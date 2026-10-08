using UnityEngine;

namespace TLDRevamp
{
    /// Page Up (Plugin.KeyOverlay): frametime graph + stats. Page Down (Plugin.KeyReport): capture a debug report.
    ///
    /// Cheap by design (measured 2026-09-25: the first version cost ~0.5 ms/frame, 240 GUI.DrawTexture calls plus string
    /// formatting and two percentile sorts on every OnGUI event): the graph is one 240×70 texture rewritten 10×/s, the
    /// text is rebuilt 4×/s, only the Repaint event draws, and the Runner skips IMGUI's layout pass (useGUILayout off).
    public class Overlay
    {
        public bool Visible = true;

        private const int GW = 240, GH = 70;
        private readonly Telemetry _t;
        private readonly float[] _hist = new float[GW];
        private readonly Color32[] _pixels = new Color32[GW * GH];
        private Texture2D _px, _graph;
        private GUIStyle _style;
        private string _toast, _text = "";
        private float _toastUntil, _nextGraph, _nextText;
        private int _lines = 4; private bool _wide;

        private static readonly Color32 Clear = new Color32(0, 0, 0, 0), Line = new Color32(255, 255, 255, 102),
            Green = new Color32(0, 255, 0, 255), Yellow = new Color32(255, 235, 4, 255), Red = new Color32(255, 0, 0, 255);

        public Overlay(Telemetry t) => _t = t;

        public void Toast(string msg, float seconds = 4f)
        {
            _toast = msg;
            _toastUntil = Time.realtimeSinceStartup + seconds;
        }

        public void Draw()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (_px == null)
            {
                _px = new Texture2D(1, 1);
                _px.SetPixel(0, 0, Color.white);
                _px.Apply();
                _graph = new Texture2D(GW, GH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                _style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
            }

            if (_toast != null && Time.realtimeSinceStartup < _toastUntil)
                GUI.Label(new Rect(Screen.width / 2 - 200, 40, 400, 24), "<b>" + _toast + "</b>", _style);

            if (!Visible) return;

            float now = Time.realtimeSinceStartup;
            if (now >= _nextGraph) { _nextGraph = now + 0.1f; RedrawGraph(); }
            if (now >= _nextText)
            {
                _nextText = now + 0.25f;
                float p50 = _t.Percentile(0.5f);
                _text = $"TLD Revamp {Plugin.Version}  <b>{(p50 > 0 ? 1000f / p50 : 0):F0} fps</b>\n" +
                        $"p50 {p50:F1}ms  p99 {_t.Percentile(0.99f):F1}ms  hitches {_t.Hitches}\n" +
                        $"GC {_t.GcCollections}  heap {_t.ManagedMB}MB\n" +
                        $"[{Plugin.KeyName(Plugin.KeyOverlay)} hide  {Plugin.KeyName(Plugin.KeyReport)} debug report]";   // own line: wrapped, it was cut off
                string net = null;
                try { net = Net.Mp.NetStatsLine(); } catch { }
                _lines = 4; _wide = net != null;
                if (net != null) { _text += "\n" + net; _lines = 5; }
            }

            const float x = 10, y = 10, h = 70;
            float w = _wide ? 470 : 260, th = _lines * 17 + 9;   // the network line is wider than the graph (420 wrapped its last word)
            GUI.color = new Color(0, 0, 0, 0.6f);
            GUI.DrawTexture(new Rect(x - 4, y - 4, w + 8, h + th + 4), _px);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(x, y, 260, h), _graph);
            GUI.Label(new Rect(x, y + h + 2, w, th), _text, _style);
        }

        /// Frametime graph, 0..50 ms, line at 16.7 ms; texture row 0 is the bottom.
        private void RedrawGraph()
        {
            int n = _t.CopyHistory(_hist);
            int start = GW - n; // newest at the right, as before
            int lineY = Mathf.RoundToInt(16.7f / 50f * GH);
            for (int col = 0; col < GW; col++)
            {
                int bh = 0;
                Color32 c = Green;
                if (col >= start)
                {
                    float ms = _hist[col - start];
                    c = ms > 50 ? Red : ms > 16.7f ? Yellow : Green;
                    bh = Mathf.RoundToInt(Mathf.Min(ms / 50f, 1f) * GH);
                }
                for (int row = 0; row < GH; row++)
                    _pixels[row * GW + col] = row < bh ? c : row == lineY ? Line : Clear;
            }
            _graph.SetPixels32(_pixels);
            _graph.Apply(false);
        }
    }
}
