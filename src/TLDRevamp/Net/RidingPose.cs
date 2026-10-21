using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Body and limbs share one sample in the vehicle owner's clock. Never extrapolate movement inside a vehicle:
    /// a late packet holds the passenger in place on it, while the vehicle keeps moving through its own interpolator.
    internal sealed class RidingPose
    {
        private readonly List<PlayerLook.Pose> _samples = new List<PlayerLook.Pose>(60);
        internal PlayerLook.Pose Latest => _samples.Count > 0 ? _samples[_samples.Count - 1] : null;

        internal void Add(PlayerLook.Pose p)
        {
            var last = Latest;
            if (last != null && p.Stamp <= last.Stamp) return;
            if (last != null && (last.Net != p.Net || last.Epoch != p.Epoch || last.Seat != p.Seat || p.Clock < last.Clock)) _samples.Clear();
            _samples.Add(p);
            if (_samples.Count > 60) _samples.RemoveAt(0);
        }

        internal bool Sample(double clock, PlayerLook.Pose into)
        {
            if (_samples.Count == 0) return false;
            int i = _samples.Count - 1;
            while (i > 0 && _samples[i].Clock > clock) i--;
            var a = _samples[i]; var b = _samples[Math.Min(i + 1, _samples.Count - 1)];
            float u = b.Clock > a.Clock ? Mathf.Clamp((float)((clock - a.Clock) / (b.Clock - a.Clock)), 0, 1) : 0;
            into.Net = a.Net; into.Epoch = a.Epoch; into.Seat = a.Seat; into.Clock = clock;
            into.Body = Vector3.Lerp(a.Body, b.Body, u);
            into.Facing = Quaternion.Slerp(a.Facing, b.Facing, u);
            for (int j = 0; j < 6; j++) into.V[j] = Vector3.Lerp(a.V[j], b.V[j], u);
            for (int j = 6; j < 12; j++) into.V[j] = Quaternion.Slerp(Quaternion.Euler(a.V[j]), Quaternion.Euler(b.V[j]), u).eulerAngles;
            // a hand on the wheel has no data in that sample (PlayerLook.WheelHands): take the other one's, never blend from zero
            for (int h = 1; h <= 2; h++)
            {
                if ((a.WheelHands & h) != 0) { into.V[h] = b.V[h]; into.V[h + 6] = b.V[h + 6]; }
                else if ((b.WheelHands & h) != 0) { into.V[h] = a.V[h]; into.V[h + 6] = a.V[h + 6]; }
            }
            into.WheelHands = (byte)((a.WheelHands & b.WheelHands & 3) | (b.WheelHands & 12));   // no data on either side: the copy must place it
            into.Controls[0] = b.Controls[0]; into.Controls[1] = b.Controls[1];
            into.ThRotX = Mathf.LerpAngle(a.ThRotX, b.ThRotX, u);
            into.Zooming = a.Zooming; into.Menus = a.Menus; into.Acts = a.Acts;
            return true;
        }
    }
}
