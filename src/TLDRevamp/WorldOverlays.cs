using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using TLDRevamp.Net;

namespace TLDRevamp
{
    /// Local read-only overlays. Selection and text refresh at 4 Hz; Repaint only projects bounded cached objects.
    internal static class WorldOverlays
    {
        internal static bool Ownership, Ghost, GhostAll, Links;
        private static OverlayNearest<Entities.Ent> _nearest;
        private static Row[] _rows;
        private static Edge[] _edges;
        private static GUIStyle _style;
        private static Material _lines;
        private static readonly GUIContent NoSession = new GUIContent("World overlays: not in a multiplayer game");
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private static readonly Color Green = new Color(.35f, 1, .5f), Yellow = new Color(1, .85f, .2f), Red = new Color(1, .3f, .25f), Grey = new Color(.75f, .8f, .85f);
        private static double _next, _ms, _maxMs;
        private static double _linkMs, _linkMaxMs;
        private static long _linkRefreshes;
        private static Vector2 _noSessionSize;
        private static long _paints, _allocated;
        private static int _count, _edgeCount, _frame = -1, _scanned, _dropped;
        private static int _flags;
        private sealed class Row
        {
            internal Entities.Ent Entity;
            internal Transform Frame;
            internal uint Epoch;
            internal int Owner;
            internal bool Proxy, HasSample, Ghost, Stale;
            internal Vector3d Pose;
            internal Quaternion Rotation;
            internal Vector3 Center, Extents;
            internal double Age, Error;
            internal float Angle;
            internal Color Colour;
            internal readonly GUIContent Text = new GUIContent();
            internal Vector2 Size;
        }
        private sealed class Edge
        {
            internal Transform A, B;
            internal int OwnerA, OwnerB;
            internal uint NetA, NetB;
            internal int Rider;
            internal bool Bad;
            internal string Kind;
            internal readonly GUIContent TextA = new GUIContent(), TextB = new GUIContent();
            internal Vector2 SizeA, SizeB;
        }
        private static void Init()
        {
            if (_rows != null) return;
            _nearest = new OverlayNearest<Entities.Ent>(WorldOverlayLogic.Limit);
            _rows = new Row[WorldOverlayLogic.Limit + 1];
            for (int i = 0; i < _rows.Length; i++) _rows[i] = new Row();
            _edges = new Edge[160];
            for (int i = 0; i < _edges.Length; i++) _edges[i] = new Edge();
            _style = new GUIStyle(GUI.skin.label) { fontSize = 11, richText = false, wordWrap = false, padding = new RectOffset(0, 0, 0, 0) };
            _style.normal.textColor = Color.white;
            _noSessionSize = _style.CalcSize(NoSession);
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader != null)
            {
                _lines = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                _lines.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _lines.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _lines.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _lines.SetInt("_ZWrite", 0); _lines.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            }
        }
        internal static void Draw()
        {
            if (!Ownership && !Ghost && !Links || Event.current.type != EventType.Repaint) return;
            long start = Stopwatch.GetTimestamp(), bytes = SessionLog.AllocCounter ? GC.GetAllocatedBytesForCurrentThread() : 0;
            Color colour = GUI.color; int depth = GUI.depth;
            try
            {
                Init(); GUI.depth = 11;
                var pl = mainscript.s != null ? mainscript.s.player : null;
                var cam = pl != null ? (pl.Cam != null ? pl.Cam : pl.mainCam) : null;
                if (!Entities.InSession || cam == null || !cam.isActiveAndEnabled)
                {
                    _count = _edgeCount = 0;
                    Paint(new Vector2(12, Screen.height - 66), NoSession, _noSessionSize, Grey);
                    return;
                }
                double now = Time.realtimeSinceStartupAsDouble;
                int flags = (Ownership ? 1 : 0) | (Ghost ? 2 : 0) | (GhostAll ? 4 : 0) | (Links ? 8 : 0);
                if (now >= _next || flags != _flags)
                {
                    _next = now + .25; _flags = flags; Refresh(cam, now);
                }
                _frame = Time.frameCount;
                if ((Ghost || Links) && _lines != null && _lines.SetPass(0))
                {
                    GL.PushMatrix();
                    try
                    {
                        GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0); GL.Begin(GL.LINES);
                        try
                        {
                            for (int i = 0; i < _count; i++)
                            {
                                var r = _rows[i];
                                if (!Live(r) || !r.Ghost || !r.HasSample) continue;
                                if (!Entities.OverlaySample(r.Entity, out r.Pose, out r.Rotation)) continue;
                                Vector3 pose = mainscript.UnityPosFromGlobal(r.Pose);
                                var c = r.Colour; c.a = .4f;
                                Box(cam, r, pose, c);
                                Segment(cam, pose, r.Frame.position, c);
                            }
                            if (Links) for (int i = 0; i < _edgeCount; i++)
                            {
                                var e = _edges[i];
                                if (e.A != null && e.B != null) Segment(cam, e.A.position, e.B.position, e.Bad ? Red : Grey);
                            }
                        }
                        finally { GL.End(); }
                    }
                    finally { GL.PopMatrix(); }
                }
                for (int i = _count - 1; i >= 0; i--)
                {
                    var r = _rows[i];
                    if (!Live(r) || !Ownership && !r.Ghost) continue;
                    if (Project(cam, r.Frame.position + Vector3.up * (r.Entity.Root.car != null ? 2 : .75f), out var screen))
                        Paint(screen, r.Text, r.Size, r.Colour);
                }
                if (Links) for (int i = 0; i < _edgeCount; i++)
                {
                    var e = _edges[i];
                    if (e.A == null || e.B == null) continue;
                    if (Project(cam, e.A.position, out var a)) Paint(a, e.TextA, e.SizeA, e.Bad ? Red : Grey);
                    if (Project(cam, e.B.position, out var b)) Paint(b + new Vector2(0, 14), e.TextB, e.SizeB, e.Bad ? Red : Grey);
                }
            }
            finally
            {
                GUI.color = colour; GUI.depth = depth;
                double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                _ms += ms; _maxMs = Math.Max(_maxMs, ms); _paints++;
                if (SessionLog.AllocCounter) _allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
            }
        }
        private static bool Live(Row r) => r.Entity.Root != null && r.Frame != null && r.Entity.Root.gameObject.activeInHierarchy &&
            r.Entity.Epoch == r.Epoch && r.Entity.OwnerId == r.Owner && r.Entity.Proxy == r.Proxy;
        private static void Refresh(Camera cam, double now)
        {
            for (int i = 0; i < _count; i++) { _rows[i].Entity = null; _rows[i].Frame = null; }
            for (int i = 0; i < _edgeCount; i++) { _edges[i].A = null; _edges[i].B = null; }
            _count = _edgeCount = _dropped = 0;
            if (Ownership || Links || GhostAll) Entities.OverlaySelect(_nearest, cam.transform.position);
            else _nearest.Clear();
            _scanned = _nearest.Count;
            var focus = Ghost ? Entities.OverlayLook() : null;
            bool found = false;
            for (int i = 0; i < _nearest.Count; i++)
            {
                var e = _nearest.Items[i];
                bool selected = e == focus; found |= selected;
                if (Ownership || Ghost && (selected || GhostAll)) Fill(_rows[_count++], e, Ghost && (selected || GhostAll), now);
            }
            if (!found && focus != null && focus.Root != null && focus.Root.gameObject.activeInHierarchy)
                Fill(_rows[_count++], focus, true, now);
            if (Links)
            {
                long start = Stopwatch.GetTimestamp();
                Entities.OverlayLinks(_nearest);
                double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                _linkMs += ms; _linkMaxMs = Math.Max(_linkMaxMs, ms); _linkRefreshes++;
            }
        }
        private static void Fill(Row r, Entities.Ent e, bool ghost, double now)
        {
            r.Entity = e; r.Frame = Entities.OverlayFrame(e); r.Epoch = e.Epoch; r.Owner = e.OwnerId; r.Proxy = e.Proxy; r.Ghost = ghost;
            r.HasSample = Entities.OverlaySample(e, out r.Pose, out r.Rotation);
            r.Age = r.HasSample && e.TelemetryReceived > 0 ? Math.Max(0, now - e.TelemetryReceived) : double.NaN;
            r.Error = r.HasSample ? (mainscript.GlobalFromUnityPos(r.Frame.position) - r.Pose).magnitude : double.NaN;
            r.Angle = r.HasSample ? Quaternion.Angle(r.Frame.rotation, r.Rotation) : 0;
            bool moving = Entities.OverlayMoving(e);
            r.Stale = WorldOverlayLogic.Stale(r.Age, moving);
            int c = WorldOverlayLogic.Colour(r.Error);
            r.Colour = !e.Proxy ? Green : c == 0 ? Green : c == 1 ? Yellow : c == 2 ? Red : Grey;
            string owner = WorldOverlayLogic.Owner(e.OwnerId, Entities.MyId);
            SetText(r.Text, ref r.Size, owner + (e.Proxy ? " / proxy" : " / simulated") + " / epoch " + e.Epoch.ToString(IC) + "\n" +
                (e.Proxy ? WorldOverlayLogic.Error(r.Error, r.Angle, r.Age, moving) + "\n" +
                    Entities.OverlayMode(e) + " / " + WorldOverlayLogic.Age(r.Age) : Entities.OverlaySendState(e)));
            if (!ghost) return;
            // Root bounds only, without walking a vehicle's renderers. Missing bounds use a small fixed box.
            var col = e.Root.GetComponent<Collider>();
            var renderer = e.Root.GetComponent<Renderer>();
            Bounds bounds = col != null ? col.bounds : renderer != null ? renderer.bounds : new Bounds(r.Frame.position, Vector3.one * .7f);
            r.Center = Quaternion.Inverse(r.Frame.rotation) * (bounds.center - r.Frame.position);
            var size = bounds.extents;
            var inv = Quaternion.Inverse(r.Frame.rotation);
            var x = inv * new Vector3(size.x, 0, 0); var y = inv * new Vector3(0, size.y, 0); var z = inv * new Vector3(0, 0, size.z);
            r.Extents = Vector3.Max(new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y), Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z)), Vector3.one * .1f);
        }
        internal static void Link(Transform a, Transform b, int ownerA, int ownerB, string kind, bool sameOwner, bool reciprocal, uint netA, uint netB, int rider = -1)
        {
            if (a == null || b == null || a == b) return;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || (a.position - pl.transform.position).sqrMagnitude > 300f * 300f) return;
            bool bad = WorldOverlayLogic.BadLink(ownerA, ownerB, sameOwner, reciprocal);
            if (kind == "part" && netA != 0 && netA == netB && !bad) return;
            for (int i = 0; i < _edgeCount; i++)
                if (_edges[i].A == a && _edges[i].B == b && _edges[i].Kind == kind) { _edges[i].Bad |= bad; return; }
            if (_edgeCount == _edges.Length) { _dropped++; return; }
            var edge = _edges[_edgeCount++]; edge.A = a; edge.B = b; edge.OwnerA = ownerA; edge.OwnerB = ownerB; edge.Bad = bad; edge.Kind = kind;
            edge.NetA = netA; edge.NetB = netB; edge.Rider = rider;
            SetText(edge.TextA, ref edge.SizeA, kind + " / " + WorldOverlayLogic.Owner(ownerA, Entities.MyId));
            SetText(edge.TextB, ref edge.SizeB, WorldOverlayLogic.Owner(ownerB, Entities.MyId));
        }
        private static bool Project(Camera cam, Vector3 world, out Vector2 screen)
        {
            Vector3 p = cam.WorldToScreenPoint(world); screen = new Vector2(p.x, Screen.height - p.y);
            return p.z > cam.nearClipPlane && cam.pixelRect.Contains(new Vector2(p.x, p.y));
        }
        private static void Segment(Camera cam, Vector3 a, Vector3 b, Color colour)
        {
            Vector3 ca = cam.WorldToScreenPoint(a), cb = cam.WorldToScreenPoint(b);
            if (ca.z <= cam.nearClipPlane || cb.z <= cam.nearClipPlane) return;
            GL.Color(colour); GL.Vertex3(ca.x, Screen.height - ca.y, 0); GL.Vertex3(cb.x, Screen.height - cb.y, 0);
        }
        private static Vector3 Corner(Row r, Vector3 p, int i) => p + r.Rotation * (r.Center + new Vector3(
            (i & 1) == 0 ? -r.Extents.x : r.Extents.x, (i & 2) == 0 ? -r.Extents.y : r.Extents.y, (i & 4) == 0 ? -r.Extents.z : r.Extents.z));
        private static void Box(Camera cam, Row r, Vector3 p, Color colour)
        {
            for (int i = 0; i < 8; i++) for (int bit = 1; bit <= 4; bit *= 2)
                if ((i & bit) == 0) Segment(cam, Corner(r, p, i), Corner(r, p, i | bit), colour);
        }
        private static void SetText(GUIContent text, ref Vector2 size, string value)
        {
            if (text.text == value) return;
            text.text = value; size = _style.CalcSize(text);
        }
        private static void Paint(Vector2 at, GUIContent text, Vector2 size, Color colour)
        {
            var rect = new Rect(Mathf.Clamp(at.x - size.x * .5f, 0, Mathf.Max(0, Screen.width - size.x - 8)), Mathf.Clamp(at.y - size.y, 0, Mathf.Max(0, Screen.height - size.y - 4)), size.x + 8, size.y + 4);
            GUI.color = new Color(0, 0, 0, .75f); GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = colour; rect.x += 4; rect.y += 2; GUI.Label(rect, text, _style);
        }
        internal static string Command(string tool, string action)
        {
            if (action != "" && action != "state" && action != "on" && action != "off" && action != "show" && action != "hide" && action != "reset")
                return "{\"error\":\"use on|off|state|reset\"}";
            if (action == "reset") { _paints = _allocated = _linkRefreshes = 0; _ms = _maxMs = _linkMs = _linkMaxMs = 0; }
            else if (action != "" && action != "state")
            {
                bool value = action == "on" || action == "show";
                switch (tool) { case "ownership": Ownership = value; break; case "ghost": Ghost = value; break; case "ghostall": GhostAll = value; break; case "links": Links = value; break; }
                _next = 0;
            }
            var sb = new StringBuilder("{\"ownership\":").Append(Ownership ? "true" : "false").Append(",\"ghost\":").Append(Ghost ? "true" : "false")
                .Append(",\"ghostAll\":").Append(GhostAll ? "true" : "false").Append(",\"links\":").Append(Links ? "true" : "false")
                .Append(",\"limit\":40,\"nearby\":").Append(_scanned).Append(",\"linkLimit\":160,\"dropped\":").Append(_dropped)
                .Append(",\"linesAvailable\":").Append(_lines != null ? "true" : "false")
                .Append(",\"repaints\":").Append(_paints).Append(",\"meanMs\":").Append((_paints > 0 ? _ms / _paints : 0).ToString("F4", IC))
                .Append(",\"maxMs\":").Append(_maxMs.ToString("F4", IC)).Append(",\"bytesPerRepaint\":").Append(SessionLog.AllocCounter ? (_paints > 0 ? _allocated / _paints : 0).ToString(IC) : "null")
                .Append(",\"linkRefreshes\":").Append(_linkRefreshes).Append(",\"linkMeanMs\":").Append((_linkRefreshes > 0 ? _linkMs / _linkRefreshes : 0).ToString("F4", IC))
                .Append(",\"linkMaxMs\":").Append(_linkMaxMs.ToString("F4", IC)).Append(",\"rows\":[");
            bool live = (Ownership || Ghost || Links) && Entities.InSession && _frame >= Time.frameCount - 1;
            if (live) for (int i = 0; i < _count; i++)
            {
                var r = _rows[i]; if (!Live(r)) continue;
                if (sb[sb.Length - 1] != '[') sb.Append(',');
                sb.Append("{\"net\":").Append(r.Entity.NetId).Append(",\"owner\":").Append(r.Owner).Append(",\"epoch\":").Append(r.Epoch).Append(",\"proxy\":").Append(r.Proxy ? "true" : "false")
                    .Append(",\"ghost\":").Append(r.Ghost ? "true" : "false").Append(",\"stale\":").Append(r.Stale ? "true" : "false")
                    .Append(",\"errorM\":").Append(Json.Num(r.Error)).Append(",\"angleDeg\":").Append(Json.Num(r.Angle)).Append(",\"ageS\":").Append(Json.Num(r.Age))
                    .Append(",\"text\":").Append(Json.Str(r.Text.text)).Append('}');
            }
            sb.Append("],\"edges\":[");
            if (live && Links) for (int i = 0; i < _edgeCount; i++)
            {
                var e = _edges[i]; if (e.A == null || e.B == null) continue;
                if (sb[sb.Length - 1] != '[') sb.Append(',');
                sb.Append("{\"kind\":").Append(Json.Str(e.Kind)).Append(",\"netA\":").Append(e.NetA).Append(",\"netB\":").Append(e.NetB).Append(",\"rider\":").Append(e.Rider)
                    .Append(",\"ownerA\":").Append(e.OwnerA).Append(",\"ownerB\":").Append(e.OwnerB).Append(",\"bad\":").Append(e.Bad ? "true" : "false").Append('}');
            }
            return sb.Append("]}").ToString();
        }
    }
}
