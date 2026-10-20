using UnityEngine;

namespace TLDRevamp
{
    public static partial class DriveLab
    {
        private static carscript _brakeCar;

        /// Bridge brakecar: hold the brakes like a player. Zeroing velocity reads as a crash to DamageStuff,
        /// and leaves a hitched trailer moving into a car that has stopped in one step.
        public static string Brake()
        {
            var p = mainscript.s != null ? mainscript.s.player : null;
            var car = p != null ? p.Car : null;
            if (car == null || car.RB == null || !car.isPlayerDriving) return "{\"error\":\"not in a car\"}";
            Active = false; _brakeCar = car;
            BrakeInput(inputscript.i);
            return "{\"braked\":true}";
        }

        private static bool BrakeInput(inputscript input)
        {
            if (_brakeCar == null) return false;
            var p = mainscript.s != null ? mainscript.s.player : null;
            if (Active || p == null || p.Car != _brakeCar || !_brakeCar.isPlayerDriving || _brakeCar.RB == null)
            { _brakeCar = null; return false; }
            if (input == null) return false;
            bool reversing = _brakeCar.IsAutoShift() && Vector3.Dot(_brakeCar.RB.velocity, _brakeCar.transform.forward) < 0f;
            // AutoShift treats a brake held at rest as a request to reverse. The handbrake holds the stop.
            float brake = _brakeCar.RB.velocity.sqrMagnitude > 0.25f ? 1f : 0f;
            input.throttle = reversing ? brake : 0f;
            input.brake = reversing ? 0f : brake;
            input.handbrake = 1f;
            return true;
        }
    }
}
