using System;

namespace UnityEngine
{
    public struct Color
    {
        public float r, g, b, a;
        public float R => r; public float G => g;
        public Color(float r, float g, float b, float a = 1) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new Color(1, 1, 1);
    }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => default;
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
        public int fontSize; public bool richText, wordWrap; public RectOffset padding, border; public TextClipping clipping; public TextAnchor alignment;
        public GUIStyleState normal = new GUIStyleState(), hover = new GUIStyleState(), active = new GUIStyleState();
        public GUIStyle() { } public GUIStyle(GUIStyle other) { }
        public Vector2 CalcSize(GUIContent text) { Measures++; return new Vector2((text.text?.Length ?? 0) * fontSize * .5f, fontSize + 4); }
        public float CalcHeight(GUIContent text, float width) => (text.text == null ? 1 : text.text.Split('\n').Length) * (fontSize + 6);
    }
    public class Font { public static Font CreateDynamicFontFromOSFont(string[] names, int size) => new Font(); }
    public class GUISkin { public GUIStyle label = new GUIStyle(), button = new GUIStyle(), textArea = new GUIStyle(); }
    public class Texture2D
    {
        public static Texture2D whiteTexture = new Texture2D();
        public Texture2D() { } public Texture2D(int w, int h) { }
        public void SetPixel(int x, int y, Color c) { } public void Apply(bool updateMipmaps, bool makeNoLongerReadable) { }
    }
}
