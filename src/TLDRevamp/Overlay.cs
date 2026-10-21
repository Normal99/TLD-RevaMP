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
        public bool ShowWip = true;

        private const int GW = 240, GH = 70;
        private readonly Telemetry _t;
        private readonly float[] _hist = new float[GW];
        private readonly Color32[] _pixels = new Color32[GW * GH];
        private Texture2D _graph;
        private string _toast, _text = "";
        private float _toastUntil, _nextGraph, _nextText;
        private readonly CompactOverlay _toastBar = new CompactOverlay(), _noticeBar = new CompactOverlay();
        private readonly PacketTable _packetTable = new PacketTable();
        private int _styleRevision = -1;
        private float _textHeight;
        private float _panelWidth; private int _screenWidth;
        private readonly GUIContent _content = new GUIContent();

        private static readonly Color32 Clear = new Color32(0, 0, 0, 0);

        public Overlay(Telemetry t) => _t = t;

        public void Toast(string msg, float seconds = 4f)
        {
            _toast = msg;
            _toastUntil = Time.realtimeSinceStartup + seconds; _nextToast = 0;
        }

        public void Draw()
        {
            if (Event.current.type != EventType.Repaint) return;
            OverlayStyle.Ensure();
            if (_graph == null) _graph = new Texture2D(GW, GH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            float width = DebugLayout.LeftWidth(Screen.width, Screen.height, MarkPanel.Opened);
            if (_styleRevision != OverlayStyle.Revision || _panelWidth != width || _screenWidth != Screen.width)
            {
                _styleRevision = OverlayStyle.Revision; _panelWidth = width; _screenWidth = Screen.width; _nextText = _nextPackets = _nextToast = 0;
            }
            float now = Time.realtimeSinceStartup, scale = OverlayStyle.Scale;
            if (Visible && now >= _nextText)
            {
                _nextText = now + .25f;
                float p50 = _t.Percentile(.5f);
                _text = $"{Plugin.Version} ({Plugin.CommitLabel})  {(p50 > 0 ? 1000f / p50 : 0):F0} fps\n" +
                    $"p50  {p50:F1} ms     p99  {_t.Percentile(.99f):F1} ms\n" +
                    $"Hitches  {_t.Hitches}     GC  {_t.GcCollections}     Heap  {_t.ManagedMB} MB\n" +
                    $"{Plugin.KeyName(Plugin.KeyOverlay)} hide · {Plugin.KeyName(Plugin.KeyReport)} report";
                string net = null; try { net = Net.Mp.NetStatsLine(); } catch { }
                if (net != null) _text += "\n" + net.Replace("<b>", "").Replace("</b>", "").Replace("  down", "\nDown");
                _content.text = OverlayStyle.Truncate(_text, Mathf.Max(0, width - 2 * OverlayStyle.Padding));
                _textHeight = OverlayStyle.Height(_content, width - 2 * OverlayStyle.Padding);
            }
            if (Net.NetMeter.Show && now >= _nextPackets)
            {
                _nextPackets = now + .5f;
                _packetTable.Rebuild(width, Net.Mp.Client != null || Net.Mp.Server != null);
            }
            bool toast = _toast != null && now < _toastUntil;
            if (toast && now >= _nextToast)
            {
                _toastBar.Measure("RevaMP · Notice", _toast.Replace("\n", " · ")); _nextToast = now + .25f;
            }
            _noticeBar.Measure(Net.ItemTraffic.Show ? "RevaMP · Item traffic" : "RevaMP · Debug view: Everything", "Not in a multiplayer game");
            NetworkConditions.Measure(); ReproRecorder.Measure(); WorldOverlays.MeasureMore();
            ObjectPanels.ReserveLayout(width);
            bool notice = DebugView.Active && !Net.Entities.InSession && (WorldOverlays.Ownership || WorldOverlays.Ghost || WorldOverlays.Links || Net.ItemTraffic.Show);
            DebugLayout.Arrange(Screen.width, Screen.height, new DebugLayout.Request {
                Performance = Visible ? OverlayStyle.PanelHeight(36 * scale + _textHeight) : 0,
                Packets = Net.NetMeter.Show ? _packetTable.Height : 0,
                Card = ObjectPanels.CardHeight, Compare = ObjectPanels.CompareHeight,
                Toast = toast ? _toastBar.Height : 0, ToastWidth = _toastBar.Width,
                Network = NetworkConditions.BarHeight, NetworkWidth = NetworkConditions.BarWidth,
                Recording = ReproRecorder.BarHeight, RecordingWidth = ReproRecorder.BarWidth,
                More = WorldOverlays.MoreHeight, MoreWidth = WorldOverlays.MoreWidth,
                Notice = notice ? _noticeBar.Height : 0, NoticeWidth = _noticeBar.Width,
                Mark = MarkPanel.Measure(), Banner = Net.Banner.Measure() });
            _toastBar.Draw(DebugLayout.Toast); _noticeBar.Draw(DebugLayout.Notice, OverlayKind.Muted);
            WorldOverlays.Draw(); ItemTrafficLabels.Draw();
            _packetTable.Draw(DebugLayout.Packets);
            if (ShowWip) WipMark.Draw();
            if (DebugLayout.Performance.H <= 0) return;
            if (now >= _nextGraph) { _nextGraph = now + .1f; RedrawGraph(); }
            var content = OverlayStyle.Panel(DebugLayout.Performance, OverlayTool.Performance);
            var old = GUI.color; GUI.color = Color.white;
            GUI.DrawTexture(new Rect(content.x, content.y, content.width, Mathf.Min(32 * scale, content.height)), _graph); GUI.color = old;
            OverlayStyle.Label(new Rect(content.x, content.y + 36 * scale, content.width, Mathf.Max(0, content.height - 36 * scale)), _content);
        }

        private float _nextPackets, _nextToast;
        /// Frametime graph, adaptive range up to 50 ms, line at 16.7 ms; texture row 0 is the bottom.
        private void RedrawGraph()
        {
            int n = _t.CopyHistory(_hist);
            int start = GW - n; // newest at the right, as before
            float range = 16.7f;
            for (int i = 0; i < n; i++) range = Mathf.Max(range, Mathf.Min(50, _hist[i]));
            int lineY = Mathf.Min(GH - 1, Mathf.RoundToInt(16.7f / range * GH));
            for (int col = 0; col < GW; col++)
            {
                int bh = 0;
                Color32 c = OverlayStyle.Ok;
                if (col >= start)
                {
                    float ms = _hist[col - start];
                    c = ms > 50 ? OverlayStyle.Problem : ms > 16.7f ? OverlayStyle.Warning : OverlayStyle.Ok;
                    bh = Mathf.RoundToInt(Mathf.Min(ms / range, 1f) * GH);
                }
                for (int row = 0; row < GH; row++)
                    _pixels[row * GW + col] = row < bh ? c : row == lineY ? (Color32)OverlayStyle.Muted : Clear;
            }
            _graph.SetPixels32(_pixels);
            _graph.Apply(false);
        }
    }
}
