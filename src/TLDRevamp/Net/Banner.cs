using UnityEngine;

namespace TLDRevamp.Net
{
    /// Caption at the top of the screen for watchable test scenarios (bridge `banner <seconds> <text>`, `banner off`):
    /// what the scenario is doing now and what to watch for. Plain GUI.Label (no layout pass needed).
    public static class Banner
    {
        private static string _text = "";
        private static float _until;
        private static GUIStyle _style;

        public static string Show(float seconds, string text)
        {
            _text = (text ?? "").Replace("\\n", "\n"); // the bridge is line-based: line breaks arrive as a literal \n
            _until = Time.realtimeSinceStartup + seconds;
            return "{\"ok\":true}";
        }

        public static void Draw()
        {
            if (_text.Length == 0 || Time.realtimeSinceStartup > _until) return;
            if (Event.current.type != EventType.Repaint) return;
            if (_style == null)
                _style = new GUIStyle(GUI.skin.label) { fontSize = 26, alignment = TextAnchor.MiddleCenter, wordWrap = true, richText = true };
            var r = new Rect(Screen.width * 0.1f, 20f, Screen.width * 0.8f, 110f);
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(r, _text, _style);
        }
    }
}
