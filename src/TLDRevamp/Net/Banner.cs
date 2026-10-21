using UnityEngine;

namespace TLDRevamp.Net
{
    /// Caption at the top of the screen for watchable test scenarios (bridge `banner <seconds> <text>`, `banner off`):
    /// what the scenario is doing now and what to watch for. Plain GUI.Label (no layout pass needed).
    public static class Banner
    {
        private static string _text = "";
        private static float _until;
        private static readonly GUIContent Content = new GUIContent();
        private static int _revision = -1;
        private static float _width;
        public static bool Active => _text.Length > 0 && Time.realtimeSinceStartup <= _until;

        public static string Show(float seconds, string text)
        {
            _text = (text ?? "").Replace("\\n", "\n"); // the bridge is line-based: line breaks arrive as a literal \n
            _until = Time.realtimeSinceStartup + seconds;
            _revision = -1;
            return "{\"ok\":true}";
        }

        internal static float Measure()
        {
            if (!Active) return 0;
            float width = Mathf.Max(0, Screen.width - 24 * OverlayStyle.Scale - 2 * OverlayStyle.Padding);
            if (_revision != OverlayStyle.Revision || _width != width)
            {
                _revision = OverlayStyle.Revision; _width = width;
                Content.text = OverlayStyle.Truncate(_text, width, OverlayStyle.Title);
                _height = OverlayStyle.PanelHeight(OverlayStyle.Height(Content, width, OverlayStyle.Title));
            }
            return _height;
        }
        private static float _height;

        public static void Draw()
        {
            if (!Active) return;
            if (Event.current.type != EventType.Repaint) return;
            if (DebugLayout.Banner.H <= 0) return;
            var r = OverlayStyle.Panel(DebugLayout.Banner, OverlayTool.Banner);
            OverlayStyle.Label(r, Content, OverlayKind.Text, OverlayStyle.Title);
        }
    }
}
