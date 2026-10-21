using System;
using System.Collections.Generic;

namespace TLDRevamp.Net
{
    internal struct JoinRect
    {
        internal float X, Y, W, H;
        internal JoinRect(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
    }

    internal static class JoinPresentation
    {
        // uGUI anchors are bottom-up: the road bar at .25 occupies y=760..860 at 1080p.
        // Reserve the top-left title too. The right-hand paper ends above that full-width strip.
        internal static JoinRect Paper(float width, float height)
        {
            bool wide = width >= height * 1.4f;
            float margin = Math.Max(2, height * .015f);
            float x = wide ? width * .48f : margin;
            float y = wide ? margin : height * .30f;
            return new JoinRect(x, y, Math.Max(1, width - x - margin), Math.Max(1, height * .68f - y));
        }
        internal static JoinRect Road(float width, float height)
        {
            float scale = height / 1080f;
            float w = Math.Max(1, Math.Min(1000 * scale, width - 40 * scale));
            return new JoinRect((width - w) / 2, height * .75f - 50 * scale, w, 100 * scale);
        }
        internal static bool UseNative(bool canvas, bool root, bool image, bool title, bool slider) => canvas && root && image && title && slider;
        internal static float Forward(float previous, float progress) => Math.Max(previous, Math.Max(0, Math.Min(1, progress)));

        // Source border order is Unity's left, bottom, right, top. Row zero is the source bottom.
        internal static void Slice(int column, int row, JoinRect target, JoinRect source,
            float left, float bottom, float right, float top, float scale, float textureWidth, float textureHeight,
            out JoinRect destination, out JoinRect uv)
        {
            Axis(column, target.X, target.W, source.X, source.W, left, right, scale, out float x, out float w, out float sx, out float sw);
            Axis(row, 0, target.H, source.Y, source.H, bottom, top, scale, out float y, out float h, out float sy, out float sh);
            destination = new JoinRect(x, target.Y + target.H - y - h, w, h);
            uv = new JoinRect(sx / textureWidth, sy / textureHeight, sw / textureWidth, sh / textureHeight);
        }
        private static void Axis(int cell, float start, float length, float source, float sourceLength, float first, float last, float scale,
            out float at, out float size, out float sourceAt, out float sourceSize)
        {
            float sourceScale = first + last > sourceLength ? sourceLength / (first + last) : 1;
            first *= sourceScale; last *= sourceScale;
            float fit = first + last > 0 ? Math.Min(scale, length / (first + last)) : scale;
            float a = first * fit, b = last * fit;
            at = start + (cell == 0 ? 0 : cell == 1 ? a : length - b);
            size = Math.Max(0, cell == 0 ? a : cell == 1 ? length - a - b : b);
            sourceAt = source + (cell == 0 ? 0 : cell == 1 ? first : sourceLength - last);
            sourceSize = Math.Max(0, cell == 0 ? first : cell == 1 ? sourceLength - first - last : last);
        }
    }

    // Capture once, even when an ancestor is shared by several loading-screen children.
    internal sealed class JoinActiveStates<T> where T : class
    {
        private readonly List<T> _objects = new List<T>();
        private readonly List<bool> _active = new List<bool>();
        internal int Count => _objects.Count;
        internal T Target(int i) => _objects[i];
        internal bool Active(int i) => _active[i];
        internal void Remember(T target, bool active)
        {
            for (int i = 0; i < Count; i++) if (ReferenceEquals(_objects[i], target)) return;
            _objects.Add(target); _active.Add(active);
        }
        internal void Clear() { _objects.Clear(); _active.Clear(); }
    }
}
