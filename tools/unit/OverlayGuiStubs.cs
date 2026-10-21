using System;

namespace UnityEngine
{
    public struct Color
    {
        public float r, g, b, a;
        public float R => r; public float G => g;
        public Color(float r, float g, float b, float a = 1) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new Color(1, 1, 1);
        public static Color Lerp(Color a, Color b, float t) => new Color(a.r + (b.r-a.r)*t, a.g + (b.g-a.g)*t, a.b + (b.b-a.b)*t, a.a + (b.a-a.a)*t);
    }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => default;
        public static Vector2 one => new Vector2(1, 1);
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
    }
    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
        public bool Contains(Vector2 p) => p.x >= x && p.x < x + width && p.y >= y && p.y < y + height;
    }
    public static class Screen { public static int width = 1920, height = 1080; }
    public class GUIContent { public string text; public GUIContent(string text = null) { this.text = text; } }
    public class RectOffset { public RectOffset(int a, int b, int c, int d) { } }
    public enum TextClipping { Clip }
    public enum TextAnchor { MiddleCenter, UpperRight }
    public class GUIStyleState { public Color textColor; public Texture2D background; }
    public class GUIStyle
    {
        public static int Measures; public Font font; public float lineHeight => fontSize + 6;
        public int fontSize; public FontStyle fontStyle; public bool richText, wordWrap; public RectOffset padding, border; public TextClipping clipping; public TextAnchor alignment;
        public GUIStyleState normal = new GUIStyleState(), hover = new GUIStyleState(), active = new GUIStyleState();
        public GUIStyle() { } public GUIStyle(GUIStyle other) { }
        public Vector2 CalcSize(GUIContent text) { Measures++; return new Vector2((text.text?.Length ?? 0) * fontSize * .5f, fontSize + 4); }
        public float CalcHeight(GUIContent text, float width)
        {
            Measures++; int lines = 1, column = 0, capacity = wordWrap ? Math.Max(1, (int)(width / Math.Max(1, fontSize * .5f))) : int.MaxValue;
            foreach (char c in text.text ?? "") { if (c == '\n') { lines++; column = 0; } else if (++column > capacity) { lines++; column = 1; } }
            return lines * (fontSize + 6);
        }
    }
    public enum FontStyle { Normal, Bold }
    public class Font { public string name = "Arial"; public static Font CreateDynamicFontFromOSFont(string[] names, int size) => new Font(); }
    public class GUISkin { public GUIStyle label = new GUIStyle(), button = new GUIStyle(), textArea = new GUIStyle(); }
    public class Texture2D
    {
        public TextureWrapMode wrapMode; public int Width, Height; public float MinAlpha = 1;
        public int width => Width; public int height => Height;
        public static Texture2D whiteTexture = new Texture2D();
        public Texture2D() { } public Texture2D(int w, int h) { Width = w; Height = h; }
        public void SetPixel(int x, int y, Color c) { MinAlpha = Math.Min(MinAlpha, c.a); } public void Apply(bool updateMipmaps, bool makeNoLongerReadable) { }
    }
    public enum TextureWrapMode { Clamp }
}
