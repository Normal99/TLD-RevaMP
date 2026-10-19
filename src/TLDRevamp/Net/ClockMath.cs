using System;

namespace TLDRevamp.Net
{
    /// Keep clock projection independent of Unity's scaled and frame-clamped clocks.
    internal static class ClockMath
    {
        internal static double Wrap(double time, double length) => length > 0 ? (time % length + length) % length : time;
        internal static double Difference(double target, double current, double length) =>
            length > 0 ? Wrap(target - current + length * 0.5, length) - length * 0.5 : target - current;

        internal static double Project(double position, double stamp, double now, double rate) =>
            position + Math.Max(0, now - stamp) * rate;

        internal static bool AcceptWorldSample(double stamp, double previous, double wake, bool snap, bool awaitingWake, ushort epoch, ushort previousEpoch) =>
            stamp >= wake && (stamp > previous || stamp == previous && snap) && (!awaitingWake || epoch != previousEpoch);

        internal static double Follow(double current, double elapsed, double target, double rate, double length, bool force, out bool snapped)
        {
            double next = current + Math.Max(0, elapsed) * rate;
            double error = Difference(target, next, length);
            snapped = force || Math.Abs(error) > 30;
            if (snapped) return Wrap(target, length);
            // At most ten percent of normal speed; converge without a visible step of the sun.
            double correction = Math.Max(-0.1, Math.Min(0.1, error / 5));
            return Wrap(next + correction * Math.Max(0, elapsed), length);
        }

        internal static double RadioPosition(double position, double stamp, double now, double rate, double length) =>
            Math.Max(0, Math.Min(Math.Max(0, length - 0.01), Project(position, stamp, now, rate)));
    }
}
