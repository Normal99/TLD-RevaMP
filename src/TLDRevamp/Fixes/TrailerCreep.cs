using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// An unhitched trailer creeps: its wheels roll freely and nothing holds the drawbar, so on the slightest slope a
    /// parked trailer slowly rolls off (the idea from Long Haul Overhaul's trailer friction fix, QuantumRage & Wisher,
    /// LGPL-3.0; written anew here against the beta's utanfutoscript).
    ///
    /// Here a trailer that isn't hitched (utanfutoscript.attached), stands on at least one wheel and moves slower than
    /// CreepMs loses its speed along the ground each physics step, and its turning about the vertical, and the slope's
    /// pull is cancelled, as a parked car's brake would do. Not while the local player holds its drawbar (walking it
    /// by hand) and not on a copy (a kinematic body another machine moves): the machine simulating it does this.
    /// Faster than CreepMs (a car bumps it, a slope too steep to hold) it is left alone.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.TrailerCreep.Enabled
    [HarmonyPatch(typeof(utanfutoscript), "Start")]
    public static class TrailerCreep
    {
        public static bool Enabled = true;
        public static float CreepMs = 0.55f;   // 2 km/h
        public static float BrakeNm = 1000f;   // on each wheel while held (at least the trailer's own brakeForce)
        public static long Held;

        /// Tests (bridge `call TLDRevamp.Fixes.TrailerCreep.Trailers`): the spawnable items with a trailer script
        /// (utanfutoscript) or a drawbar (attachgripscript): [index] name (wheels, utanfuto, grip).
        public static string Trailers()
        {
            var rows = new System.Collections.Generic.List<string>();
            var db = itemdatabase.s != null ? itemdatabase.s.items : null;
            if (db == null) return "no itemdatabase";
            for (int i = 0; i < db.Length; i++)
            {
                if (db[i] == null) continue;
                var u = db[i].GetComponentsInChildren<utanfutoscript>(true);
                var g = db[i].GetComponentsInChildren<attachgripscript>(true);
                if (u.Length == 0 && g.Length == 0) continue;
                int wheels = 0; foreach (var x in u) wheels += x.wheels != null ? x.wheels.Count : 0;
                rows.Add($"[{i}] {db[i].name} (utanfuto {u.Length}, wheels {wheels}, grips {g.Length}, car {(db[i].GetComponent<carscript>() != null)})");
            }
            return string.Join("; ", rows);
        }

        [HarmonyPostfix]
        private static void Postfix(utanfutoscript __instance)
        {
            if (__instance.GetComponent<Holder>() == null) __instance.gameObject.AddComponent<Holder>();
        }

        public sealed class Holder : MonoBehaviour
        {
            private utanfutoscript _t;
            private attachgripscript _grip;

            private void Awake() { _t = GetComponent<utanfutoscript>(); _grip = GetComponentInChildren<attachgripscript>(true); }

            private bool _braked;
            private float _pushedUntil;

            /// A car against it lets it go for a second (a car nudging it at a walking pace was stopped dead: 0.4 m
            /// in 30 s at 0.4 m/s, trailerpush.py). Items lying on it don't.
            private void OnCollisionStay(Collision c)
            {
                var rb = c.rigidbody;
                if (rb != null && rb.transform.root != transform.root && rb.GetComponentInParent<carscript>() != null) _pushedUntil = Time.time + 1f;
            }

            private void FixedUpdate()
            {
                if (!Hold()) Release();
            }

            /// Its wheels braked as the game's own trailer brake does (utanfutoscript.Update sets brakeTorque each
            /// frame only while that brake exists; then it puts its own value back after us).
            private void Brake(bool on)
            {
                if (_t.wheels == null) return;
                float nm = on ? Mathf.Max(_t.brakeForce, BrakeNm) : 0f;
                foreach (var w in _t.wheels) if (w != null && (on || w.brakeTorque == Mathf.Max(_t.brakeForce, BrakeNm))) w.brakeTorque = nm;
                _braked = on;
            }

            private void Release() { if (_braked) Brake(false); }

            private bool Hold()
            {
                if (!Enabled || _t == null || _t.attached) return false;
                var rb = _t.RB != null ? _t.RB : GetComponent<Rigidbody>();
                if (rb == null || rb.isKinematic || rb.IsSleeping()) return _braked;   // asleep: stays braked
                var v = rb.velocity;
                if (v.sqrMagnitude > CreepMs * CreepMs) return false;
                if (HeldByPlayer()) return false;
                if (Time.time < _pushedUntil) return false;
                if (!OnGround()) return false;
                Brake(true);
                // held as a parked car's brake would: its speed along the ground and its turning about up go, and the
                // slope's pull along the ground is cancelled (only zeroing the speed left it rolling 0.5 m in 30 s on a
                // 10 % slope: each physics step's gravity started it again). Settling onto its wheels (along up) stays.
                var up = transform.up;
                rb.velocity = up * Vector3.Dot(v, up);
                var w = rb.angularVelocity;
                rb.angularVelocity = w - up * Vector3.Dot(w, up);
                var g = Physics.gravity;
                rb.AddForce(-(g - up * Vector3.Dot(g, up)), ForceMode.Acceleration);
                Held++;
                return true;
            }

            private bool HeldByPlayer()
            {
                var pl = mainscript.s != null ? mainscript.s.player : null;
                if (pl == null) return false;
                if (_grip != null && _grip.Grip != null && pl.pickedUp != null && pl.pickedUp.gameObject == _grip.Grip) return true;
                return pl.pushgripping && pl.pushgrip != null && pl.pushgrip.transform.root == transform.root;
            }

            private bool OnGround()
            {
                if (_t.wheels == null) return false;
                foreach (var w in _t.wheels) if (w != null && w.isGrounded) return true;
                return false;
            }
        }
    }
}
