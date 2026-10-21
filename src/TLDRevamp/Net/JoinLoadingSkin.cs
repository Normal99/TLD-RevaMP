using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// IMGUI counterpart of the game's paper windows and menu buttons; independent of debug-overlay styling.
    internal static class JoinLoadingSkin
    {
        internal static readonly Color Paper = new Color(210 / 255f, 205 / 255f, 200 / 255f), Ink = new Color(.2f, .2f, .2f),
            Orange = new Color(1, 100 / 255f, 0), Green = new Color(97 / 255f, 243 / 255f, 0),
            Muted = new Color(.40f, .39f, .35f), Done = new Color(.22f, .38f, .10f), ButtonTint = new Color(231 / 255f, 220 / 255f, 173 / 255f);
        internal static GUIStyle Small, Body, Title, Large, Wrap, Numeric;
        private static GUIStyle _button, _hit, _loading;
        private static readonly GUIContent Scratch = new GUIContent(), Loading = new GUIContent("Loading");
        private static Sprite _paper, _menuButton, _poster;
        private static Font _font;
        private static int _height = -1;
        internal static float Scale, Gap, Padding, Line, Header;
        internal static float ButtonHeight => 60 * Scale;

        internal static void Ensure()
        {
            if (Body == null)
            {
                foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (sprite.name == "newpaper") _paper = sprite;
                    else if (sprite.name == "newmenubutton2") _menuButton = sprite;
                    else if (sprite.name == "loading text") _poster = sprite;
                }
                foreach (var font in Resources.FindObjectsOfTypeAll<Font>())
                    if (font.name.IndexOf("LiberationSans", StringComparison.OrdinalIgnoreCase) >= 0) { _font = font; break; }
                if (_font == null)
                {
                    try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
                    if (_font == null) _font = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Liberation Sans" }, 16);
                }
                Small = Make(); Body = Make(); Title = Make(); Large = Make(); Wrap = Make(); Numeric = Make();
                Wrap.wordWrap = true; Numeric.alignment = TextAnchor.UpperRight;
                Title.fontStyle = Large.fontStyle = FontStyle.Bold;
                _button = Make(); _button.fontStyle = FontStyle.Bold; _button.alignment = TextAnchor.MiddleCenter;
                _loading = Make(); _loading.fontStyle = FontStyle.Bold;
                _hit = new GUIStyle { padding = new RectOffset(0, 0, 0, 0) };
                _hit.normal.textColor = _hit.hover.textColor = _hit.active.textColor = new Color(0, 0, 0, 0);
            }
            if (_height == Screen.height) return;
            _height = Screen.height;
            Scale = Math.Max(.65f, Math.Min(1.8f, Screen.height / 1080f)); Gap = 5 * Scale; Padding = 22 * Scale; Header = Padding;
            Small.fontSize = Math.Max(11, (int)(14 * Scale));
            Body.fontSize = Wrap.fontSize = Numeric.fontSize = Math.Max(12, (int)(16 * Scale));
            Title.fontSize = (int)(22 * Scale); Large.fontSize = (int)(28 * Scale); _button.fontSize = (int)(25 * Scale);
            _loading.fontSize = Math.Max(24, (int)(150 * Screen.height / 1080f)); Line = Body.lineHeight;
        }
        private static GUIStyle Make()
        {
            var style = new GUIStyle { font = _font, richText = false, padding = new RectOffset(0, 0, 0, 0), clipping = TextClipping.Clip };
            style.normal.textColor = Color.white; return style;
        }
        internal static float Height(GUIContent text, float width, GUIStyle style = null) => (style ?? Body).CalcHeight(text, Math.Max(1, width));
        internal static Color Colour(OverlayKind kind) => kind == OverlayKind.Ok ? Done : kind == OverlayKind.Accent || kind == OverlayKind.Warning ? Orange : kind == OverlayKind.Muted ? Muted : Ink;
        internal static void Label(Rect r, GUIContent content, OverlayKind kind = OverlayKind.Text, GUIStyle style = null)
        {
            if (Event.current.type != EventType.Repaint || r.width <= 0 || r.height <= 0) return;
            var old = GUI.color; GUI.color = Colour(kind); GUI.Label(r, content, style ?? Body); GUI.color = old;
        }
        internal static void Fill(Rect r, Color color)
        {
            var old = GUI.color; GUI.color = color; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = old;
        }
        internal static void StatusDot(Rect r, OverlayKind kind) => Fill(r, Colour(kind));
        internal static void Progress(Rect r, float value, OverlayKind kind)
        {
            Fill(r, Muted); r.width *= Mathf.Clamp01(value); Fill(r, Colour(kind));
        }
        internal static Rect Panel(Rect r)
        {
            if (Event.current.type == EventType.Repaint) Sprite(r, _paper, Paper);
            return new Rect(r.x + Padding, r.y + Padding, Math.Max(1, r.width - 2 * Padding), Math.Max(1, r.height - 2 * Padding));
        }
        internal static void Sprite(Rect r, Sprite sprite, Color tint)
        {
            if (sprite == null) { Fill(r, tint); return; }
            var old = GUI.color; GUI.color = tint;
            var source = sprite.textureRect; var border = sprite.border; var texture = sprite.texture;
            for (int row = 0; row < 3; row++) for (int col = 0; col < 3; col++)
            {
                JoinPresentation.Slice(col, row, new JoinRect(r.x, r.y, r.width, r.height), new JoinRect(source.x, source.y, source.width, source.height),
                    border.x, border.y, border.z, border.w, Scale, texture.width, texture.height, out var dst, out var uv);
                if (dst.W > 0 && dst.H > 0 && uv.W > 0 && uv.H > 0)
                    GUI.DrawTextureWithTexCoords(new Rect(dst.X, dst.Y, dst.W, dst.H), texture, new Rect(uv.X, uv.Y, uv.W, uv.H));
            }
            GUI.color = old;
        }
        internal static bool Click(Rect r, string text, bool primary = false)
        {
            if (Event.current.type == EventType.Repaint)
            {
                bool hover = r.Contains(Event.current.mousePosition);
                Sprite(r, _menuButton, hover ? Color.Lerp(ButtonTint, Color.white, .18f) : ButtonTint);
                Scratch.text = text;
                Outlined(r, Scratch, _button, primary ? Green : new Color(.51f, .51f, .51f));
            }
            return GUI.Button(r, text, _hit);
        }
        private static void Outlined(Rect r, GUIContent content, GUIStyle style, Color color)
        {
            var old = GUI.color; GUI.color = Ink; float d = Math.Max(1, Scale);
            var outline = r; outline.x -= d; GUI.Label(outline, content, style); outline.x += 2 * d; GUI.Label(outline, content, style);
            outline = r; outline.y -= d; GUI.Label(outline, content, style); outline.y += 2 * d; GUI.Label(outline, content, style);
            GUI.color = color; GUI.Label(r, content, style); GUI.color = old;
        }
        internal static void Fallback(float progress)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (_poster != null) Sprite(new Rect(0, 0, Screen.width, Screen.height), _poster, Color.white);
            var old = GUI.color; GUI.color = Color.white;
            GUI.Label(new Rect(24 * Scale, 20 * Scale, Screen.width * .46f, Screen.height * .26f), Loading, _loading); GUI.color = old;
            var road = JoinPresentation.Road(Screen.width, Screen.height);
            float roadScale = Screen.height / 1080f;
            var bar = new Rect(road.X, road.Y, road.W, road.H);
            Sprite(bar, _menuButton, ButtonTint);
            bar.x += 10 * roadScale; bar.y += 14 * roadScale; bar.width = Math.Max(0, bar.width - 20 * roadScale) * progress; bar.height -= 28 * roadScale;
            Fill(bar, Done);
        }
    }
}
