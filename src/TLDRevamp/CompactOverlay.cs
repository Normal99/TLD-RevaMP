using System;
using UnityEngine;

namespace TLDRevamp
{
    // Text and font measurements are cached; drawing a warmed bar allocates nothing.
    internal sealed class CompactOverlay
    {
        private readonly GUIContent _title = new GUIContent(), _status = new GUIContent(), _detail = new GUIContent();
        private string _rawTitle, _rawStatus, _rawDetail;
        private int _revision = -1;
        private float _limit, _titleWidth, _row;
        internal float Width, Height;
        internal void Measure(string title, string status = null, string detail = null)
        {
            float limit = Math.Max(0, Screen.width - 24 * OverlayStyle.Scale);
            if (_revision == OverlayStyle.Revision && _limit == limit && _rawTitle == title && _rawStatus == status && _rawDetail == detail) return;
            _revision = OverlayStyle.Revision; _limit = limit; _rawTitle = title; _rawStatus = status; _rawDetail = detail;
            float p = OverlayStyle.Padding, gap = OverlayStyle.Gap, available = Math.Max(0, limit - 2 * p);
            _title.text = OverlayStyle.Truncate(title, available, OverlayStyle.Small);
            _titleWidth = OverlayStyle.Small.CalcSize(_title).x;
            _status.text = OverlayStyle.Truncate(status, Math.Max(0, available - _titleWidth - gap));
            _detail.text = OverlayStyle.Truncate(detail, available);
            float first = _titleWidth + (string.IsNullOrEmpty(status) ? 0 : gap + OverlayStyle.Body.CalcSize(_status).x);
            Width = Math.Min(limit, 2 * p + Math.Max(first, string.IsNullOrEmpty(detail) ? 0 : OverlayStyle.Body.CalcSize(_detail).x));
            _row = Math.Max(OverlayStyle.Height(_title, available, OverlayStyle.Small), string.IsNullOrEmpty(status) ? 0 : OverlayStyle.Height(_status, available));
            Height = _row + 12 * OverlayStyle.Scale + (string.IsNullOrEmpty(detail) ? 0 : OverlayStyle.Height(_detail, available));
        }
        internal void Draw(DebugBox box, OverlayKind kind = OverlayKind.Text, bool mutedTitle = false)
        {
            if (Event.current.type != EventType.Repaint || box.H <= 0) return;
            var r = OverlayStyle.Rect(box); OverlayStyle.Back(r);
            float x = r.x + OverlayStyle.Padding, y = r.y + 6 * OverlayStyle.Scale;
            OverlayStyle.Label(new Rect(x, y, _titleWidth, _row), _title, mutedTitle ? OverlayKind.Muted : OverlayKind.Accent, OverlayStyle.Small);
            float offset = _titleWidth + OverlayStyle.Gap;
            OverlayStyle.Label(new Rect(x + offset, y, Math.Max(0, r.width - 2 * OverlayStyle.Padding - offset), _row), _status, kind);
            if (!string.IsNullOrEmpty(_rawDetail)) OverlayStyle.Label(new Rect(x, y + _row, r.width - 2 * OverlayStyle.Padding, r.height - _row - 12 * OverlayStyle.Scale), _detail, kind);
        }
    }
}
