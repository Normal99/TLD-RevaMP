using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Optional driving assists for the car the local player drives (Settings → Revamp, all off by default): trailer
    /// sway reduction, roll correction and traction control. The ideas are Long Haul Overhaul's (QuantumRage & Wisher,
    /// LGPL-3.0), written anew here against the beta and kept simple.
    ///
    /// They push only bodies this machine simulates (not kinematic): the driver's car and the trailers it tows, which
    /// go with it (Net/Entities). Another player in the car feels them through the driver's physics, so a car handles
    /// as its driver set it.
    ///
    ///  Sway reduction: a towed trailer's turning, beyond the tow car's own, is damped (a fishtailing trailer settles).
    ///  Roll correction: the car's lean is pushed back towards upright, and its rolling damped (fewer rollovers in
    ///                   fast corners, still tips over on a real obstacle or a steep side slope).
    ///  Traction control: a driven wheel spinning faster than the ground under it gets less engine torque.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.DrivingAssists.Sway / Roll / Traction
    public static class DrivingAssists
    {
        public static bool Sway, Roll, Traction;
        public static float SwayDamp = 3f;               // 1/s on the trailer's extra yaw rate
        public static float RollSpring = 6f, RollDamp = 2.5f, RollMaxAccel = 8f;   // rad/s² per rad, per rad/s; cap
        public static float RollDeadDeg = 2f;
        public static float SpinMs = 2f;                  // tyre speed over the ground's (m/s) where traction control starts
        public static long SwayTicks, RollTicks, TractionCuts;

        private static carscript Driven()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !pl.Bsitting || pl.seat == null || !pl.seat.driverSeat0) return null;
            var car = pl.seat.transform.root.GetComponent<carscript>();
            return car != null && car.RB != null && !car.RB.isKinematic ? car : null;
        }

        /// Each physics step (Plugin's runner).
        public static void FixedTick()
        {
            if (!Sway && !Roll) return;
            var car = Driven();
            if (car == null) return;
            var rb = car.RB;
            if (Roll) RollTick(car.transform, rb);
            if (Sway) SwayTick(car, rb);
        }

        private static void RollTick(Transform t, Rigidbody rb)
        {
            var fwd = t.forward;
            // the lean: the car's up against the world's, about its length
            var up = Vector3.ProjectOnPlane(t.up, fwd);
            var worldUp = Vector3.ProjectOnPlane(Vector3.up, fwd);
            if (up.sqrMagnitude < 1e-4f || worldUp.sqrMagnitude < 1e-4f) return;
            float roll = Vector3.SignedAngle(worldUp, up, fwd);
            if (Mathf.Abs(roll) > 60f) return;   // on its side: that's a crash, not a corner
            float lean = Mathf.Abs(roll) > RollDeadDeg ? (roll - Mathf.Sign(roll) * RollDeadDeg) * Mathf.Deg2Rad : 0f;
            float rate = Vector3.Dot(rb.angularVelocity, fwd);
            float a = Mathf.Clamp(-(lean * RollSpring + rate * RollDamp), -RollMaxAccel, RollMaxAccel);
            if (Mathf.Abs(a) < 1e-3f) return;
            rb.AddTorque(fwd * a, ForceMode.Acceleration);
            RollTicks++;
        }

        private static void SwayTick(carscript car, Rigidbody rb)
        {
            foreach (var g in TrailerGrip.All)
            {
                var u = g != null ? g.utanfuto : null;
                if (u == null || TrailerBrakes.TowCar(u) != car) continue;
                var trb = u.RB;
                if (trb == null || trb.isKinematic) continue;
                var up = u.transform.up;
                float extra = Vector3.Dot(trb.angularVelocity - rb.angularVelocity, up);
                trb.AddTorque(-up * extra * SwayDamp, ForceMode.Acceleration);
                SwayTicks++;
            }
        }

        /// Traction control: after the car sets its driven wheels' torque (carscript.Throttle, every frame).
        [HarmonyPatch(typeof(carscript), nameof(carscript.Throttle))]
        private static class TractionPatch
        {
            [HarmonyPostfix]
            private static void Postfix(carscript __instance)
            {
                if (!Traction || __instance.WHDriven == null || Driven() != __instance) return;
                var rb = __instance.RB;
                foreach (var w in __instance.WHDriven)
                {
                    if (w == null || w.motorTorque == 0f || !w.GetGroundHit(out var hit)) continue;
                    // spinning: the tyre's surface faster than the ground under it, by more than SpinMs and a third
                    // (WheelHit.forwardSlip alone cut the torque of a car pulling a loaded trailer from rest: stuck at
                    // 2 km/h, cartrailer.py --assists, 2026-10-15)
                    float tyre = w.rpm * w.radius * 2f * Mathf.PI / 60f;
                    float ground = Vector3.Dot(rb.GetPointVelocity(hit.point), hit.forwardDir);
                    float over = Mathf.Sign(w.motorTorque) * (tyre - ground);
                    float allowed = SpinMs + Mathf.Abs(ground) * 0.33f;
                    if (over <= allowed) continue;
                    w.motorTorque *= Mathf.Clamp(allowed / over, 0.3f, 1f);
                    TractionCuts++;
                }
            }
        }
    }
}
