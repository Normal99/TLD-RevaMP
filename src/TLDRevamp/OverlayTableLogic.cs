using System;

namespace TLDRevamp
{
    internal static class OverlayTableLogic
    {
        // Numeric widths are maxima across the header and all visible rows. The name
        // column receives the remaining space; each numeric cell shares a right edge.
        internal static float Columns(float width, float gap, float[] measured, float[] left, float[] right)
        {
            float total = 0;
            for (int i = 0; i < measured.Length; i++) total += measured[i] + gap;
            float name = Math.Max(0, width - total), x = name;
            float fit = total > width && total > 0 ? width / total : 1;
            for (int i = 0; i < measured.Length; i++)
            {
                left[i] = x + gap * fit; right[i] = left[i] + measured[i] * fit; x = right[i];
            }
            return name;
        }
    }
}
