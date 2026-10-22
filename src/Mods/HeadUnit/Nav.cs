using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeadUnit
{
    public enum NavKind : byte { BendLeft, BendRight, Descent, Climb, Danger }

    /// Something on the road ahead.
    public struct NavEvent
    {
        public NavKind Kind;
        public float Distance;   // metres along the road from the car
        public float Amount;     // bends: degrees; slopes: percent; danger: 0–1
        public string Note;      // danger: what (a wreck, an animal...)
    }

    /// One source of things ahead. Providers run on this machine only (each player's unit reads its own world), every
    /// half second while the unit is in use.
    public interface INavProvider
    {
        string Name { get; }
        void Scan(in NavContext ctx, List<NavEvent> into);
    }

    /// What a provider gets: where the car is and the road ahead, as points every few metres from the car on.
    public readonly struct NavContext
    {
        public readonly Vector3d Pos;
        public readonly Vector3 Forward;
        public readonly float Speed;                  // m/s
        public readonly List<Vector3d> Path;          // the road ahead, from the nearest point, in driving order
        public readonly List<float> PathDist;         // distance along the road for each point
        public NavContext(Vector3d pos, Vector3 fwd, float speed, List<Vector3d> path, List<float> dist) { Pos = pos; Forward = fwd; Speed = speed; Path = path; PathDist = dist; }
    }

    /// The navigation framework: finds the road ahead of the car (the game's own road bones, the same everywhere for the
    /// same world seed), asks every provider what lies on it, and keeps the result sorted by distance.
    /// Built in: bends and slopes (RoadShape). Danger (wrecks, animals, broken bridges) is a provider still to write —
    /// register it with Nav.Providers.Add.
    public static class Nav
    {
        public static readonly List<INavProvider> Providers = new List<INavProvider> { new RoadShape() };
        public static readonly List<NavEvent> Events = new List<NavEvent>();
        public static float LookAheadM = 1500f;
        /// The road ahead found last (for the map).
        public static List<Vector3d> Path => _path;
        public static string Problem;
        private static float _next;
        private static readonly List<Vector3d> _path = new List<Vector3d>();
        private static readonly List<float> _dist = new List<float>();

        public static void Tick(Transform car, Rigidbody rb)
        {
            if (Time.realtimeSinceStartup < _next || car == null || mainscript.s == null) return;
            _next = Time.realtimeSinceStartup + 0.5f;
            Events.Clear(); Problem = null;
            var pos = mainscript.GlobalFromUnityPos(car.position);
            var fwd = car.forward; fwd.y = 0; fwd.Normalize();
            float speed = rb != null ? rb.velocity.magnitude : 0f;
            if (!PathAhead(pos, fwd, _path, _dist)) { Problem = "no road here"; return; }
            var ctx = new NavContext(pos, fwd, speed, _path, _dist);
            foreach (var p in Providers)
            {
                try { p.Scan(ctx, Events); }
                catch (Exception e) { Plugin.Log.LogError($"nav provider {p.Name}: {e}"); }
            }
            Events.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        /// The next thing worth saying, for the screen and the panel ("left 60 deg in 300 m").
        public static string NextText() => Problem ?? (Events.Count == 0 ? "road straight on" : UnitBehaviour.Describe(Events[0]));

        /// The road's bones from the point nearest the car onward, in the direction the car faces, chained across road
        /// pieces, up to LookAheadM.
        private static bool PathAhead(Vector3d pos, Vector3 fwd, List<Vector3d> path, List<float> dist)
        {
            path.Clear(); dist.Clear();
            List<roadGenScript.roadClass> roads;
            try { roads = mapSettingScript.RoadsNear(pos); }
            catch { return false; }
            roads = roads.FindAll(r => r.generated);
            if (roads.Count == 0) return false;
            var road = roadGenScript.NearestPointOnRoadBone(pos, roads, out var near);
            if (road == null || Flat(near - pos) > 60.0) return false;   // off the road
            var used = new HashSet<roadGenScript.roadClass>();
            var bones = Bones(road);
            int k = Nearest(bones, near);
            bool forward = k + 1 < bones.Count ? Vector3.Dot(Dir(bones[k], bones[k + 1]), fwd) >= 0f : k > 0 && Vector3.Dot(Dir(bones[k - 1], bones[k]), fwd) >= 0f;
            float total = 0f;
            path.Add(near); dist.Add(0f);
            while (road != null && total < LookAheadM)
            {
                used.Add(road);
                for (int i = forward ? k + 1 : k - 1; forward ? i < bones.Count : i >= 0; i += forward ? 1 : -1)
                {
                    total += (float)Flat(bones[i] - path[path.Count - 1]);
                    path.Add(bones[i]); dist.Add(total);
                    if (total >= LookAheadM) return true;
                }
                // the next piece: the one that starts (or ends) where this one stopped
                var tail = path[path.Count - 1];
                road = null;
                foreach (var r in roads)
                {
                    if (used.Contains(r)) continue;
                    var b = Bones(r);
                    if (b.Count < 2) continue;
                    if (Flat(b[0] - tail) < 15.0) { road = r; bones = b; k = 0; forward = true; break; }
                    if (Flat(b[b.Count - 1] - tail) < 15.0) { road = r; bones = b; k = b.Count - 1; forward = false; break; }
                }
            }
            return path.Count > 2;
        }

        private static List<Vector3d> Bones(roadGenScript.roadClass r)
        {
            var l = new List<Vector3d>();
            if (r.roadElements != null) foreach (var el in r.roadElements) if (el?.bonePos != null) l.AddRange(el.bonePos);
            return l;
        }

        private static int Nearest(List<Vector3d> b, Vector3d p)
        {
            int k = 0; double best = double.MaxValue;
            for (int i = 0; i < b.Count; i++) { double d = Flat(b[i] - p); if (d < best) { best = d; k = i; } }
            return k;
        }

        internal static double Flat(Vector3d v) => Math.Sqrt(v.x * v.x + v.z * v.z);
        internal static Vector3 Dir(Vector3d a, Vector3d b) { var d = (Vector3)(b - a); d.y = 0; return d.normalized; }
    }

    /// Bends and slopes from the road's shape: the heading change over each 60 m of road, and the height change over
    /// each 100 m.
    public sealed class RoadShape : INavProvider
    {
        public string Name => "road shape";
        public static float BendMinDeg = 25f, SlopeMinPct = 6f;

        public void Scan(in NavContext c, List<NavEvent> into)
        {
            var p = c.Path; var d = c.PathDist;
            // bends: walk 60 m windows; a turn sharper than BendMinDeg is one event (merged while it continues)
            float lastBendEnd = -1f;
            for (int i = 0; i < p.Count; i++)
            {
                int j = Ahead(d, i, 60f);
                if (j < 0) break;
                int h = Ahead(d, j, 30f); if (h < 0) break;
                var a = Nav.Dir(p[i], p[j]); var b = Nav.Dir(p[j], p[h]);
                float ang = Vector3.SignedAngle(a, b, Vector3.up);
                if (Mathf.Abs(ang) < BendMinDeg || d[i] < lastBendEnd) continue;
                into.Add(new NavEvent { Kind = ang < 0 ? NavKind.BendLeft : NavKind.BendRight, Distance = d[j], Amount = Mathf.Abs(ang) });
                lastBendEnd = d[h] + 30f;
            }
            // slopes: 100 m windows
            float lastSlopeEnd = -1f;
            for (int i = 0; i < p.Count; i++)
            {
                int j = Ahead(d, i, 100f);
                if (j < 0) break;
                float run = d[j] - d[i];
                float pct = (float)(p[j].y - p[i].y) / Mathf.Max(1f, run) * 100f;
                if (Mathf.Abs(pct) < SlopeMinPct || d[i] < lastSlopeEnd) continue;
                into.Add(new NavEvent { Kind = pct < 0 ? NavKind.Descent : NavKind.Climb, Distance = d[i], Amount = Mathf.Abs(pct) });
                lastSlopeEnd = d[j];
            }
        }

        private static int Ahead(List<float> d, int from, float m)
        {
            for (int k = from + 1; k < d.Count; k++) if (d[k] - d[from] >= m) return k;
            return -1;
        }
    }
}
