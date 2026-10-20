using System;

namespace TLDRevamp.Net
{
    // Protocol 39: reliable changes, fenced by owner epoch. Global targets survive motion and origin shifts.
    internal static class RabbitStateCodec
    {
        internal struct State
        {
            internal uint Net, Epoch;
            internal byte Flags, Run;
            internal bool Seen;
            internal double X, Y, Z;
        }

        internal static void Write(NetWriter w, State s)
        {
            w.U8(Entities.RabbitState); w.U32(s.Net); w.U32(s.Epoch);
            w.U8(s.Flags); w.U8(s.Run); w.Bool(s.Seen);
            w.I32(Cm(s.X)); w.I32(Cm(s.Y)); w.I32(Cm(s.Z));
        }

        internal static State Read(NetReader r) => new State
        {
            Net = r.U32(), Epoch = r.U32(), Flags = r.U8(), Run = r.U8(), Seen = r.Bool(),
            X = r.I32() * .01, Y = r.I32() * .01, Z = r.I32() * .01
        };

        private static int Cm(double v) => (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Round(v * 100)));
    }
}
