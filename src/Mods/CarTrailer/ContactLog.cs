using System.Collections.Generic;
using UnityEngine;

namespace CarTrailer
{
    /// Tests only (Probe.Tune "contacts=1", Probe.Contacts): what the trailer's body touches while it rides, with
    /// which of its colliders and how hard (tools/cartrailerride.py --contacts).
    internal class ContactLog : MonoBehaviour
    {
        internal static readonly List<string> Log = new List<string>();

        private void OnCollisionEnter(Collision c) => Note("hit", c);
        private void OnCollisionStay(Collision c) { if (Time.frameCount % 10 == 0) Note("on", c); }

        private void Note(string what, Collision c)
        {
            if (Log.Count > 400) return;
            var own = c.contactCount > 0 ? c.GetContact(0).thisCollider : null;
            Log.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2} {1} {2} by {3} at {4} v {5:F1}",
                Time.time, what, c.collider != null ? c.collider.name : "?", own != null ? own.name : "?",
                own != null ? transform.InverseTransformPoint(c.GetContact(0).point).ToString("F2") : "-", c.relativeVelocity.magnitude));
        }
    }
}
