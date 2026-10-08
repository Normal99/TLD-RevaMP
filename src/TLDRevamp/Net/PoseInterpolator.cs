using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Shows a remote object smoothly from unreliable 20 Hz states at high, uneven ping (target: 150–200 ms, loss, jitter).
    ///  - Latency: base = the smallest (arrival − send time) seen recently (the least delayed packet); jitter = 95th
    ///    percentile of how much later than that packets arrive. The display runs `delay` behind the sender:
    ///    delay = send interval + jitter + margin — rises at once when the network gets worse, falls slowly.
    ///  - Display clock: advances with real time and is steered towards (now − base − delay) by changing its rate within
    ///    ±5 % (low-passed error), so changes of the estimate never make the object jump or visibly change speed.
    ///  - Between two states: cubic Hermite with the sent velocities, slerp for rotation.
    ///  - Late/lost packets: extrapolate with the last velocity for at most MaxExtrapolate, then hold. A moving object
    ///    that may coast (MaxCoast: cars) goes on longer — a car does not stop dead in a Wi-Fi stall and then lunge
    ///    after it (stallcar, 2026-10-08: 1 s stall at 70 km/h → held 47 frames, then 125 m/s to catch up).
    ///  - A stall is not jitter: latencies more than StallCutoff over the least delayed one are left out of the
    ///    jitter estimate — one stall raised the delay to ~1 s, and at 20 ms/s it took ~40 s to come back down.
    ///  - When new data changes the curve at the time being shown (e.g. it replaces an extrapolation), the difference
    ///    becomes a visual offset that is blended out (CorrectionTau) — never a snap. Except a teleport: a state farther
    ///    from the previous one than anything can travel in between (SnapBaseM + SnapSpeed × gap) starts the curve anew
    ///    there. Blended, a 200 km jump made the copy glide through the world at up to ~1,000 km/s (farworld run 9:
    ///    a body-moved copy swept 30–130 km per physics step and killed the player it passed).
    /// Positions are global doubles; each machine converts through its own floating origin when displaying.
    public sealed class PoseInterpolator
    {
        public struct Sample { public double T; public Vector3d Pos; public Quaternion Rot; public Vector3 Vel; }

        public float SendInterval = 1f / Protocol.StateHz;
        public float Margin = 0.015f, MaxExtrapolate = 0.25f, CorrectionTau = 0.12f;
        public float MaxCoast = 0.25f, CoastMinSpeed = 2f, StallCutoff = 0.5f;
        public static long StallSamples;
        /// teleport detection, and the most a sent velocity may be (extrapolation, Hermite tangents): 150 m/s = 540 km/h
        public float SnapBaseM = 100f, SnapSpeed = 150f;
        public static long Snaps, VelClamped;
        /// set when the curve restarted at a teleport; the copy mover takes it (TakeSnap) and moves the body there at once
        private bool _snapped;
        public bool TakeSnap() { bool v = _snapped; _snapped = false; return v; }

        private readonly List<Sample> _buf = new List<Sample>(64);
        private readonly List<double> _lat = new List<double>(64);
        // the spacing states actually arrive at: the server thins far cars' states (10 / 4 Hz, MpServer.SendCarState) —
        // the display has to run that far behind, or it extrapolates most of the time and corrects at every state
        private readonly float[] _gaps = new float[8];
        private int _gapN;
        public float Interval { get; private set; } = 1f / Protocol.StateHz;
        private readonly List<double> _tmp = new List<double>(64);
        private double _base = double.MaxValue;
        private float _delay = -1f;
        private double _renderT;
        private bool _hasClock;
        private double _smoothErr;
        public float RateRange = 0.05f;
        private Vector3d _corr;
        private Quaternion _corrRot = Quaternion.identity;

        public bool Extrapolating { get; private set; }
        /// diagnostics (jump log): how far the shown time is past the newest state (ms; > MaxExtrapolate = holding still),
        /// and the correction offset still being blended out (cm)
        public float AheadMs { get; private set; }
        public float CorrCm => (float)Math.Sqrt(_corr.x * _corr.x + _corr.y * _corr.y + _corr.z * _corr.z) * 100f;
        public float DelayMs => _delay * 1000f;
        public float JitterMs { get; private set; }
        public double RenderTime => _renderT;
        public bool Ready => _buf.Count > 0;
        /// Diagnostics (`mp interp <net>`): the recent latency deviations (ms over the least delayed), send gaps (ms), delay.
        public string Dump()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var lat = new System.Text.StringBuilder(); double min = double.MaxValue; foreach (var l in _lat) if (l < min) min = l;
            foreach (var l in _lat) { if (lat.Length > 0) lat.Append(','); lat.Append(((l - min) * 1000.0).ToString("F0", ic)); }
            var gaps = new System.Text.StringBuilder();
            for (int i = 0; i < Math.Min(_gapN, _gaps.Length); i++) { if (gaps.Length > 0) gaps.Append(','); gaps.Append((_gaps[i] * 1000f).ToString("F0", ic)); }
            return "{\"delayMs\":" + DelayMs.ToString("F0", ic) + ",\"jitterMs\":" + JitterMs.ToString("F0", ic) + ",\"intervalMs\":" + (Interval * 1000f).ToString("F0", ic) +
                   ",\"latDevMs\":[" + lat + "],\"gapsMs\":[" + gaps + "]}";
        }

        public Vector3d LastPos => _buf.Count > 0 ? _buf[_buf.Count - 1].Pos : default;   // diagnostics (mp entities)

        public void Add(Sample s, double arrival)
        {
            if (_buf.Count > 0 && s.T <= _buf[_buf.Count - 1].T) return; // duplicate / out of order
            if (s.Vel.sqrMagnitude > SnapSpeed * SnapSpeed) { s.Vel = s.Vel.normalized * SnapSpeed; VelClamped++; }
            bool teleport = false;
            if (_buf.Count > 0)
            {
                var last = _buf[_buf.Count - 1];
                double reach = SnapBaseM + SnapSpeed * (s.T - last.T);
                if ((s.Pos - last.Pos).sqrMagnitude > reach * reach) teleport = true;
            }
            if (teleport)
            {
                _buf.Clear();
                _corr = default; _corrRot = Quaternion.identity;
                _snapped = true; Snaps++;
            }
            if (_buf.Count > 0)
            {
                // gaps of a moving stream only: a car at rest sends nothing, its first state after is no "interval"
                float gap = (float)(s.T - _buf[_buf.Count - 1].T);
                if (gap <= 0.5f) { _gaps[_gapN++ % _gaps.Length] = gap; }
                float mx = SendInterval;
                for (int i = 0; i < Math.Min(_gapN, _gaps.Length); i++) if (_gaps[i] > mx) mx = _gaps[i];
                Interval = mx;
            }
            Vector3d before = default; Quaternion beforeRot = default;
            bool absorb = _hasClock && _buf.Count > 0;
            if (absorb) Raw(_renderT, out before, out beforeRot, out _);
            _buf.Add(s);
            if (_buf.Count > 60) _buf.RemoveAt(0);
            if (absorb)
            {
                Raw(_renderT, out var after, out var afterRot, out _);
                _corr = _corr + (before - after);
                _corrRot = beforeRot * Quaternion.Inverse(afterRot) * _corrRot;
            }

            _lat.Add(arrival - s.T);
            if (_lat.Count > 60) _lat.RemoveAt(0);
            double min = double.MaxValue;
            foreach (var l in _lat) if (l < min) min = l;
            _base = min;
            _tmp.Clear();
            foreach (var l in _lat) { if (l - min <= StallCutoff) _tmp.Add(l - min); }
            if (arrival - s.T - min > StallCutoff) StallSamples++;
            _tmp.Sort();
            JitterMs = _tmp.Count > 0 ? (float)(_tmp[Math.Min(_tmp.Count - 1, (int)(_tmp.Count * 0.95))] * 1000.0) : 0f;
            float target = Interval + JitterMs / 1000f + Margin;
            if (_delay < 0 || target > _delay) _delay = target;
            else _delay = Mathf.MoveTowards(_delay, target, 0.02f * Interval);
        }

        /// The display continues from `shown` (a physical copy handing back to the display): the difference to the curve
        /// at the time being shown becomes the visual offset that blends out.
        public void Absorb(Vector3d shown, Quaternion shownRot)
        {
            if (!_hasClock || _buf.Count == 0) return;
            Raw(_renderT, out var raw, out var rawRot, out _);
            _corr = shown - raw;
            _corrRot = shownRot * Quaternion.Inverse(rawRot);
        }

        /// Pose to show at local time `now` (same clock as `arrival` in Add), `dt` since the previous call.
        public void Evaluate(double now, float dt, out Vector3d pos, out Quaternion rot)
        {
            double target = now - _base - _delay;
            if (!_hasClock || Math.Abs(target - _renderT) > 1.0) { _renderT = target; _smoothErr = 0; _hasClock = true; }
            else
            {
                // gentle steering: the error is low-passed (single packets move the target) and the rate stays within
                // ±RateRange, so the shown speed never visibly changes because of clock corrections
                double lp = 1.0 - Math.Exp(-dt / 0.5);
                _smoothErr += ((target - _renderT) - _smoothErr) * lp;
                double rate = Math.Max(1.0 - RateRange, Math.Min(1.0 + RateRange, 1.0 + _smoothErr * 0.5));
                _renderT += dt * rate;
            }
            Raw(_renderT, out var raw, out var rawRot, out bool ex);
            Extrapolating = ex;
            AheadMs = (float)((_renderT - _buf[_buf.Count - 1].T) * 1000.0);
            double k = Math.Exp(-dt / CorrectionTau);
            _corr = _corr * k;
            _corrRot = Quaternion.Slerp(Quaternion.identity, _corrRot, (float)k);
            pos = raw + _corr;
            rot = _corrRot * rawRot;
        }

        private void Raw(double t, out Vector3d pos, out Quaternion rot, out bool extrapolating)
        {
            extrapolating = false;
            if (t <= _buf[0].T) { pos = _buf[0].Pos; rot = _buf[0].Rot; return; }
            var n = _buf[_buf.Count - 1];
            if (t >= n.T)
            {
                double ex = Math.Min(t - n.T, MaxCoast > MaxExtrapolate && n.Vel.sqrMagnitude > CoastMinSpeed * CoastMinSpeed ? MaxCoast : MaxExtrapolate);
                pos = n.Pos + n.Vel * (float)ex;
                rot = n.Rot;
                extrapolating = t > n.T + 1e-4;
                return;
            }
            int i = _buf.Count - 2;
            while (i > 0 && _buf[i].T > t) i--;
            var a = _buf[i]; var b = _buf[i + 1];
            double h = b.T - a.T, u = (t - a.T) / h;
            double u2 = u * u, u3 = u2 * u;
            double h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
            pos = a.Pos * h00 + b.Pos * h01 + a.Vel * (float)(h10 * h) + b.Vel * (float)(h11 * h);
            rot = Quaternion.Slerp(a.Rot, b.Rot, (float)u);
        }
    }
}
