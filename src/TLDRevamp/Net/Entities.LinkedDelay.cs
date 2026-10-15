using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Copies joined to each other are shown at the same moment of their owner's time: a trailer and the car towing it,
    /// and the cars carried on either (a bed's mountStuff.storedcars). Each copy's interpolator picks its own display
    /// delay from its own packets' jitter, so two copies moving together could be shown ~0.1 s apart: at 30 km/h the
    /// laptop drew the car trailer 0.85 m further behind the tow car than the host had it, with the car on its bed in
    /// the right place, so on the trailer that car looked a metre off (cartrailer.py C3, 2026-10-15: max 1.1–2.5 m,
    /// median under 0.06 m; the logging showed the trailer late, not the car).
    /// Now, a few times a second, every group of joined copies takes the delay of its most delayed member
    /// (PoseInterpolator.Hold): the others are shown that much further back. The display clock steers there within its
    /// ±5 % rate, so nothing jumps.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Net.Entities.LinkedDelay
    public static partial class Entities
    {
        public static bool LinkedDelay = true;   // A/B: false = every copy on its own delay (before v0.67.1)
        public static float LinkedDelayEveryS = 0.2f, LinkedHoldMaxS = 0.5f;
        public static long LinkedGroups, LinkedHeld;
        public static float LinkedLastHoldMs;
        private static float _linkedAt;
        private static readonly List<Ent> _linkedHeld = new List<Ent>();
        private static readonly List<Ent> _group = new List<Ent>();
        private static readonly HashSet<Transform> _groupRoots = new HashSet<Transform>();

        private static void LinkedDelayTick()
        {
            if (Time.time - _linkedAt < LinkedDelayEveryS) return;
            _linkedAt = Time.time;
            foreach (var e in _linkedHeld) if (e.Ip != null) e.Ip.Hold = 0;
            _linkedHeld.Clear();
            if (!LinkedDelay) return;
            double now = Time.realtimeSinceStartupAsDouble;
            var done = new HashSet<Transform>();
            foreach (var g in Fixes.TrailerGrip.All)
            {
                var at = g != null && g.tosaveitem != null ? g.tosaveitem.attachable : null;
                if (at == null || !at.attached || at.point == null) continue;
                var tow = at.point.root;
                if (done.Contains(tow)) continue;
                done.Add(tow);
                // the whole chain on this tow car: every trailer hitched to it (and to them), and what they all carry
                _groupRoots.Clear(); _groupRoots.Add(tow);
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (var h in Fixes.TrailerGrip.All)
                    {
                        var ha = h != null && h.tosaveitem != null ? h.tosaveitem.attachable : null;
                        if (ha == null || !ha.attached || ha.point == null || !_groupRoots.Contains(ha.point.root)) continue;
                        if (_groupRoots.Add(h.tosaveitem.transform.root)) grew = true;
                    }
                }
                var carriers = new List<Transform>(_groupRoots);
                foreach (var c in carriers)
                    foreach (var m in c.GetComponentsInChildren<mountStuff>())
                        foreach (var sc in m.storedcars) if (sc != null) _groupRoots.Add(sc.transform.root);
                Align(now);
            }
        }

        /// The group in _groupRoots: its copies shown at the oldest moment any of them would show.
        private static void Align(double now)
        {
            _group.Clear();
            foreach (var r in _groupRoots)
            {
                var e = ProxyOf(r);
                if (e != null && e.Ip != null && e.Ip.Ready) _group.Add(e);
            }
            if (_group.Count < 2) return;
            double oldest = double.MaxValue;
            foreach (var e in _group) oldest = System.Math.Min(oldest, e.Ip.OwnTarget(now));
            foreach (var e in _group)
            {
                double hold = System.Math.Min(e.Ip.OwnTarget(now) - oldest, LinkedHoldMaxS);
                if (hold <= 0) continue;
                e.Ip.Hold = hold;
                _linkedHeld.Add(e);
                LinkedHeld++;
                LinkedLastHoldMs = (float)(hold * 1000.0);
            }
            LinkedGroups++;
        }
    }
}
