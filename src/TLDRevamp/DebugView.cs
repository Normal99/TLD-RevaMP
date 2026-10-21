using System;

namespace TLDRevamp
{
    internal enum DebugViewMode { Off, Focus, Problems, Everything }

    internal static class DebugView
    {
        private static DebugViewMode _mode;
        internal static int Revision;
        internal static uint PinnedNet = 0;
        internal static bool Ownership, Ghost, GhostAll, Links;
        internal static DebugViewMode Mode
        {
            get => _mode;
            set
            {
                _mode = (DebugViewMode)Math.Max(0, Math.Min(3, (int)value)); Revision++;
                Ownership = _mode >= DebugViewMode.Problems;
                Ghost = Links = _mode != DebugViewMode.Off; GhostAll = _mode == DebugViewMode.Everything;
            }
        }
        internal static bool Active => _mode != DebugViewMode.Off;
        internal static DebugViewMode Next(DebugViewMode mode) => (DebugViewMode)(((int)mode + 1) % 4);
        internal static void Enable(DebugViewMode mode) { if (!Active) Mode = mode; }
        internal static void EnableTool(DebugViewMode mode)
        {
            if (Active) return;
            Mode = mode; Ownership = Ghost = GhostAll = Links = false;
        }
        internal static string Command(string action)
        {
            if (action != "state" && action != "")
            {
                if (!Enum.TryParse(action, true, out DebugViewMode mode) || !Enum.IsDefined(typeof(DebugViewMode), mode))
                    return "{\"error\":\"use off|focus|problems|everything|state\"}";
                Mode = mode;
            }
            return "{\"mode\":\"" + Mode.ToString().ToLowerInvariant() + "\"}";
        }
    }

    internal struct DebugBox
    {
        internal float X, Y, W, H;
        internal DebugBox(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
        internal bool Overlaps(DebugBox b) => W > 0 && H > 0 && b.W > 0 && b.H > 0 && X < b.X + b.W + 3 && X + W + 3 > b.X && Y < b.Y + b.H + 3 && Y + H + 3 > b.Y;
    }

    /// Shared screen-space placement, nearest first. Storage and all repaint operations are allocation-free.
    internal sealed class DebugLabelLayout
    {
        private readonly DebugBox[] _placed = new DebugBox[128];
        private int _count;
        internal void Clear() { _count = 0; }
        internal void Reserve(DebugBox box) { if (_count < _placed.Length && box.W > 0 && box.H > 0) _placed[_count++] = box; }
        internal bool Place(ref DebugBox box, float width, float height)
        {
            float natural = box.Y;
            for (int move = 0; move < 8; move++)
            {
                if (box.X < 4 || box.X + box.W > width - 4 || box.Y < 4 || box.Y + box.H > height - 4 || Math.Abs(box.Y - natural) > 160) return false;
                int hit = -1;
                for (int i = 0; i < _count; i++) if (box.Overlaps(_placed[i])) { hit = i; break; }
                if (hit < 0) { if (_count == _placed.Length) return false; Reserve(box); return true; }
                box.Y = _placed[hit].Y - box.H - 4;
            }
            return false;
        }
    }

    internal static class DebugLayout
    {
        internal static DebugBox Performance = default, Packets = default, Card = default, Footer = default;
        internal static readonly DebugLabelLayout Labels = new DebugLabelLayout();
        internal static float CardTop => Math.Max(12, Packets.Y + Packets.H + 8);
        internal static void Begin()
        {
            Labels.Clear(); Labels.Reserve(Performance); Labels.Reserve(Packets); Labels.Reserve(Card); Labels.Reserve(Footer);
        }
    }
}
