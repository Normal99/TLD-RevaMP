using TLDRevamp.Multiplayer;
using UnityEngine;

namespace HeadUnit
{
    /// What the head unit shows off its screen: the "click" hint by the crosshair and the big picture for passengers
    /// (BIG on the screen). Questions and typing are on the unit's own screen (UnitBehaviour's dialogs).
    public static class Panel
    {
        public static bool Big;
        private static string _flash; private static float _flashUntil;
        private static GUIStyle _small;

        /// A short message, shown on the screens of the units nearby for a few seconds.
        public static void Flash(string s) { _flash = s; _flashUntil = Time.realtimeSinceStartup + 4f; }
        public static string Flashing => Time.realtimeSinceStartup < _flashUntil ? _flash : null;

        public static void Draw()
        {
            Styles();
            if (Touch.LookedButton != null && Touch.Looked != null)
                GUI.Label(new Rect(Screen.width * 0.5f - 60f, Screen.height * 0.5f + 14f, 120f, 20f), "click", _small);

            var u = Controls.Mine;
            if (Big && u != null && u.VideoTexture != null)
            {
                float bw = Mathf.Min(Screen.width * 0.45f, 800f), bh = bw * 9f / 16f;
                GUI.DrawTexture(new Rect(Screen.width - bw - 16f, 16f, bw, bh), u.VideoTexture, ScaleMode.ScaleToFit);
            }
        }

        private static void Styles()
        {
            if (_small != null) return;
            _small = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, alignment = TextAnchor.UpperCenter, normal = { textColor = new Color(0.85f, 0.85f, 0.85f) } };
        }
    }
}
