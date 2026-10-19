using System;

namespace TLDRevamp.Net
{
    internal static class BandwidthRules
    {
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
