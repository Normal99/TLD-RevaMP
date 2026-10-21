using System;
using System.IO;
using UnityEngine;

namespace TLDRevamp
{
    /// The "TLD RevaMP <version> · WIP" mark in the lower right corner, so a screenshot or a video of a test build is
    /// never mistaken for a finished release and always says which build it came from. Styled after Unity's
    /// "Development Build" watermark: bold white Fira Sans with a soft dark drop shadow, no box.
    ///
    /// The letters come from a prebaked atlas (tools/wipfont.py, embedded in the DLL). The whole mark is composed once
    /// into one texture at the atlas's 64 px size, shadow included, and drawn scaled down with mipmaps, so a frame
    /// costs one GUI.DrawTexture.
    internal static class WipMark
    {
        private const float SizeAt1080 = 22f, MarginAt1080 = 10f;
        private const int ShadowOffset = 5, ShadowBlur = 3;   // in atlas pixels (64 px em): ~1.7 px and ~1 px at 1080p
        private static readonly Color32 ShadowColor = new Color32(20, 12, 6, 230);
        private static Texture2D _texture;
        private static int _em, _baseline, _pad;
        private static bool _failed;

        internal static string Text => "TLD RevaMP " + Plugin.Version + " · WIP";

        internal static void Draw()
        {
            if (_texture == null && !_failed) Build();
            if (_texture == null)
            {
                GUI.Label(new Rect(Screen.width - 316f, Screen.height - 34f, 300f, 24f), Text);   // the atlas failed to load
                return;
            }
            float size = Mathf.Max(14f, SizeAt1080 * Screen.height / 1080f), scale = size / _em, margin = MarginAt1080 * Screen.height / 1080f;
            float w = _texture.width * scale, h = _texture.height * scale;
            // the text's right edge `margin` in from the screen's, its baseline a little more than that above the bottom
            float baseline = Screen.height - margin - .15f * size;
            var rect = new Rect(Mathf.Round(Screen.width - margin - w + _pad * scale), Mathf.Round(baseline - (h - _baseline * scale)), w, h);
            Color old = GUI.color; GUI.color = Color.white;
            GUI.DrawTexture(rect, _texture);
            GUI.color = old;
        }

        private static void Build()
        {
            try
            {
                var asm = typeof(WipMark).Assembly;
                var atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                using (var png = asm.GetManifestResourceStream("TLDRevamp.WipFont.png"))
                using (var copy = new MemoryStream())
                {
                    png.CopyTo(copy);
                    if (!ImageConversion.LoadImage(atlas, copy.ToArray(), false)) throw new InvalidDataException("WipFont.png");
                }
                string[] lines;
                using (var txt = asm.GetManifestResourceStream("TLDRevamp.WipFont.txt"))
                using (var reader = new StreamReader(txt))
                    lines = reader.ReadToEnd().Split('\n');
                var glyphs = new System.Collections.Generic.Dictionary<char, float[]>();
                int ascent = 0, descent = 0;
                foreach (var line in lines)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var f = line.Trim().Split(' ');
                    if (f.Length == 3) { _em = int.Parse(f[0]); ascent = int.Parse(f[1]); descent = int.Parse(f[2]); continue; }
                    var v = new float[7];
                    for (int i = 0; i < 7; i++) v[i] = float.Parse(f[i + 1], System.Globalization.CultureInfo.InvariantCulture);
                    glyphs[(char)int.Parse(f[0])] = v;
                }
                Compose(atlas, glyphs, ascent, descent, Text);
                UnityEngine.Object.Destroy(atlas);
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Log.LogWarning("WIP mark: the font atlas did not load, falling back to plain text: " + e.Message);
            }
        }

        /// Lays the text out on the baseline, blits each glyph's coverage, then puts the white letters over a blurred,
        /// offset copy of themselves (straight alpha, as IMGUI draws it).
        private static void Compose(Texture2D atlas, System.Collections.Generic.Dictionary<char, float[]> glyphs, int ascent, int descent, string text)
        {
            int aw = atlas.width, ah = atlas.height;
            var src = atlas.GetPixels32();
            int pad = _pad = ShadowOffset + 2 * ShadowBlur + 2;
            float pen = 0;
            foreach (char c in text) pen += glyphs.TryGetValue(c, out var g) ? g[6] : _em * .3f;
            int w = Mathf.CeilToInt(pen) + 2 * pad, h = ascent + descent + 2 * pad;
            _baseline = pad + descent;   // from the bottom of the texture
            var cover = new float[w * h];
            pen = pad;
            foreach (char c in text)
            {
                if (!glyphs.TryGetValue(c, out var g)) { pen += _em * .3f; continue; }
                int gx = (int)g[0], gy = (int)g[1], gw = (int)g[2], gh = (int)g[3];
                int ox = Mathf.RoundToInt(pen + g[4]);
                int top = _baseline + (int)g[5];   // texture rows count up from the bottom
                for (int y = 0; y < gh; y++)
                {
                    int srcRow = ah - 1 - (gy + y), dstRow = top - 1 - y;   // LoadImage puts the image's top row last
                    if (srcRow < 0 || dstRow < 0 || dstRow >= h) continue;
                    for (int x = 0; x < gw; x++)
                    {
                        int dx = ox + x;
                        if (dx < 0 || dx >= w) continue;
                        float a = src[srcRow * aw + gx + x].a / 255f;
                        int i = dstRow * w + dx;
                        cover[i] = Mathf.Max(cover[i], a);
                    }
                }
                pen += g[6];
            }
            var shadow = Blur(cover, w, h, ShadowBlur);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sx = x - ShadowOffset, sy = y + ShadowOffset;   // down and to the right
                    float s = sx >= 0 && sy < h ? shadow[sy * w + sx] * ShadowColor.a / 255f : 0, t = cover[y * w + x];
                    float a = t + s * (1 - t);
                    if (a <= 0) { px[y * w + x] = new Color32(255, 255, 255, 0); continue; }
                    float k = t / a;   // straight alpha: the colour is the white/shadow mix weighted by coverage
                    px[y * w + x] = new Color32((byte)(ShadowColor.r + (255 - ShadowColor.r) * k), (byte)(ShadowColor.g + (255 - ShadowColor.g) * k),
                        (byte)(ShadowColor.b + (255 - ShadowColor.b) * k), (byte)(a * 255));
                }
            _texture = new Texture2D(w, h, TextureFormat.RGBA32, true) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, anisoLevel = 0 };
            _texture.SetPixels32(px);
            _texture.Apply(true, true);
        }

        /// Two passes of a separable box blur (close to a Gaussian).
        private static float[] Blur(float[] src, int w, int h, int r)
        {
            var a = (float[])src.Clone(); var b = new float[a.Length];
            for (int pass = 0; pass < 2; pass++)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float sum = 0; for (int k = -r; k <= r; k++) { int xx = x + k; if (xx >= 0 && xx < w) sum += a[y * w + xx]; }
                        b[y * w + x] = sum / (2 * r + 1);
                    }
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float sum = 0; for (int k = -r; k <= r; k++) { int yy = y + k; if (yy >= 0 && yy < h) sum += b[yy * w + x]; }
                        a[y * w + x] = sum / (2 * r + 1);
                    }
            }
            return a;
        }
    }
}
