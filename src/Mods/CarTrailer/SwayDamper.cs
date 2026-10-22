using UnityEngine;

namespace CarTrailer
{
    /// A friction sway control, as real car trailers have on the drawbar: a turning force against the trailer swinging
    /// relative to the car that tows it, on both (what the trailer is held back by, the car gets). It does nothing
    /// while the two turn together, so it doesn't stiffen bends; it only damps the swing.
    internal class SwayDamper : MonoBehaviour
    {
        public float Strength;   // N·m per rad/s of swing

        private Rigidbody _rb;
        private attachgripscript _grip;

        private void FixedUpdate()
        {
            if (Strength <= 0f) return;
            if (_rb == null) { _rb = GetComponent<Rigidbody>(); _grip = GetComponent<attachgripscript>(); }
            if (_rb == null || _grip == null || _grip.Grip == null || _rb.isKinematic) return;
            var ball = _grip.Grip.GetComponent<attachablescript>();
            if (ball == null || !ball.attached || ball.J == null) return;
            var car = ball.J.connectedBody;
            if (car == null || car.isKinematic) return;
            float swing = Vector3.Dot(_rb.angularVelocity - car.angularVelocity, transform.up);
            var torque = -Strength * swing * transform.up;
            _rb.AddTorque(torque);
            car.AddTorque(-torque);
        }
    }
}
