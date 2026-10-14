using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// The host checks every hit on a player by rewinding time (lag compensation, docs/MULTIPLAYER-ARCHITECTURE.md
    /// §11.7). A hit is decided on the attacker's screen, against the body drawn there (PlayerCombat): what you see is
    /// what you hit. That body is the target as it was some time ago (the attacker's ping, then the delay copies are
    /// drawn behind), so the host keeps the last seconds of every player's positions and asks: in that window, did
    /// the shot's line pass the target's body (a blade: was the target within reach of it; a creature: within its
    /// reach)? And was the shot fired from where the attacker was? A hit that couldn't have happened — a target
    /// kilometres away, a line nowhere near them, a blade 30 m out — is refused, counted and logged as [anomaly];
    /// the victim never sees it. Walls aren't checked (the host doesn't have every player's surroundings loaded).
    /// No history yet (a player who just joined): allowed.
    public static class HitCheck
    {
        public static bool Enabled = true;   // A/B: false = before v0.66.9 (the host forwarded every hit)
        public const byte KShot = 0, KCreature = 1, KMelee = 2;
        public static float HistoryS = 3f;
        public static float DrawDelayS = 0.6f;   // copies are drawn up to this far behind (adaptive buffer + jitter)
        public static float SlackS = 0.3f;
        public static float BodyHalfM = 0.95f, BodyR = 0.35f;
        public static float MarginM = 1.0f;      // on top of the body: aim at an edge, rounding, one sample's motion
        public static float OriginM = 4f;        // the shot from near the attacker (eye, a gun's muzzle)
        public static float MeleeM = 2.5f, CreatureM = 6f, ShotMaxM = 2000f;
        public static float CreatureLagM = 8f;   // the creature's copy here trails its owner's (draw delay, state rate)
        public static long Checked, Allowed, Refused, NoHistory;
        public static string LastRefusal = "";
        private static int _logged;

        private struct Sample { public double T; public Vector3d P; }
        private static readonly Dictionary<int, List<Sample>> _hist = new Dictionary<int, List<Sample>>();

        /// Server: a player's position as the host has it now (their states; the host's own every tick).
        internal static void Record(int id, Vector3d p)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (!_hist.TryGetValue(id, out var l)) _hist[id] = l = new List<Sample>(64);
            l.Add(new Sample { T = now, P = p });
            int drop = 0;
            while (drop < l.Count - 1 && now - l[drop].T > HistoryS) drop++;
            if (drop > 0) l.RemoveRange(0, drop);
        }

        internal static void Forget(int id) => _hist.Remove(id);
        internal static void Clear() { _hist.Clear(); }

        /// Server: may this hit go to its victim? `origin` global (the shot's start, the blade, the creature); a
        /// creature's hit names the creature (`creatureNet`, `creatureIdx`).
        internal static bool Allow(int from, int target, byte kind, Vector3d origin, Vector3 dir, uint creatureNet, int creatureIdx, out string why)
        {
            why = "";
            if (!Enabled) return true;
            Checked++;
            double now = Time.realtimeSinceStartupAsDouble;
            Session.Pings.TryGetValue(from, out int pingMs);
            double back = Mathf.Clamp(pingMs, 0, 2000) / 1000.0 + DrawDelayS + SlackS;
            // a creature's hit: the creature is the attacker's (they own its shared object) and is where they say it is
            if (kind == KCreature)
            {
                if (!Entities.CreatureOf(from, creatureNet, creatureIdx, out var where, out bool known))
                { why = $"creature {creatureNet}/{creatureIdx} is not the attacker's"; return Refuse(from, target, kind, why); }
                double off = known ? (where - origin).magnitude : 0;
                if (off > CreatureLagM) { why = $"the creature is {off:F0} m from where the hit says it was"; return Refuse(from, target, kind, why); }
            }
            if (!_hist.TryGetValue(target, out var tl) || tl.Count == 0) { NoHistory++; Allowed++; return true; }

            // the attacker was there: the start of the shot or the blade near their own positions of the last second
            if (kind != KCreature && _hist.TryGetValue(from, out var fl) && fl.Count > 0)
            {
                double best = double.MaxValue;
                foreach (var (p, step) in Window(fl, now - 1.0 - SlackS, now)) best = System.Math.Min(best, (p - origin).magnitude - step);
                if (best > OriginM) { why = $"came from {best + OriginM:F0} m away from the attacker"; return Refuse(from, target, kind, why); }
            }

            double nearest = double.MaxValue, closest = double.MaxValue;
            var d = new Vector3d(dir.x, dir.y, dir.z);
            double dl = d.magnitude;
            foreach (var (p, step) in Window(tl, now - back, now))
            {
                double gap;
                if (kind == KShot)
                {
                    if (dl < 1e-6) { why = "no direction"; return Refuse(from, target, kind, why); }
                    gap = RayToSegment(origin, d * (1.0 / dl), p, out double along);
                    if (along > ShotMaxM) gap = double.MaxValue;
                }
                else gap = PointToSegment(origin, p);
                gap -= step;
                if (gap < closest) closest = gap;
                double far = (p - origin).magnitude;
                if (far < nearest) nearest = far;
            }
            double reach = kind == KShot ? BodyR + MarginM : kind == KMelee ? MeleeM + MarginM : CreatureM + MarginM;
            if (closest <= reach) { Allowed++; return true; }
            why = kind == KShot ? $"the shot passed {closest:F1} m from the target (target {nearest:F0} m away)"
                : $"the target was {closest:F1} m from the {(kind == KMelee ? "blade" : "creature")}";
            return Refuse(from, target, kind, why);
        }

        private static bool Refuse(int from, int target, byte kind, string why)
        {
            Refused++;
            LastRefusal = $"player {from} → player {target} ({(kind == KShot ? "shot" : kind == KMelee ? "melee" : "creature")}): {why}";
            if (_logged++ < 20) Entities.AnomalyLine("hit refused: " + LastRefusal);
            return false;
        }

        /// The samples of [t0, t1], the last one before t0 too (the position at t0), each with half the distance to
        /// its neighbours (motion between samples counts in the target's favour).
        private static IEnumerable<(Vector3d p, double step)> Window(List<Sample> l, double t0, double t1)
        {
            int first = 0;
            while (first < l.Count - 1 && l[first + 1].T < t0) first++;
            for (int i = first; i < l.Count && l[i].T <= t1 + 0.05; i++)
            {
                double s = 0;
                if (i > 0) s = System.Math.Max(s, (l[i].P - l[i - 1].P).magnitude);
                if (i < l.Count - 1) s = System.Math.Max(s, (l[i + 1].P - l[i].P).magnitude);
                yield return (l[i].P, System.Math.Min(s * 0.5, 10.0));
            }
        }

        /// Distance from a ray (unit dir) to the body segment around a player's position, and how far along the ray.
        private static double RayToSegment(Vector3d o, Vector3d u, Vector3d p, out double along)
        {
            // the body is vertical: try the ray's closest approach to the segment by sampling it (cheap, exact enough)
            double best = double.MaxValue; along = 0;
            for (int i = 0; i <= 8; i++)
            {
                var q = new Vector3d(p.x, p.y - BodyHalfM + 2 * BodyHalfM * i / 8.0, p.z);
                var w = q - o;
                double s = System.Math.Max(0, w.x * u.x + w.y * u.y + w.z * u.z);
                var c = o + u * s;
                double g = (q - c).magnitude;
                if (g < best) { best = g; along = s; }
            }
            return best;
        }

        private static double PointToSegment(Vector3d o, Vector3d p)
        {
            double y = System.Math.Max(p.y - BodyHalfM, System.Math.Min(p.y + BodyHalfM, o.y));
            return (new Vector3d(p.x, y, p.z) - o).magnitude;
        }

        public static string Stats() => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"checked\":" + Checked + ",\"allowed\":" + Allowed
                                        + ",\"refused\":" + Refused + ",\"noHistory\":" + NoHistory + ",\"last\":" + Json.Str(LastRefusal) + "}";
    }
}
