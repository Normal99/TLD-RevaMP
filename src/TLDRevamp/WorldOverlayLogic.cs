using System;
using System.Globalization;

namespace TLDRevamp
{
    internal static class WorldOverlayLogic
    {
        internal const int Limit = 40;
        internal static int Colour(double metres) => double.IsNaN(metres) ? -1 : metres < .1 ? 0 : metres < .5 ? 1 : 2;
        internal static bool Stale(double age, bool moving) => moving && age > Net.BandwidthRules.KeepaliveS + .5;
        internal static bool Moving(bool final, bool constrained, bool velocity, bool pose) => !final && !constrained && (velocity || pose);
        internal static bool BadLink(int a, int b, bool sameOwner, bool reciprocal) => !reciprocal || sameOwner && a >= 0 && b >= 0 && a != b;
        internal static string Owner(int owner, int me) => owner < 0 ? "unshared" : owner == me ? "you" : owner == 0 ? "host" : "player " + owner.ToString(CultureInfo.InvariantCulture);
        internal static string SendState(bool locked, bool rest, bool tail, bool changed) => locked ? "locked" : rest ? "at rest" : tail && !changed ? "quiet tail" : "moving";
        internal static string Mode(int mode) => mode == 0 ? "interpolating" : mode == 1 ? "extrapolating" : mode == 3 ? "snap" : "holding";
        internal static string Error(double metres, float angle, double age, bool moving) => double.IsNaN(metres) ? "no owner sample" :
            (metres * 100).ToString("F0", CultureInfo.InvariantCulture) + " cm / " + angle.ToString("F1", CultureInfo.InvariantCulture) + " deg" + (Stale(age, moving) ? " STALE" : "");
        internal static string Age(double age) => double.IsNaN(age) ? "age unknown" : "age " + age.ToString("F2", CultureInfo.InvariantCulture) + " s";
    }

    /// Bounded insertion selection: enumerate once, keep only the nearest objects, stable on equal distance.
    internal sealed class OverlayNearest<T>
    {
        internal readonly T[] Items;
        private readonly uint[] _ids;
        private readonly float[] _distance;
        internal int Count;
        internal OverlayNearest(int capacity) { Items = new T[capacity]; _ids = new uint[capacity]; _distance = new float[capacity]; }
        internal void Clear() { Array.Clear(Items, 0, Count); Count = 0; }
        internal void Offer(T item, uint id, float distance)
        {
            if (float.IsNaN(distance) || distance < 0 || distance > 300f * 300f) return;
            int at = Count;
            while (at > 0 && (distance < _distance[at - 1] || distance == _distance[at - 1] && id < _ids[at - 1])) at--;
            if (at == Items.Length) return;
            int end = Math.Min(Count, Items.Length - 1);
            for (int i = end; i > at; i--) { Items[i] = Items[i - 1]; _ids[i] = _ids[i - 1]; _distance[i] = _distance[i - 1]; }
            Items[at] = item; _ids[at] = id; _distance[at] = distance;
            Count = Math.Min(Count + 1, Items.Length);
        }
    }
}
