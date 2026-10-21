using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Cached IMGUI assets, styled after the menu. No game UI objects are touched.
    internal static class JoinLoadingSkin
    {
        internal static Color Hex(int rgb, float alpha = 1) => new Color((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f, alpha);
        internal static readonly Color Paper = Hex(0xD2CDC8), Ink = Hex(0x2F2B28), Orange = Hex(0xFF6400),
            Current = Hex(0xC24E00), Muted = Hex(0x8A837C), Detail = Hex(0x5E5852), Done = Hex(0x3B7A1A),
            Border = Hex(0x8E867C), Outline = Hex(0x2A1A0E), Green = Hex(0x61F300);
        internal static GUIStyle Title, Host, Clock, Step, StepBold, Small, Line, Body, Heading, Counter, Numeric, Percent, Credit, Latest, Mark;
        private static GUIStyle _button, _hit;
        private static Sprite _paper, _menuButton;
        private static Font _font, _bold;
        private static int _height = -1, _pressed;
        private static Texture2D _backdrop, _vignette;
        private static readonly Vector2[] OutlineOffsets = new Vector2[4];
        internal static float Scale;
        internal static void Ensure()
        {
            if (Body == null)
            {
                foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (sprite.name == "newpaper") _paper = sprite;
                    else if (sprite.name == "newmenubutton2") _menuButton = sprite;
                }
                foreach (var font in Resources.FindObjectsOfTypeAll<Font>())
                {
                    if (string.Equals(font.name, "LiberationSans", StringComparison.OrdinalIgnoreCase)) _font = font;
                    else if (string.Equals(font.name, "LiberationSans-Bold", StringComparison.OrdinalIgnoreCase)) _bold = font;
                }
                if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                Title = Make(true); Host = Make(); Clock = Make(true); Step = Make(); StepBold = Make(true); Small = Make();
                Line = Make(); Body = Make(); Heading = Make(true); Counter = Make(true); Numeric = Make(); Percent = Make(true);
                Credit = Make(); Latest = Make(); Mark = Make(true); _button = Make(true);
                Body.wordWrap = Small.wordWrap = Step.wordWrap = StepBold.wordWrap = true;
                Numeric.alignment = Percent.alignment = TextAnchor.UpperRight;
                Clock.alignment = _button.alignment = TextAnchor.MiddleCenter;
                _hit = new GUIStyle { padding = new RectOffset(0, 0, 0, 0) };
                _hit.normal.textColor = _hit.hover.textColor = _hit.active.textColor = new Color(0, 0, 0, 0);
            }
            if (_height == Screen.height) return;
            _height = Screen.height; OverlayStyle.Ensure(); Scale = OverlayStyle.Scale;
            Size(Title, 52); Size(Host, 22); Size(Clock, 36); Size(Step, 22); Size(StepBold, 22); Size(Small, 17); Size(Line, 17);
            Size(Body, 20); Size(Heading, 24); Size(Counter, 40); Size(Numeric, 18); Size(Percent, 18);
            Size(Credit, 15); Size(Latest, 16); Size(Mark, 22); Size(_button, 24);
            const float d = 1;
            OutlineOffsets[0] = new Vector2(-d, 0); OutlineOffsets[1] = new Vector2(d, 0);
            OutlineOffsets[2] = new Vector2(0, -d); OutlineOffsets[3] = new Vector2(0, d);
        }
        private static void Size(GUIStyle style, int size) => style.fontSize = Math.Max(1, (int)Math.Round(size * Scale));
        private static GUIStyle Make(bool bold = false)
        {
            var style = new GUIStyle { font = bold && _bold != null ? _bold : _font, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                richText = false, padding = new RectOffset(0, 0, 0, 0), clipping = TextClipping.Clip };
            style.normal.textColor = Color.white; return style;
        }
        internal static Rect Rect(float x, float y, float w, float h) => new Rect(JoinPresentation.Snap(x), JoinPresentation.Snap(y),
            Math.Max(0, JoinPresentation.Snap(x + w) - JoinPresentation.Snap(x)), Math.Max(0, JoinPresentation.Snap(y + h) - JoinPresentation.Snap(y)));
        internal static Rect Rect(JoinRect r) => Rect(r.X, r.Y, r.W, r.H);
        internal static float Height(GUIContent text, float width, GUIStyle style) => JoinPresentation.Snap(style.CalcHeight(text, Math.Max(1, width)));
        internal static void Label(Rect r, GUIContent content, Color colour, GUIStyle style)
        {
            if (Event.current.type != EventType.Repaint || r.width <= 0 || r.height <= 0) return;
            var old = GUI.color; GUI.color = colour; GUI.Label(r, content, style); GUI.color = old;
        }
        internal static void Fill(Rect r, Color color)
        {
            var old = GUI.color; GUI.color = color; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = old;
        }
        internal static void Bordered(Rect r, Color fill, Color border, float thickness)
        {
            Fill(r, border); Fill(Rect(r.x + thickness, r.y + thickness, r.width - 2 * thickness, r.height - 2 * thickness), fill);
        }
        internal static void Background()
        {
            if (_backdrop == null)
            {
                _backdrop = new Texture2D(1, 256) { wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < 256; y++) _backdrop.SetPixel(0, y, Color.Lerp(Hex(0x120D0A), Hex(0x2B2119), y / 255f));
                _backdrop.Apply(false, true);
                _vignette = new Texture2D(128, 128) { wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
                {
                    float dx = (x - 63.5f) / 63.5f, dy = (y - 63.5f) / 63.5f;
                    float radius = Math.Min(1, (dx * dx + dy * dy) / 2);
                    _vignette.SetPixel(x, y, new Color(0, 0, 0, .45f * radius * radius));
                }
                _vignette.Apply(false, true);
            }
            var screen = new Rect(0, 0, Screen.width, Screen.height);
            GUI.DrawTexture(screen, _backdrop); GUI.DrawTexture(screen, _vignette);
        }
        internal static void Panel(Rect r)
        {
            if (_paper == null) Bordered(r, Paper, Border, 2 * Scale); else Sprite(r, _paper, Paper);
        }
        internal static void Progress(Rect r, float value)
        {
            Bordered(r, Hex(0x3A332D), Hex(0x1E1915), Scale);
            var fill = Rect(r.x + Scale, r.y + Scale, Math.Max(0, r.width - 2 * Scale) * value, r.height - 2 * Scale);
            Fill(fill, Orange); Fill(Rect(fill.x, fill.y, fill.width, Scale), Hex(0xFF8A3A));
        }
        private static void Sprite(Rect r, Sprite sprite, Color tint)
        {
            var old = GUI.color; GUI.color = tint;
            var source = sprite.textureRect; var border = sprite.border; var texture = sprite.texture;
            for (int row = 0; row < 3; row++) for (int col = 0; col < 3; col++)
            {
                JoinPresentation.Slice(col, row, new JoinRect(r.x, r.y, r.width, r.height), new JoinRect(source.x, source.y, source.width, source.height),
                    border.x, border.y, border.z, border.w, Scale, texture.width, texture.height, out var dst, out var uv);
                if (dst.W > 0 && dst.H > 0 && uv.W > 0 && uv.H > 0)
                    GUI.DrawTextureWithTexCoords(Rect(dst), texture, new Rect(uv.X, uv.Y, uv.W, uv.H));
            }
            GUI.color = old;
        }
        internal static bool Click(Rect r, GUIContent content, int id, bool primary = false)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            if (Event.current.type == EventType.MouseDown && hover) _pressed = id;
            if (Event.current.type == EventType.Repaint)
            {
                var tint = Hex(_pressed == id && hover ? 0xD6CB9C : hover ? 0xF2E9C4 : 0xE7DCAD);
                if (_menuButton == null) Bordered(r, tint, Hex(0x6E6650), 2 * Scale); else Sprite(r, _menuButton, tint);
                Outlined(Rect(r.x + 1, r.y + 1, r.width - 2, r.height - 2), content, _button, primary ? Green : Hex(0x828282));
            }
            bool clicked = GUI.Button(r, content, _hit);
            if (Event.current.rawType == EventType.MouseUp) _pressed = 0;
            return clicked;
        }
        internal static void Outlined(Rect r, GUIContent content, GUIStyle style, Color colour)
        {
            // Four one-pixel offsets for title and button labels.
            for (int i = 0; i < OutlineOffsets.Length; i++)
                Label(Rect(r.x + OutlineOffsets[i].x, r.y + OutlineOffsets[i].y, r.width, r.height), content, Outline, style);
            Label(r, content, colour, style);
        }
    }
}
