using System;

namespace TLDRevamp.Net
{
    internal struct JoinRect
    {
        internal float X, Y, W, H;
        internal JoinRect(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
    }

    internal static class JoinPresentation
    {
        internal static float Snap(float value) => (float)Math.Round(value);
        internal static JoinRect Paper(float width, float height, float scale, float contentHeight)
        {
            float margin = Snap(32 * scale);
            float w = Snap(Math.Max(1, Math.Min(1120 * scale, width - 2 * margin)));
            float h = Snap(Math.Max(1, Math.Min(contentHeight, height - 2 * margin)));
            return new JoinRect(Snap((width - w) / 2), Snap((height - h) / 2), w, h);
        }
        internal static bool TwoColumns(float innerWidth, float scale) => innerWidth >= 900 * scale;
        // Measure only visible lines, with an eight-pixel gap after each row.
        internal static float RowHeight(float nameHeight, float detailHeight, float scale) =>
            Snap(nameHeight + detailHeight + 8 * scale);
        internal static string FooterStatus(string status, string stepName, string stepDetail) =>
            string.IsNullOrWhiteSpace(status) || string.Equals(status, stepName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, stepDetail, StringComparison.OrdinalIgnoreCase) ? "" : status;
        internal static bool StackButtons(float width, float scale, int count) => count > 1 && width < (220 * count + 12 * (count - 1)) * scale;
        internal static float ButtonsHeight(float width, float scale, int count) =>
            Snap((StackButtons(width, scale, count) ? 56 * count + 12 * (count - 1) : 56) * scale);
        internal static JoinRect Button(float width, float y, float scale, int index, int count)
        {
            if (StackButtons(width, scale, count)) { y += index * 68 * scale; index = 0; count = 1; }
            float gap = Snap(12 * scale);
            float w = Snap(Math.Max(1, Math.Min(220 * scale, (width - (count - 1) * gap) / count)));
            return new JoinRect(Snap(width - count * w - (count - 1) * gap + index * (w + gap)), Snap(y), w, Snap(56 * scale));
        }
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
        internal static string Bytes(long bytes) => bytes < 1000 ? bytes + " B" : bytes < 1000000 ? (bytes / 1000d).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " KB" : (bytes / 1000000d).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " MB";
    }

}
