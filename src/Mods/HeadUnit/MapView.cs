using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeadUnit
{
    /// The NAV tab's map: the game's roads around the car (the road generators' pieces in the loaded area: their bones
    /// where a piece is built, a straight line from end to end where it isn't yet), the road ahead in the unit's
    /// colour, the car as an arrow, heading up. Redrawn up to 30 times a second (twice a second looked like an old
    /// car's navigation, playtest 2026-10-15), on this machine only.
    public sealed class MapView : IDisposable
    {
        public const int Wpx = 384, Hpx = 128;                      // the picture's shape: the screen's middle (3 : 1)
        public static readonly float[] Zooms = { 60f, 120f, 250f, 500f, 1000f, 2000f };   // metres from the car to the top edge
        public int Zoom = 2;
        public Texture2D Texture;
        private readonly Color32[] _px = new Color32[Wpx * Hpx], _clear = new Color32[Wpx * Hpx];
        public static float FrameS = 1f / 30f;
        private readonly Color32 _bg = new Color32(14, 17, 22, 255), _road = new Color32(150, 155, 165, 255), _unbuilt = new Color32(70, 74, 82, 255), _main = new Color32(240, 222, 120, 255), _ahead, _car;
        private float _next;
        private static float _roadsAt; private static readonly List<List<Vector3d>> _roads = new List<List<Vector3d>>(); private static readonly List<bool> _built = new List<bool>(), _mainRoad = new List<bool>();   // the main road (roadTypeEmu.mainRoad): its own colour

        public MapView(Color accent)
        {
            _ahead = accent; _car = Color.white;
            for (int i = 0; i < _clear.Length; i++) _clear[i] = _bg;
            Texture = new Texture2D(Wpx, Hpx, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        }

        public void Dispose() { if (Texture != null) UnityEngine.Object.Destroy(Texture); }

        public void ZoomBy(int d) { Zoom = Mathf.Clamp(Zoom + d, 0, Zooms.Length - 1); _next = 0f; }

        public static string ZoomText(int z) { float m = Zooms[z]; return m >= 1000f ? $"{m / 1000f:0.#} km" : $"{m:0} m"; }

        public void Draw(Transform car, List<Vector3d> ahead)
        {
            if (Time.realtimeSinceStartup < _next || mainscript.s == null) return;
            _next = Time.realtimeSinceStartup + FrameS;
            var pos = mainscript.GlobalFromUnityPos(car.position);
            var fwd = car.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var right = new Vector3(fwd.z, 0f, -fwd.x);
            // the car near the bottom middle: most of the picture is the road ahead
            float scale = Hpx * 0.8f / Zooms[Zoom];             // pixels a metre
            float cx = Wpx * 0.5f, cy = Hpx * 0.2f;
            Vector2 P(Vector3d g)
            {
                float dx = (float)(g.x - pos.x), dz = (float)(g.z - pos.z);
                return new Vector2(cx + (dx * right.x + dz * right.z) * scale, cy + (dx * fwd.x + dz * fwd.z) * scale);
            }

            Array.Copy(_clear, _px, _px.Length);
            Roads(pos, Zooms[Zoom] * 2.2f);
            for (int r = 0; r < _roads.Count; r++)
            {
                var pts = _roads[r];
                for (int i = 1; i < pts.Count; i++) Line(P(pts[i - 1]), P(pts[i]), _mainRoad[r] ? _main : _built[r] ? _road : _unbuilt, _mainRoad[r] ? (_built[r] ? 3 : 2) : _built[r] ? 2 : 1);
            }
            if (ahead != null) for (int i = 1; i < ahead.Count; i++) Line(P(ahead[i - 1]), P(ahead[i]), _ahead, 3);
            // the car: an arrow pointing up
            for (int y = 0; y < 12; y++)
            {
                int half = (12 - y) / 3;
                for (int x = -half; x <= half; x++) Dot((int)cx + x, (int)cy - 4 + y, _car);
            }
            Texture.SetPixels32(_px);
            Texture.Apply(false);
        }

        /// The road pieces near `pos` (refreshed every few seconds: the area loaded around the player changes slowly).
        private static void Roads(Vector3d pos, float radius)
        {
            if (Time.realtimeSinceStartup < _roadsAt) return;
            _roadsAt = Time.realtimeSinceStartup + 3f;
            _roads.Clear(); _built.Clear(); _mainRoad.Clear();
            var gens = menuhandler.s != null && menuhandler.s.currentMainMap != null ? menuhandler.s.currentMainMap.roadGens : null;
            if (gens == null) return;
            double reach = Math.Max(radius, 2500f);
            var seen = new HashSet<roadGenScript.roadClass>();
            foreach (var g in gens)
            {
                if (g == null || g.chunks == null) continue;
                foreach (var ch in g.chunks.Values)
                {
                    if (ch?.roads == null) continue;
                    foreach (var r in ch.roads)
                    {
                        if (r == null || !seen.Add(r)) continue;
                        if (Nav.Flat(r.start - pos) > reach && Nav.Flat(r.end - pos) > reach) continue;
                        var pts = new List<Vector3d>();
                        if (r.roadElements != null) foreach (var el in r.roadElements) if (el?.bonePos != null) pts.AddRange(el.bonePos);
                        bool built = pts.Count >= 2;
                        if (!built) { pts.Clear(); pts.Add(r.start); pts.Add(r.end); }
                        _roads.Add(pts); _built.Add(built); _mainRoad.Add(r.roadType == roadGenScript.roadTypeEmu.mainRoad);
                    }
                }
            }
        }

        private void Line(Vector2 a, Vector2 b, Color32 c, int width)
        {
            // clip to the picture (with a margin) before stepping along it
            if ((a.x < -50 && b.x < -50) || (a.x > Wpx + 50 && b.x > Wpx + 50) || (a.y < -50 && b.y < -50) || (a.y > Hpx + 50 && b.y > Hpx + 50)) return;
            float len = Vector2.Distance(a, b);
            if (len > 4000f) return;
            int steps = Mathf.Max(1, Mathf.CeilToInt(len));
            int h = width / 2;
            for (int i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(a, b, i / (float)steps);
                int x = Mathf.RoundToInt(p.x), y = Mathf.RoundToInt(p.y);
                for (int oy = -h; oy <= width - 1 - h; oy++)
                    for (int ox = -h; ox <= width - 1 - h; ox++) Dot(x + ox, y + oy, c);
            }
        }

        private void Dot(int x, int y, Color32 c) { if (x >= 0 && x < Wpx && y >= 0 && y < Hpx) _px[y * Wpx + x] = c; }
    }
}
