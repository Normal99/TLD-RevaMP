using System;

namespace TLDRevamp
{
    internal struct CaptureCandidate : IComparable<CaptureCandidate>
    {
        internal uint Net;
        internal int Priority;
        internal double Distance;
        public int CompareTo(CaptureCandidate other)
        {
            int p = Priority.CompareTo(other.Priority); if (p != 0) return p;
            int d = Distance.CompareTo(other.Distance); return d != 0 ? d : Net.CompareTo(other.Net);
        }
        internal static int Rank(bool seated, bool focused, bool center, double distance) => seated ? 0 : focused ? 1 : center && distance <= 90000 ? 2 : -1;
    }

    internal sealed class FocusRecent
    {
        private readonly uint[] _nets = new uint[4];
        private readonly double[] _seen = new double[4];
        internal void Clear() { Array.Clear(_nets, 0, _nets.Length); Array.Clear(_seen, 0, _seen.Length); }
        internal void Observe(uint net, bool held, int owner, int me, double now)
        {
            if (!held || owner == me || net == 0) return;
            int at = Array.IndexOf(_nets, net); if (at < 0) at = _nets.Length - 1;
            for (int i = at; i > 0; i--) { _nets[i] = _nets[i - 1]; _seen[i] = _seen[i - 1]; }
            _nets[0] = net; _seen[0] = now;
        }
        internal uint Net(int i, double now) => now - _seen[i] <= 35 ? _nets[i] : 0;
    }
}
