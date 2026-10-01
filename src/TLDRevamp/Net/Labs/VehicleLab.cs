using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Remote-car display test in one game (bridge `vehlab start [offset]`, `vehlab status`, `vehlab stop`).
    /// The car the player sits in (driven by DriveLab) is the owner. Its pose goes out 20×/s through a real Steam socket
    /// pair (so `net lag` applies: latency, loss, jitter), and a car-sized "ghost" beside it is moved only by what
    /// arrives, through PoseInterpolator. The same stream is also scored as the official MP shows cars: snap to the
    /// latest update.
    /// Scores: accuracy = distance between the ghost and where the real car was at the time the ghost claims to show;
    /// smoothness = per-frame velocity change of what's shown (a visible hitch = > 5 m/s change in one frame), compared
    /// with the real car's own.
    public static class VehicleLab
    {
        private static SteamTransport _t;
        private static HSteamNetConnection _a;
        private static Transform _car;
        private static Rigidbody _rb;
        private static GameObject _ghost;
        private static PoseInterpolator _ip;
        private static readonly NetWriter W = new NetWriter();
        private static float _acc, _offset;
        public static float RateRange = 0.05f;
        private static bool _running;

        private struct H { public double T; public Vector3d Pos; }
        private static readonly List<H> Hist = new List<H>(1024);

        /// Per-frame deviation from smooth motion: |p(t) − 2·p(t−1) + p(t−2)|, time-normalised to the average frame
        /// (cm). A visible jump = > 20 cm in one frame (snapping at 90 km/h and 20 Hz jumps ~125 cm).
        private sealed class Smooth
        {
            private Vector3d _p1, _p2; private float _dt1; private int _n;
            public readonly List<float> Dev = new List<float>(8192);
            public int Jumps;
            public void Add(Vector3d p, float dt)
            {
                if (dt <= 0f) return;
                if (_n >= 2)
                {
                    // expected position if the last step's velocity continued for this frame's dt
                    var v = (Vector3)(_p1 - _p2) / Mathf.Max(1e-4f, _dt1);
                    float dev = ((Vector3)(p - _p1) - v * dt).magnitude * 100f;
                    Dev.Add(dev); if (dev > 20f) Jumps++;
                }
                _p2 = _p1; _p1 = p; _dt1 = dt; _n++;
            }
            public string Json(string name)
            {
                if (Dev.Count == 0) return "\"" + name + "\":null";
                var s = new List<float>(Dev); s.Sort();
                return "\"" + name + "\":{\"frames\":" + s.Count + ",\"jumps\":" + Jumps + ",\"devP50cm\":" + s[s.Count / 2].ToString("F2") +
                       ",\"devP99cm\":" + s[(int)(s.Count * 0.99)].ToString("F2") + ",\"devMaxCm\":" + s[s.Count - 1].ToString("F2") + "}";
            }
        }

        private static Smooth _truth, _interp, _snap;
        private static readonly List<float> Err = new List<float>(8192);
        private static int _frames, _extrapFrames, _received;
        private static Vector3d _snapPos;
        private static bool _hasSnap;
        private static float _delaySum;

        public static string Start(float offset)
        {
            Stop();
            var root = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.transform.root : null;
            if (root == null || root.GetComponentInChildren<carscript>() == null) return "{\"error\":\"sit in a car first\"}";
            _car = root; _rb = root.GetComponent<Rigidbody>(); _offset = offset;
            _t = SteamTransport.Pair(true);
            _a = _t.Connections[0];
            _t.Message = OnMessage;
            _ip = new PoseInterpolator { RateRange = RateRange };
            // ghost: a box the size of the car's visible body
            var b = new Bounds(root.position, Vector3.zero);
            foreach (var r in root.GetComponentsInChildren<Renderer>()) if (r.enabled) b.Encapsulate(r.bounds);
            _ghost = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _ghost.name = "TLDRevampVehicleGhost";
            UnityEngine.Object.Destroy(_ghost.GetComponent<Collider>());
            var ls = root.InverseTransformVector(b.size);
            _ghost.transform.localScale = new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y) * 0.6f, Mathf.Abs(ls.z));
            _truth = new Smooth(); _interp = new Smooth(); _snap = new Smooth();
            Err.Clear(); Hist.Clear();
            _frames = _extrapFrames = _received = 0; _hasSnap = false; _delaySum = 0; _acc = 0;
            _running = true;
            return "{\"started\":true,\"lag\":" + Json.Str(NetLab.LagInfo) + "}";
        }

        private static double Now => Time.realtimeSinceStartupAsDouble;

        private static void OnMessage(HSteamNetConnection c, NetReader r)
        {
            var s = new PoseInterpolator.Sample
            {
                T = r.F64(),
                Pos = new Vector3d(r.F64(), r.F64(), r.F64()),
                Rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32()),
                Vel = new Vector3(r.F32(), r.F32(), r.F32())
            };
            if (r.Bad) return;
            _received++;
            _ip.Add(s, Now);
            _snapPos = s.Pos; _hasSnap = true;
        }

        /// Runner, once per frame.
        public static void Tick()
        {
            if (!_running) return;
            if (_car == null) { Stop(); return; }
            double now = Now;
            float dt = Time.unscaledDeltaTime;
            var g = mainscript.GlobalFromUnityPos(_car.position);
            Hist.Add(new H { T = now, Pos = g });
            if (Hist.Count > 1000) Hist.RemoveRange(0, 200);
            _truth.Add(g, dt);

            _acc += dt;
            if (_acc >= 1f / Protocol.StateHz)
            {
                _acc -= 1f / Protocol.StateHz;
                if (_acc > 1f / Protocol.StateHz) _acc = 0;
                var q = _car.rotation;
                var v = _rb != null ? _rb.velocity : Vector3.zero;
                // the transform only changes on physics steps: stamp the sample with the time of the last step, not the frame
                double stamp = now - (Time.timeAsDouble - Time.fixedTimeAsDouble);
                W.Reset(); W.F64(stamp); W.F64(g.x); W.F64(g.y); W.F64(g.z); W.F32(q.x); W.F32(q.y); W.F32(q.z); W.F32(q.w); W.F32(v.x); W.F32(v.y); W.F32(v.z);
                _t.Send(_a, W, SteamTransport.SendUnreliable);
            }
            _t.Poll();
            if (!_ip.Ready) return;

            _ip.Evaluate(now, dt, out var pos, out var rot);
            _frames++;
            if (_ip.Extrapolating) _extrapFrames++;
            _delaySum += _ip.DelayMs;
            _interp.Add(pos, dt);
            if (_hasSnap) _snap.Add(_snapPos, dt);
            if (TruthAt(_ip.RenderTime, out var truth)) Err.Add((float)(pos - truth).magnitude);
            _ghost.transform.SetPositionAndRotation(mainscript.UnityPosFromGlobal(pos) + rot * Vector3.right * _offset, rot);
        }

        private static bool TruthAt(double t, out Vector3d p)
        {
            p = default;
            for (int i = Hist.Count - 1; i > 0; i--)
                if (Hist[i - 1].T <= t && Hist[i].T >= t)
                {
                    double u = (t - Hist[i - 1].T) / Math.Max(1e-9, Hist[i].T - Hist[i - 1].T);
                    p = Hist[i - 1].Pos + (Hist[i].Pos - Hist[i - 1].Pos) * u;
                    return true;
                }
            return false;
        }

        public static string Status()
        {
            if (_truth == null) return "{\"running\":false}";
            string err = "null";
            if (Err.Count > 0)
            {
                var s = new List<float>(Err); s.Sort();
                double mean = 0; foreach (var e in s) mean += e; mean /= s.Count;
                err = "{\"mean\":" + mean.ToString("F3") + ",\"p99\":" + s[(int)(s.Count * 0.99)].ToString("F3") + ",\"max\":" + s[s.Count - 1].ToString("F3") + "}";
            }
            return "{\"running\":" + (_running ? "true" : "false") + ",\"lag\":" + Json.Str(NetLab.LagInfo) + ",\"received\":" + _received +
                   ",\"frames\":" + _frames + ",\"extrapolatedFrames\":" + _extrapFrames +
                   ",\"delayMsAvg\":" + (_frames > 0 ? _delaySum / _frames : 0).ToString("F1") + ",\"jitterMs\":" + (_ip != null ? _ip.JitterMs : 0).ToString("F1") +
                   ",\"errorM\":" + err + "," + _truth.Json("truth") + "," + _interp.Json("interp") + "," + _snap.Json("snap") + "}";
        }

        public static string Stop()
        {
            string s = Status();
            _running = false;
            if (_ghost != null) UnityEngine.Object.Destroy(_ghost);
            _ghost = null;
            _t?.Dispose(); _t = null;
            return s;
        }
    }
}
