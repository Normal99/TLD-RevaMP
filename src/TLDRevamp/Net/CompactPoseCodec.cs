using System;

namespace TLDRevamp.Net
{
    internal static class CompactPoseCodec
    {
        internal static void Write(NetWriter w, uint net, uint epoch, ushort seq, double stamp,
                                   double x, double y, double z, float yaw, bool moving, bool final = false)
        {
            w.U8(Entities.CompactState); w.VarU32(net); w.VarU32(epoch); w.U16(seq);
            w.U32((uint)(long)Math.Round(stamp * 1000.0));
            w.I32(Cm(x)); w.I32(Cm(y)); w.I32(Cm(z));
            w.U16((ushort)(int)Math.Round((yaw % 360 + 360) % 360 / 360 * 65536)); w.U8((byte)((moving ? 1 : 0) | (final ? 2 : 0)));
        }

        internal static void Read(NetReader r, out uint net, out uint epoch, out double stamp,
                                  out double x, out double y, out double z, out float yaw, out bool moving, out bool final)
        {
            net = r.VarU32(); epoch = r.VarU32(); r.U16(); stamp = r.U32() / 1000.0;
            x = r.I32() * 0.01; y = r.I32() * 0.01; z = r.I32() * 0.01;
            yaw = r.U16() / 65536f * 360f; byte flags = r.U8(); moving = (flags & 1) != 0; final = (flags & 2) != 0;
        }

        private static int Cm(double v) => (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Round(v * 100)));
    }
}
