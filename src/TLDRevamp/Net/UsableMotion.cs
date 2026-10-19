using System;
using System.Collections.Generic;

namespace TLDRevamp.Net
{
    internal sealed class UsableMotion<T>
    {
        private readonly List<(double Stamp, T Value)> _samples = new List<(double, T)>();
        internal double Latest => _samples.Count == 0 ? double.NegativeInfinity : _samples[_samples.Count - 1].Stamp;

        internal void Add(double stamp, T value)
        {
            if (stamp <= Latest) return;
            _samples.Add((stamp, value));
        }

        internal bool Sample(double stamp, out T a, out T b, out float t)
        {
            a = b = default; t = 0;
            if (_samples.Count == 0) return false;
            int i = 0;
            while (i + 1 < _samples.Count && _samples[i + 1].Stamp <= stamp) i++;
            if (i > 0) _samples.RemoveRange(0, i);
            a = b = _samples[0].Value;
            if (_samples.Count > 1 && stamp > _samples[0].Stamp)
            {
                b = _samples[1].Value;
                t = (float)Math.Min(1, (stamp - _samples[0].Stamp) / (_samples[1].Stamp - _samples[0].Stamp));
            }
            return true;
        }
    }
}
