using System;

namespace TLDRevamp.Net
{
    internal static class BandwidthRules
    {
        internal const double PositionM = 0.01;
        internal const float RotationDeg = 0.3f, KeepaliveS = 1f, QuietTailS = 5f;

        internal enum Quiet { None, Pose, Locked, Attached, Final }
        internal struct Tail { internal double VisualAt; internal bool Active; }

        internal static bool PoseChanged(double distanceSquared, float angle) =>
            distanceSquared > PositionM * PositionM || angle > RotationDeg;

        internal static bool PositionChanged(double distanceSquared, bool atRest) =>
            distanceSquared > PositionM * PositionM * (atRest ? 4 : 1);

        internal static bool AttachedPart(bool car, bool attached, bool part, bool slot, bool carPoint) =>
            !car && attached && part && slot && carPoint;

        internal static bool RabbitDue(bool epoch, byte previous, byte flags, byte previousRun, byte run,
                                       bool previousSeen, bool seen) =>
            epoch || previous != flags || Math.Abs(previousRun - run) > 2 || previousSeen != seen;

        internal static Quiet StateQuiet(ref Tail tail, double now, bool first, bool pose, bool limbs, bool visual, bool control, bool moving,
                                        bool wasAtRest, bool locked, bool attached, double elapsed, bool driven = false, bool stored = false,
                                        bool resumedMotion = false)
        {
            if (first || pose || limbs || visual)
            {
                tail.VisualAt = now; tail.Active = moving && !locked && !attached && !stored;
            }
            if (locked || attached || stored) tail.Active = false;
            if (control || first) return Quiet.None;
            if (attached) return Quiet.Attached;
            if (pose || limbs || visual) return Quiet.None;
            if (driven) return elapsed >= KeepaliveS * 0.5 ? Quiet.None : Quiet.Pose;
            if (!moving)
            {
                // A sub-threshold sample between visible steps is not a stop. Keep the cached moving pose until
                // one keepalive interval is quiet, then deliver one reliable final to near and far copies alike.
                if (!wasAtRest && tail.Active && now - tail.VisualAt < KeepaliveS) return Quiet.Pose;
                tail.Active = false;
                if (!wasAtRest) return Quiet.Final;
            }
            if (locked) return Quiet.Locked;
            if (!moving || stored || !tail.Active) return Quiet.Pose;
            // Keepalives never extend the tail. Waking alone cannot restart a completed tail.
            if (now - tail.VisualAt >= QuietTailS) { tail.Active = false; return Quiet.Final; }
            // Releases, handovers and unlocks retain the physics cadence between visible samples.
            // The same visible-change deadline still ends awake jitter with one reliable final.
            return resumedMotion || elapsed >= KeepaliveS ? Quiet.None : Quiet.Pose;
        }

        internal static bool CarVisualChanged(byte[] previous, int length, byte[] bytes, int count)
        {
            if (length != count || count < 3 || previous[0] != bytes[0]) return true;
            int wheels = bytes[0] & 127, at = 1;
            for (int i = 0; i < wheels; i++, at += 4)
            {
                if (Math.Abs((sbyte)previous[at] - (sbyte)bytes[at]) > 4 || previous[at + 1] != bytes[at + 1] ||
                    Math.Abs(I16(previous, at + 2) - I16(bytes, at + 2)) > 2) return true;
            }
            if ((bytes[0] & 128) != 0) at += wheels * 2;   // torques have no parked visual of their own
            if (previous[at] != bytes[at]) return true;
            if ((bytes[at++] & 1) != 0)
            {
                if (Math.Abs((previous[at] | previous[at + 1] << 8) - (bytes[at] | bytes[at + 1] << 8)) > 50) return true;
                at += 2;
            }
            if (previous[at] != bytes[at]) return true;
            if ((bytes[at++] & 1) != 0 && (previous[at] != bytes[at] || Math.Abs(previous[at + 1] - bytes[at + 1]) > 4)) return true;
            return false;
        }

        private static int I16(byte[] bytes, int at) => (short)(bytes[at] | bytes[at + 1] << 8);

        internal static bool SkipState(bool enabled, bool same, bool moving, bool wasAtRest, bool control) =>
            enabled && same && !moving && wasAtRest && !control;

        internal static int StatePoseAt(byte[] bytes, int length)
        {
            int at = 1;
            for (int n = 0; n < 2; n++)
            {
                int end = Math.Min(length, at + 5);
                while (at < end && bytes[at++] >= 128) { }
            }
            return at + 6;   // sequence and stamp are not state
        }

        internal static bool SameState(byte[] previous, int length, byte[] bytes, int count)
        {
            if (length != count || count < 10) return false;
            int at = StatePoseAt(bytes, count), oldAt = StatePoseAt(previous, length);
            if (at != oldAt || at > count) return false;
            for (int i = 0; i < at - 6; i++) if (previous[i] != bytes[i]) return false;
            for (int i = at; i < count; i++) if (previous[i] != bytes[i]) return false;
            return true;
        }

        internal static bool CreatureDue(byte previous, byte flags, bool sameInputs, float now, float sentAt) =>
            previous != flags || !sameInputs || now - sentAt >= 1f;
    }
}
