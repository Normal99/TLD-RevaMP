using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace TLDRevamp
{
    /// Insert / Settings → Revamp → Debugging: this machine's traffic over the live object, including copies and parts.
    internal static class ItemTrafficLabels
    {
        private const int Limit = 40;
        private static readonly List<Net.ItemTraffic.Row> Rows = new List<Net.ItemTraffic.Row>(8192);
        private static readonly Label[] Cache = new Label[128], Drawn = new Label[Limit];
        private static readonly GUIContent Note = new GUIContent("Item traffic: not in a multiplayer game");
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private static GUIStyle _rate, _name;
        private static Texture2D _down, _up;
        private static int _count, _frame = -1;
        private sealed class Label
        {
            public uint Net; public bool Mod;
            public GameObject Object;
            public Transform Transform;
            public Renderer Renderer;
            public Collider Collider;
            public float Offset, Distance, X, Y, Width, Height, PointY;
            public int Seen = -1, Metres = -1, UpUnit = -1, DownUnit = -1, UpValue = -1, DownValue = -1;
            public readonly GUIContent Up = new GUIContent(), Down = new GUIContent(), Name = new GUIContent();
            public string ObjectName, Text;
            public bool HasUp, HasDown, Placed;
            public float NextText;
        }
        /// Only Repaint draws; bounded caches keep projections, sorting and unchanged text allocation-free.
        internal static void Draw()
        {
            if (!DebugView.Active || !Net.ItemTraffic.Show || Event.current.type != EventType.Repaint) return;
            for (int i = 0; i < _count; i++) Drawn[i].Placed = false;
            _count = 0; _frame = Time.frameCount;
            Init();
            Color color = GUI.color;
            int depth = GUI.depth;
            GUI.depth = 10;
            GUI.color = Color.white;
            try
            {
                if (Net.Mp.Client == null && Net.Mp.Server == null)
                {
                    var rect = new Rect(12, Screen.height - 40, 310, 26);
                    Back(rect); GUI.Label(rect, Note, _rate);
                    return;
                }
                var player = mainscript.s != null ? mainscript.s.player : null;
                var cam = player != null ? (player.Cam != null ? player.Cam : player.mainCam) : null;
                if (cam == null || !cam.isActiveAndEnabled) return;
                Net.ItemTraffic.Current(Rows);
                Vector3 eye = cam.transform.position;
                uint focus = DebugView.Mode == DebugViewMode.Everything ? 0 : DebugView.PinnedNet != 0 ? DebugView.PinnedNet : Net.Entities.PanelFocus(true);
                for (int i = 0; i < Rows.Count; i++)
                {
                    var row = Rows[i];
                    if (DebugView.Mode != DebugViewMode.Everything && (row.Mod || row.Net != focus)) continue;
                    if (row.UpBps <= 0f && row.DownBps <= 0f) continue;
                    var go = Resolve(row);
                    if (go == null || !go.activeInHierarchy) continue;
                    var label = Get(row, go);
                    Vector3 pos = label.Transform.position;
                    float d2 = (pos - eye).sqrMagnitude;
                    if (d2 > 300f * 300f) continue;
                    pos.y += label.Offset;
                    if (label.Renderer != null && label.Renderer.enabled)
                    {
                        Bounds bounds = label.Renderer.bounds;
                        pos = new Vector3(bounds.center.x, Mathf.Max(pos.y, bounds.max.y + 0.25f), bounds.center.z);
                    }
                    else if (label.Collider != null && label.Collider.enabled)
                    {
                        Bounds bounds = label.Collider.bounds;
                        pos = new Vector3(bounds.center.x, Mathf.Max(pos.y, bounds.max.y + 0.25f), bounds.center.z);
                    }
                    Vector3 screen = cam.WorldToScreenPoint(pos);
                    Rect viewport = cam.pixelRect;
                    if (screen.z <= 0f || !viewport.Contains(new Vector2(screen.x, screen.y))) continue;
                    label.Distance = Mathf.Sqrt(d2);
                    UpdateText(label, row);
                    label.X = screen.x - label.Width * 0.5f;
                    label.PointY = Screen.height - screen.y;
                    label.Y = label.PointY - label.Height;
                    if (label.X < 0f || label.Y < 0f || label.X + label.Width > Screen.width || label.Y + label.Height > Screen.height) continue;
                    if (_count == Limit && Drawn[_count - 1].Distance <= label.Distance) continue;
                    if (_count == Limit) Drawn[_count - 1].Placed = false;
                    int at = Math.Min(_count, Limit - 1); _count = Math.Min(_count + 1, Limit);
                    while (at > 0 && Drawn[at - 1].Distance > label.Distance) { Drawn[at] = Drawn[at - 1]; at--; }
                    Drawn[at] = label; label.Placed = true;
                }
                Layout();
                Array.Reverse(Drawn, 0, _count);
                for (int i = 0; i < _count; i++) Paint(Drawn[i]);
            }
            finally { GUI.color = color; GUI.depth = depth; }
        }
        /// Candidates arrive nearest first; share placement with the world tags and reserved overlay areas.
        private static void Layout()
        {
            int count = _count; _count = 0;
            for (int i = 0; i < count; i++)
            {
                var label = Drawn[i];
                float natural = label.PointY - label.Height;
                var rect = new Rect(label.X, natural, label.Width, label.Height);
                var box = new DebugBox(rect.x, rect.y, rect.width, rect.height);
                if (DebugLayout.Labels.Place(ref box, Screen.width, Screen.height)) { label.Y = box.Y; Drawn[_count++] = label; }
                else label.Placed = false;
            }
        }
        private static void Init()
        {
            if (_rate != null) return;
            _rate = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = false, wordWrap = false, padding = new RectOffset(0, 0, 0, 0) };
            _rate.normal.textColor = Color.white;
            _name = new GUIStyle(_rate) { fontSize = 10 };
            _name.normal.textColor = new Color(0.72f, 0.72f, 0.72f);
            _down = Arrow(false); _up = Arrow(true);
            for (int i = 0; i < Cache.Length; i++) Cache[i] = new Label();
        }
        /// Pixel arrows stay legible even when the game's font has no arrow glyphs. Texture row zero is the bottom.
        private static Texture2D Arrow(bool up)
        {
            var tex = new Texture2D(12, 12, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < 12; y++)
                for (int x = 0; x < 12; x++)
                {
                    int v = up ? 11 - y : y;
                    bool ink = v >= 2 && v <= 6 && Math.Abs(x - 5.5f) <= v - 1 || v >= 6 && v <= 10 && x >= 4 && x <= 7;
                    tex.SetPixel(x, y, ink ? Color.white : Color.clear);
                }
            tex.Apply(false, true);
            return tex;
        }
        private static GameObject Resolve(Net.ItemTraffic.Row row)
        {
            if (row.Mod)
            {
                var so = MpApi.Find(row.Net);
                return so != null && !so.Stored ? so.GameObject : null;
            }
            // Root is the live save script, for an owner and a proxy alike; a detached part has its own Root.
            var e = Net.Entities.TrafficEntity(row.Net);
            return e != null && e.Root != null ? e.Root.gameObject : null;
        }
        private static Label Get(Net.ItemTraffic.Row row, GameObject go)
        {
            Label oldest = Cache[0];
            for (int i = 0; i < Cache.Length; i++)
            {
                var label = Cache[i];
                if (label.Net == row.Net && label.Mod == row.Mod && label.Object == go) { label.Seen = Time.frameCount; return label; }
                if (!label.Placed && (oldest.Placed || label.Seen < oldest.Seen)) oldest = label;
            }
            oldest.Net = row.Net; oldest.Mod = row.Mod; oldest.Object = go; oldest.Transform = go.transform;
            // A root component's bounds are cheap. Hierarchies without one use a fixed offset, without walking a car's parts.
            oldest.Renderer = go.GetComponent<Renderer>(); oldest.Collider = go.GetComponent<Collider>();
            var e = row.Mod ? null : Net.Entities.TrafficEntity(row.Net);
            oldest.Offset = e != null && e.PartIndex < 0 && e.Root != null && e.Root.car != null ? 2f : 0.75f;
            oldest.ObjectName = Net.ItemTraffic.ObjectName(go.name);
            if (oldest.ObjectName.Length > 42) oldest.ObjectName = oldest.ObjectName.Substring(0, 39) + "...";
            oldest.Metres = oldest.UpValue = oldest.DownValue = oldest.UpUnit = oldest.DownUnit = -1;
            oldest.Text = null; oldest.Seen = Time.frameCount;
            oldest.NextText = 0;
            return oldest;
        }
        /// Only rounded rate / distance changes rebuild strings; GUIContent and styles live across frames.
        private static void UpdateText(Label label, Net.ItemTraffic.Row row)
        {
            if (label.Text != null && Time.realtimeSinceStartup < label.NextText) return;
            label.NextText = Time.realtimeSinceStartup + .25f;
            bool changed = Rate(label.Down, row.DownBps, ref label.DownUnit, ref label.DownValue);
            changed |= Rate(label.Up, row.UpBps, ref label.UpUnit, ref label.UpValue);
            label.HasDown = row.DownBps > 0f; label.HasUp = row.UpBps > 0f;
            int metres = Mathf.RoundToInt(label.Distance);
            if (metres != label.Metres)
            {
                label.Metres = metres; label.Name.text = label.ObjectName + "  " + metres.ToString(IC) + " m"; changed = true;
            }
            if (!changed && label.Text != null) return;
            label.Width = Mathf.Max(_name.CalcSize(label.Name).x, Mathf.Max(label.HasDown ? _rate.CalcSize(label.Down).x + 17f : 0f, label.HasUp ? _rate.CalcSize(label.Up).x + 17f : 0f)) + 12f;
            label.Height = (label.HasDown ? 16f : 0f) + (label.HasUp ? 16f : 0f) + 20f;
            label.Text = (label.HasDown ? "Download " + label.Down.text + "\n" : "") + (label.HasUp ? "Upload " + label.Up.text + "\n" : "") + label.Name.text;
        }
        private static bool Rate(GUIContent content, float bps, ref int unit, ref int value)
        {
            int u = bps <= 0f ? -1 : bps < 1024f ? 0 : bps < 1024f * 1024f ? 1 : 2;
            int v = u < 0 ? 0 : Mathf.RoundToInt(u == 0 ? bps : u == 1 ? bps / 1024f * 10f : bps / (1024f * 1024f) * 100f);
            if (u == unit && v == value) return false;
            unit = u; value = v;
            content.text = u < 0 ? "" : u == 0 ? v.ToString(IC) + " B/s" : u == 1 ? (v / 10.0).ToString("F1", IC) + " KB/s" : (v / 100.0).ToString("F2", IC) + " MB/s";
            return true;
        }
        private static void Back(Rect rect)
        {
            GUI.color = new Color(0, 0, 0, 0.78f); GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = Color.white;
        }
        private static void Paint(Label label)
        {
            float fade = Mathf.Clamp01(1 - label.Distance / 350f);
            float bottom = label.Y + label.Height;
            if (bottom < label.PointY)
            {
                GUI.color = new Color(0.72f, 0.72f, 0.72f);
                GUI.DrawTexture(new Rect(label.X + label.Width * 0.5f, bottom, 1f, label.PointY - bottom), Texture2D.whiteTexture);
                GUI.color = Color.white;
            }
            GUI.color = new Color(0, 0, 0, .9f * fade);
            GUI.DrawTexture(new Rect(label.X, label.Y, label.Width, label.Height), Texture2D.whiteTexture);
            float y = label.Y + 4f;
            if (label.HasDown) { Line(label, y, _down, label.Down, new Color(0.4f, 0.75f, 1f, fade)); y += 16f; }
            if (label.HasUp) { Line(label, y, _up, label.Up, new Color(1f, 0.65f, 0.3f, fade)); y += 16f; }
            GUI.color = new Color(1, 1, 1, fade);
            GUI.Label(new Rect(label.X + 6f, y, label.Width - 12f, 16f), label.Name, _name);
        }
        private static void Line(Label label, float y, Texture2D arrow, GUIContent text, Color color)
        {
            GUI.color = color;
            GUI.DrawTexture(new Rect(label.X + 6f, y + 1f, 12f, 12f), arrow);
            GUI.Label(new Rect(label.X + 23f, y, label.Width - 29f, 16f), text, _rate);
            GUI.color = Color.white;
        }
        /// Bridge snapshot of the last Repaint: x/y are the box's top left in IMGUI screen pixels, nearest drawn last.
        internal static string Json()
        {
            if (!DebugView.Active || !Net.ItemTraffic.Show || Net.Mp.Client == null && Net.Mp.Server == null || _frame < Time.frameCount - 1) return "[]";
            var sb = new StringBuilder("[");
            for (int i = 0; i < _count; i++)
            {
                var label = Drawn[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"net\":").Append(label.Net).Append(",\"mod\":").Append(label.Mod ? "true" : "false")
                  .Append(",\"x\":").Append(label.X.ToString("F1", IC)).Append(",\"y\":").Append(label.Y.ToString("F1", IC))
                  .Append(",\"text\":").Append(TLDRevamp.Json.Str(label.Text)).Append(",\"distance\":").Append(label.Distance.ToString("F1", IC)).Append('}');
            }
            return sb.Append(']').ToString();
        }
    }
}
namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        /// Live entities only: a far-store record has no world object to label.
        internal static Ent TrafficEntity(uint net) => ByNet.TryGetValue(net, out var e) ? e : null;
    }
}
