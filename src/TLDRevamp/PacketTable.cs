using System;
using UnityEngine;

namespace TLDRevamp
{
    internal sealed class PacketTable
    {
        private readonly GUIContent _summary = new GUIContent();
        private GUIContent[,] _cells;
        private readonly float[] _measured = new float[5], _left = new float[5], _right = new float[5];
        private float _nameWidth, _summaryHeight, _row;
        internal float Height;
        internal void Rebuild(float width, bool session)
        {
            width = Math.Max(0, width - 2 * OverlayStyle.Padding);
            // The bridge keeps its existing text format. Only the in-game table uses cells.
            var rows = session ? Net.NetMeter.PanelRows(12) : new[] { new[] { "Not in a multiplayer game" } };
            _summary.text = OverlayStyle.Truncate(rows[0][0], width, OverlayStyle.Small);
            _summaryHeight = OverlayStyle.Height(_summary, width, OverlayStyle.Small);
            _row = OverlayStyle.Small.lineHeight;
            _cells = new GUIContent[rows.Length - 1, 6]; Array.Clear(_measured, 0, _measured.Length);
            for (int r = 1; r < rows.Length; r++)
                for (int c = 0; c < 6; c++)
                {
                    var cell = new GUIContent(c < rows[r].Length ? rows[r][c] : ""); _cells[r - 1, c] = cell;
                    if (c > 0) _measured[c - 1] = Math.Max(_measured[c - 1], OverlayStyle.Small.CalcSize(cell).x);
                }
            _nameWidth = OverlayTableLogic.Columns(width, 10 * OverlayStyle.Scale, _measured, _left, _right);
            for (int r = 0; r < _cells.GetLength(0); r++) _cells[r, 0].text = OverlayStyle.Truncate(_cells[r, 0].text, _nameWidth, OverlayStyle.Small);
            Height = OverlayStyle.PanelHeight(_summaryHeight + _cells.GetLength(0) * _row);
        }
        internal void Draw(DebugBox box)
        {
            if (box.H <= 0) return;
            var r = OverlayStyle.Panel(box, OverlayTool.Packets);
            OverlayStyle.Label(new Rect(r.x, r.y, r.width, Math.Min(r.height, _summaryHeight)), _summary, OverlayKind.Text, OverlayStyle.Small);
            for (int row = 0; row < _cells.GetLength(0); row++)
            {
                float y = r.y + _summaryHeight + row * _row, h = Math.Min(_row, r.y + r.height - y);
                OverlayStyle.Label(new Rect(r.x, y, _nameWidth, h), _cells[row, 0], OverlayKind.Text, OverlayStyle.Small);
                for (int c = 0; c < 5; c++) OverlayStyle.Label(new Rect(r.x + _left[c], y, _right[c] - _left[c], h), _cells[row, c + 1], OverlayKind.Text, OverlayStyle.Numeric);
            }
        }
    }
}
