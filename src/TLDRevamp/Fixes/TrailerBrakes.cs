using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Trailer brakes: a hitched trailer's wheels brake with the car towing it (its brake pedal, its handbrake lever),
    /// and the game's run key (Left Shift unless rebound) brakes the trailers alone while driving: steadies a swaying
    /// trailer, holds a downhill run. The trailer's brake lights come on with it. In the game a trailer's wheels only
    /// brake by its own lever on the drawbar (utanfutoscript.brake), and its lights follow the car (rearlights).
    /// The idea is Long Haul Overhaul's (QuantumRage & Wisher, LGPL-3.0), written anew here against the beta.
    ///
    /// The torque goes on top of the game's own on the machine that simulates the trailer (its body isn't kinematic):
    /// the tow car's driver's, as hitched trailers go with the tow car (Net/Entities). The car's own inputs are read
    /// there. Another player sees the brake lights the pedal lights; the run-key brake lights are this machine's only.
    ///
    /// Settings → Revamp → "Trailer brakes". Runtime switch (bridge `set`): TLDRevamp.Fixes.TrailerBrakes.Enabled
    [HarmonyPatch(typeof(utanfutoscript), "Update")]
    public static class TrailerBrakes
    {
        public static bool Enabled = true;
        /// Of the tow car's brake torque per wheel (carscript.brakePower), on each trailer wheel.
        public static float PedalShare = 0.6f;
        /// The run key's brake, per trailer wheel, as a share of the tow car's brakePower.
        public static float AloneShare = 0.8f;
        /// The car's handbrake lever, per trailer wheel, as a share of its maxHandBrake.
        public static float LeverShare = 1f;
        public static long Braking;
        public static string Last = "";   // the last braking trailer: masses, k, torque (tests)
        public static bool TestAlone;   // tests: the run-key brake held (bridge `set`)

        private sealed class State { public bool Set; public bool Alone; }
        private static readonly ConditionalWeakTable<utanfutoscript, State> States = new ConditionalWeakTable<utanfutoscript, State>();

        /// The car at the front of this trailer's chain (rearlights carries it down a chain of trailers).
        public static carscript TowCar(utanfutoscript t) => t != null && t.attached && t.rear != null ? t.rear.currentCar : null;

        /// The run-key brake on this trailer (its brake lights).
        public static bool BrakingAlone(utanfutoscript t) => t != null && States.TryGetValue(t, out var s) && s.Alone;

        [HarmonyPostfix]
        private static void Postfix(utanfutoscript __instance)
        {
            var t = __instance;
            if (t.wheels == null) return;
            var s = States.GetOrCreateValue(t);
            var car = Enabled ? TowCar(t) : null;
            var rb = t.RB;
            float nm = 0f; bool alone = false;
            if (car != null && rb != null && !rb.isKinematic)
            {
                // scaled by the weight each trailer wheel carries against each car wheel: the car's brakes are sized
                // for the car (the IFA's 3 t trailer stopped no shorter with the car01-sized torque, trailerbrakes.py)
                float k = PerWheelMassRatio(car, t);
                nm = car.brakePower * PedalShare * Mathf.Clamp01(car.brake) * k;
                if (car.BhandBrake) nm = Mathf.Max(nm, car.maxHandBrake * LeverShare * k);
                if (AloneKey(car)) { nm = Mathf.Max(nm, car.brakePower * AloneShare * k); alone = true; }
            }
            s.Alone = alone;
            if (nm <= 0f)
            {
                // the game sets brakeTorque itself only while the trailer has its own lever: otherwise ours is undone here
                if (s.Set && t.brake == null) foreach (var w in t.wheels) if (w != null) w.brakeTorque = 0f;
                s.Set = false;
                return;
            }
            foreach (var w in t.wheels) if (w != null) w.brakeTorque = Mathf.Max(w.brakeTorque, nm);
            if (!s.Set) Last = $"{t.name}: {t.RB.mass:F0} kg on {t.wheels.Count} wheels behind {car.name} {car.RB.mass:F0} kg on {car.allWH.Count}; brakePower {car.brakePower:F0}, k {PerWheelMassRatio(car, t):F2}, {nm:F0} Nm a wheel";
            s.Set = true;
            Braking++;
        }

        private static float PerWheelMassRatio(carscript car, utanfutoscript t)
        {
            int cw = car.allWH != null && car.allWH.Count > 0 ? car.allWH.Count : 4, tw = Mathf.Max(1, t.wheels.Count);
            float cm = car.RB != null ? car.RB.mass : 1000f, tm = t.RB != null ? t.RB.mass : 300f;
            return Mathf.Clamp((tm / tw) / Mathf.Max(1f, cm / cw), 0.25f, 4f);
        }

        /// The game's run key held by the local driver of the tow car (not while leaning: run + lean moves the head).
        private static bool AloneKey(carscript car)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !pl.Bsitting || pl.seat == null || !pl.seat.driverSeat0 || pl.seat.transform.root != car.transform.root) return false;
            var inp = inputscript.i;
            return TestAlone || (inp != null && inp.run && !inp.lean);
        }

        /// The trailer's brake lights while the run key brakes it alone.
        [HarmonyPatch(typeof(rearlights), "Update")]
        private static class Lights
        {
            [HarmonyPostfix]
            private static void Postfix(rearlights __instance)
            {
                if (__instance.utanfuto == null || !BrakingAlone(__instance.utanfuto)) return;
                if (__instance.FLight != null) foreach (var l in __instance.FLight) if (l != null) l.SetActive(true);
                if (__instance.FLamp != null) foreach (var r in __instance.FLamp) if (r != null) r.material = __instance.FLampOn;
            }
        }
    }
}
