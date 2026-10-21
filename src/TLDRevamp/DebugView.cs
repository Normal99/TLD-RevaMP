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
        internal static DebugBox Performance, Packets, Card, Compare, Toast, Network, Recording, More, Notice, Mark, Banner, Footer;
        internal static readonly DebugLabelLayout Labels = new DebugLabelLayout();
        internal const int BaseSize = 14;
        internal static float Scale(int height) => Math.Max(.8f, height / 1080f);
        internal static int FontSize(int height, int reference) => (int)Math.Round(reference * Scale(height), MidpointRounding.AwayFromZero);
        internal static float LeftWidth(float width, int height, bool mark)
        {
            float s = Scale(height), w = Math.Max(0, width - 24 * s), left = Math.Min(w, 500 * s);
            return mark ? Math.Max(0, Math.Min(left, (width - Math.Min(w, 700 * s)) / 2 - 20 * s)) : left;
        }
        internal struct Request
        {
            internal float Performance, Packets, Card, Compare, Toast, Network, Recording, More, Notice, Mark, Banner;
            internal float ToastWidth, NetworkWidth, RecordingWidth, MoreWidth, NoticeWidth;
        }
        internal static void Arrange(float width, int height, Request r)
        {
            float s = Scale(height), gap = 8 * s, margin = 12 * s, bottom = height - margin, w = Math.Max(0, width - 2 * margin);
            More = Bottom(width, ref bottom, Math.Min(w, r.MoreWidth), r.More, gap, margin);
            Recording = Bottom(width, ref bottom, Math.Min(w, r.RecordingWidth), r.Recording, gap, margin);
            Network = Bottom(width, ref bottom, Math.Min(w, r.NetworkWidth), r.Network, gap, margin);
            Notice = Bottom(width, ref bottom, Math.Min(w, r.NoticeWidth), r.Notice, gap, margin);
            Toast = Bottom(width, ref bottom, Math.Min(w, r.ToastWidth), r.Toast, gap, margin);
            Footer = bottom < height - margin ? new DebugBox(0, bottom, width, height - bottom) : default;
            float top = margin;
            Banner = Top(ref top, margin, w, r.Banner, bottom, gap, s);
            Mark = default;
            float leftWidth = LeftWidth(width, height, r.Mark > 0);
            if (r.Mark > 0)
            {
                float mw = Math.Min(w, 700 * s), mh = Math.Min(r.Mark, Math.Max(0, bottom - top));
                Mark = new DebugBox((width - mw) / 2, top + (bottom - top - mh) / 2, mw, mh);
            }
            if (leftWidth < 260 * s) { Performance = Packets = Card = Compare = default; Begin(); return; }
            float space = Math.Max(0, bottom - top), later = (r.Card > 0 ? 60 * s : 0) + (r.Compare > 0 ? 60 * s : 0);
            Performance = Top(ref top, margin, leftWidth, Math.Min(r.Performance, Math.Max(0, space - later - (r.Packets > 0 ? 60 * s : 0))), bottom, gap, s);
            Packets = Top(ref top, margin, leftWidth, Math.Min(r.Packets, Math.Max(0, bottom - top - later)), bottom, gap, s);
            Card = Top(ref top, margin, leftWidth, Math.Min(r.Card, Math.Max(0, bottom - top - (r.Compare > 0 ? 60 * s : 0))), bottom, gap, s);
            Compare = Top(ref top, margin, leftWidth, r.Compare, bottom, gap, s);
            Begin();
        }
        private static DebugBox Top(ref float top, float x, float width, float height, float bottom, float gap, float scale)
        {
            height = Math.Min(height, bottom - top);
            if (height < 40 * scale || width <= 0) return default;
            var box = new DebugBox(x, top, width, height); top += height + gap; return box;
        }
        private static DebugBox Bottom(float width, ref float bottom, float w, float h, float gap, float margin)
        {
            if (w <= 0 || h <= 0 || bottom - h < margin) return default;
            bottom -= h; var box = new DebugBox((width - w) / 2, bottom, w, h); bottom -= gap; return box;
        }
        internal static void Begin()
        {
            Labels.Clear(); Labels.Reserve(Performance); Labels.Reserve(Packets); Labels.Reserve(Card); Labels.Reserve(Compare);
            Labels.Reserve(Footer); Labels.Reserve(Mark); Labels.Reserve(Banner);
        }
    }
}
