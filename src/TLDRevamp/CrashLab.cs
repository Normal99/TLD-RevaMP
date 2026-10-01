using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp
{
    /// Collision test recorder (bridge `crash rec start|stop`, `crash launch <m/s> [gx gz]`). Records every car near the
    /// player each frame — its own car and other players' cars as this machine shows them — and reports per car what a
    /// crash did to it: speed before the first car-car contact and after, peak speed and spin after it, how high it went,
    /// whether it ended on its roof, the game's crash-damage input (carscript.DamageStuff's Δspeed) and parts lost.
    /// Velocities come from positions (0.1 s windows): a display copy's rigidbody reads 0, its motion is what counts.
    /// The same recorder runs in single player (both cars simulated here: the game's own crash — the reference) and on
    /// both machines of a session (each car's truth is on its owner's machine).
    [HarmonyPatch]
    public static class CrashLab
    {
        private const float Window = 0.1f, MaxSeconds = 20f;

        private sealed class Sample { public float T; public Vector3d P; public Quaternion Q; }

        private sealed class Tracked
        {
            public Transform Root; public carscript Car; public string Label; public uint Net; public bool Proxy;
            public readonly List<Sample> S = new List<Sample>(2048);
            public float ContactT = -1f, ContactRelVel, ContactImpulse; public string ContactWith = "";
            public int PartsAtStart;
            public float DamageMaxV, DamageMaxVAfterContact; public int DamageCalls;
        }

        private static readonly List<Tracked> Cars = new List<Tracked>();
        private static bool _rec;
        private static float _t0;
        private static long _cs0, _cr0, _ca0;
        private static Recorder _runner;

        private sealed class Recorder : MonoBehaviour
        {
            private void LateUpdate() { if (_rec) Tick(); }
        }

        /// On each tracked root while recording: the first contact with another tracked car.
        private sealed class Probe : MonoBehaviour
        {
            [System.NonSerialized] public Tracked Me;
            private void OnCollisionEnter(Collision c)
            {
                if (!_rec || Me == null) return;
                var otherRoot = c.transform.root;
                foreach (var o in Cars)
                    if (o != Me && o.Root == otherRoot)
                    {
                        float t = Time.time - _t0;
                        Mark(Me, o, t, c.relativeVelocity.magnitude, c.impulse.magnitude);
                        Mark(o, Me, t, c.relativeVelocity.magnitude, c.impulse.magnitude);   // a kinematic copy gets no event of its own
                        return;
                    }
            }
            private static void Mark(Tracked a, Tracked b, float t, float rel, float imp)
            {
                if (a.ContactT >= 0f) return;
                a.ContactT = t; a.ContactRelVel = rel; a.ContactImpulse = imp; a.ContactWith = b.Label;
            }
        }

        [HarmonyPatch(typeof(carscript), nameof(carscript.DamageStuff))]
        [HarmonyPostfix]
        private static void DamageSeen(carscript __instance, float v)
        {
            if (!_rec) return;
            foreach (var c in Cars)
                if (c.Car == __instance)
                {
                    c.DamageCalls++;
                    if (v > c.DamageMaxV) c.DamageMaxV = v;
                    if (c.ContactT >= 0f && v > c.DamageMaxVAfterContact) c.DamageMaxVAfterContact = v;
                    return;
                }
        }

        public static string Start(float radius)
        {
            Stop();
            if (_runner == null) { var go = new GameObject("TLDRevamp.CrashLab"); Object.DontDestroyOnLoad(go); _runner = go.AddComponent<Recorder>(); }
            var me = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.transform.position : Vector3.zero;
            foreach (var car in Object.FindObjectsOfType<carscript>())
            {
                var root = car.transform.root;
                if ((root.position - me).magnitude > radius) continue;
                var t = new Tracked { Root = root, Car = car, PartsAtStart = AttachedParts(root) };
                t.Label = root.name + "#" + root.GetInstanceID();
                if (Net.Entities.NetOf(root, out uint net, out bool proxy)) { t.Net = net; t.Proxy = proxy; t.Label = "net" + net + (proxy ? " (copy)" : " (own)"); }
                var probe = root.gameObject.AddComponent<Probe>(); probe.Me = t;
                Cars.Add(t);
            }
            _t0 = Time.time; _rec = true;
            _cs0 = Net.Entities.ContactSent; _cr0 = Net.Entities.ContactRelayed; _ca0 = Net.Entities.ContactApplied;
            Net.Entities.ContactImpulseMaxSent = Net.Entities.ContactImpulseMaxApplied = 0f;
            var names = new List<string>(); foreach (var c in Cars) names.Add(Json.Str(c.Label));
            return "{\"recording\":[" + string.Join(",", names) + "]}";
        }

        private static int AttachedParts(Transform root)
        {
            int n = 0;
            foreach (var a in root.GetComponentsInChildren<attachablescript>(true)) if (a.attached) n++;
            return n;
        }

        private static void Tick()
        {
            float t = Time.time - _t0;
            if (t > MaxSeconds) { _rec = false; return; }
            foreach (var c in Cars)
                if (c.Root != null) c.S.Add(new Sample { T = t, P = mainscript.GlobalFromUnityPos(c.Root.position), Q = c.Root.rotation });
        }

        /// velocity (global, m/s) and spin (deg/s) over the window ending at sample i
        private static bool At(Tracked c, int i, out Vector3 v, out float w)
        {
            v = Vector3.zero; w = 0f;
            int j = i;
            while (j > 0 && c.S[i].T - c.S[j].T < Window) j--;
            float dt = c.S[i].T - c.S[j].T;
            if (dt < Window * 0.5f) return false;
            v = (Vector3)(c.S[i].P - c.S[j].P) / dt;
            w = Quaternion.Angle(c.S[j].Q, c.S[i].Q) / dt;
            return true;
        }

        private static int IndexAt(Tracked c, float t)
        {
            for (int i = 0; i < c.S.Count; i++) if (c.S[i].T >= t) return i;
            return c.S.Count - 1;
        }

        /// forcedContact: the crash moment for cars that saw no earlier contact of their own on this machine (a crash simulated
        /// on another machine shows here as two display copies — kinematic copies raise no collision events).
        public static string Stop(float forcedContact = -1f)
        {
            bool was = _rec || Cars.Count > 0;
            _rec = false;
            if (!was) return "{\"cars\":[]}";
            var ic = CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var c in Cars)
            {
                if (c.Root != null) { var p = c.Root.GetComponent<Probe>(); if (p != null) Object.Destroy(p); }
                if (c.S.Count < 5) { rows.Add("{\"car\":" + Json.Str(c.Label) + ",\"error\":\"no samples\"}"); continue; }
                float tc = c.ContactT;
                // no contact event seen (e.g. a copy's contact on a machine where nothing collided): report the frame
                // the copy's motion changed most, so a crash seen only through the network still has a "moment"
                Vector3 vBefore = Vector3.zero, vAfter = Vector3.zero; float peakV = 0f, peakW = 0f, riseM = 0f, yAt = 0f, maxDv = 0f;
                // a later contact of its own doesn't count: with copies kinematic (v0.57.77) the laptop's first contact
                // came at 3.69 s — its car back from the crash lease touching the other copy — and every record was timed
                // from after the crash
                if (forcedContact >= 0f && (tc < 0f || tc > forcedContact)) tc = forcedContact;
                if (tc < 0f)
                {
                    Vector3 prev = Vector3.zero; bool has = false;
                    for (int i = 0; i < c.S.Count; i++)
                    {
                        if (!At(c, i, out var v, out _)) continue;
                        if (has && (v - prev).magnitude > maxDv) { maxDv = (v - prev).magnitude; tc = c.S[i].T; }
                        prev = v; has = true;
                    }
                }
                int ib = IndexAt(c, tc - 0.15f), ic0 = IndexAt(c, tc), ia = IndexAt(c, tc + 0.6f);
                At(c, ib, out vBefore, out _);
                At(c, ia, out vAfter, out _);
                yAt = (float)c.S[ic0].P.y;
                for (int i = ic0; i < c.S.Count && c.S[i].T < tc + 4f; i++)
                {
                    if (At(c, i, out var v, out var w)) { if (v.magnitude > peakV) peakV = v.magnitude; if (w > peakW) peakW = w; }
                    float rise = (float)c.S[i].P.y - yAt; if (rise > riseM) riseM = rise;
                }
                var last = c.S[c.S.Count - 1];
                float upY = (last.Q * Vector3.up).y;
                var endShift = (Vector3)(last.P - c.S[ic0].P);
                int parts = c.Root != null ? AttachedParts(c.Root) : -1;
                rows.Add("{\"car\":" + Json.Str(c.Label) + ",\"net\":" + c.Net + ",\"proxy\":" + (c.Proxy ? "true" : "false") +
                         ",\"contactT\":" + tc.ToString("F2", ic) + ",\"contactSeen\":" + (c.ContactT >= 0f ? "true" : "false") + ",\"contactWith\":" + Json.Str(c.ContactWith) +
                         ",\"contactRelVel\":" + c.ContactRelVel.ToString("F2", ic) + ",\"contactImpulse\":" + c.ContactImpulse.ToString("F0", ic) +
                         ",\"vBefore\":" + V(vBefore) + ",\"vAfter\":" + V(vAfter) + ",\"speedBefore\":" + H(vBefore).ToString("F2", ic) + ",\"speedAfter\":" + H(vAfter).ToString("F2", ic) +
                         ",\"peakSpeedAfter\":" + peakV.ToString("F2", ic) + ",\"peakSpinDeg\":" + peakW.ToString("F0", ic) + ",\"riseM\":" + riseM.ToString("F2", ic) +
                         ",\"endUpY\":" + upY.ToString("F2", ic) + ",\"movedAfterM\":" + new Vector2(endShift.x, endShift.z).magnitude.ToString("F2", ic) +
                         ",\"damageMaxV\":" + c.DamageMaxV.ToString("F1", ic) + ",\"damageAfterContact\":" + c.DamageMaxVAfterContact.ToString("F1", ic) + ",\"damageCalls\":" + c.DamageCalls +
                         ",\"partsLost\":" + (parts >= 0 ? c.PartsAtStart - parts : -1) + ",\"samples\":" + c.S.Count +
                         ",\"endPos\":[" + last.P.x.ToString("F2", ic) + "," + last.P.y.ToString("F2", ic) + "," + last.P.z.ToString("F2", ic) + "]}");
            }
            Cars.Clear();
            return "{\"contactSent\":" + (Net.Entities.ContactSent - _cs0) + ",\"contactRelayed\":" + (Net.Entities.ContactRelayed - _cr0) + ",\"contactApplied\":" + (Net.Entities.ContactApplied - _ca0) +
                   ",\"impulseMaxSent\":" + Net.Entities.ContactImpulseMaxSent.ToString("F0", ic) + ",\"impulseMaxApplied\":" + Net.Entities.ContactImpulseMaxApplied.ToString("F0", ic) +
                   ",\"cars\":[" + string.Join(",", rows) + "]}";
        }

        /// Cars within `radius` of the player (a crash stretch must be free of them): name, distance.
        public static string CarsNear(float radius)
        {
            var me = mainscript.s.player.transform.position;
            var rows = new List<string>();
            var seen = new HashSet<Transform>();
            foreach (var c in Object.FindObjectsOfType<carscript>())
            {
                var root = c.transform.root;
                if (!seen.Add(root)) continue;
                float d = (root.position - me).magnitude;
                if (d <= radius) rows.Add("{\"name\":" + Json.Str(root.name) + ",\"dist\":" + d.ToString("F1", CultureInfo.InvariantCulture) + "}");
            }
            return "{\"cars\":[" + string.Join(",", rows) + "]}";
        }

        private static float H(Vector3 v) => new Vector2(v.x, v.z).magnitude;
        private static string V(Vector3 v) => "[" + v.x.ToString("F2", CultureInfo.InvariantCulture) + "," + v.y.ToString("F2", CultureInfo.InvariantCulture) + "," + v.z.ToString("F2", CultureInfo.InvariantCulture) + "]";

        /// Set a car rolling at `speed` m/s along its nose (the player's car, or the car nearest a global point): every body
        /// of it, parking brake off, and the game's crash detector told the new speed is normal (it compares this frame's
        /// forward speed with the last one — a launch would read as a crash otherwise).
        public static string Launch(float speed, Vector3d? near, bool hold = false)
        {
            carscript car = null;
            if (near.HasValue)
            {
                var at = mainscript.UnityPosFromGlobal(near.Value); float best = float.MaxValue;
                foreach (var c in Object.FindObjectsOfType<carscript>()) { float d = (c.transform.position - at).magnitude; if (d < best) { best = d; car = c; } }
            }
            else
            {
                var pl = mainscript.s.player;
                car = pl != null ? pl.Car : null;
                if (car == null && pl != null && pl.seat != null) car = pl.seat.transform.root.GetComponentInChildren<carscript>();   // seated, Car not set
                if (car == null) return "{\"error\":\"no car (sitting " + (pl != null && pl.Bsitting) + ", seat " + (pl != null && pl.seat != null ? pl.seat.name + " of " + pl.seat.transform.root.name : "none") + ")\"}";
            }
            if (car == null || car.RB == null) return "{\"error\":\"no car\"}";
            if (Net.Entities.NetOf(car.transform.root, out _, out bool proxy) && proxy) return "{\"error\":\"that car is another machine's copy\"}";
            var v = car.transform.forward * speed;
            foreach (var rb in car.transform.root.GetComponentsInChildren<Rigidbody>()) if (!rb.isKinematic) { rb.velocity = v; rb.angularVelocity = Vector3.zero; }
            bool wasLocked = car.whlocked;
            car.BhandBrake = hold;   // hold: the handbrake stays pulled (as a seated player's automatic gearbox holds it)
            car.Lock(false);   // the parked car's friction boxes off now (the game drops them only when its handbrake input has eased below 0.5)
            float kmh = speed * 3.6f;
            car.speed = kmh; car.lspeed = kmh; car.LV = v;
            return "{\"launched\":" + Json.Str(car.transform.root.name) + ",\"wasLocked\":" + (wasLocked ? "true" : "false") + ",\"speed\":" + speed.ToString("F1", CultureInfo.InvariantCulture) + "}";
        }
    }
}
