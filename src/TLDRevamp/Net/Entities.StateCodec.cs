using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Compact car/item state (protocol 8). Every car state reaches every other player 20× a second — at 16 players the
    /// host relays ~4,500 of them a second (loadtest, v0.57.68: 731 KB/s host upload with 112-byte states). Now ~55 bytes:
    ///   net, epoch      varint (1–2 bytes each)          seq      u16
    ///   stamp           u32, sender's ms                 position 3× i32 mm (±2,147 km)
    ///   rotation        smallest three: u8 index + 3× i16 (≈0.002° steps)
    ///   velocity        3× i16 cm/s (±327 m/s)           flags    u8 (1 = driven)
    /// Wheels and engine follow (WriteWheels/WriteEngine).
    public static partial class Entities
    {
        internal static void WriteStateHead(NetWriter w, uint net, uint epoch, ushort seq, double stamp, Vector3d g, Quaternion q, Vector3 v, bool driven)
        {
            w.U8(State); w.VarU32(net); w.VarU32(epoch); w.U16(seq);
            w.U32((uint)(long)Math.Round(stamp * 1000.0));
            w.I32(Mm(g.x)); w.I32(Mm(g.y)); w.I32(Mm(g.z));
            WriteRot(w, q);
            w.U16((ushort)Cm(v.x)); w.U16((ushort)Cm(v.y)); w.U16((ushort)Cm(v.z));
            w.U8((byte)(driven ? 1 : 0));
        }

        internal static void ReadStateHead(NetReader r, out uint net, out uint epoch, out double stamp, out Vector3d g, out Quaternion q, out Vector3 v, out bool driven)
        {
            net = r.VarU32(); epoch = r.VarU32(); r.U16();
            stamp = r.U32() / 1000.0;
            g = new Vector3d(r.I32() / 1000.0, r.I32() / 1000.0, r.I32() / 1000.0);
            q = ReadRot(r);
            v = new Vector3((short)r.U16() / 100f, (short)r.U16() / 100f, (short)r.U16() / 100f);
            driven = (r.U8() & 1) != 0;
        }

        private static int Mm(double x) => (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Round(x * 1000.0)));
        private static short Cm(float x) => (short)Mathf.Clamp(Mathf.RoundToInt(x * 100f), -32767, 32767);

        private const float RotScale = 32767f / 0.70710678f;

        private static void WriteRot(NetWriter w, Quaternion q)
        {
            float x = q.x, y = q.y, z = q.z, ww = q.w;
            float ax = Mathf.Abs(x), ay = Mathf.Abs(y), az = Mathf.Abs(z), aw = Mathf.Abs(ww);
            int big = 3; float m = aw;
            if (ax > m) { big = 0; m = ax; }
            if (ay > m) { big = 1; m = ay; }
            if (az > m) { big = 2; }
            float s = (big == 0 ? x : big == 1 ? y : big == 2 ? z : ww) < 0f ? -1f : 1f;   // q and -q are the same rotation
            w.U8((byte)big);
            if (big != 0) w.U16((ushort)(short)Mathf.Clamp(Mathf.RoundToInt(x * s * RotScale), -32767, 32767));
            if (big != 1) w.U16((ushort)(short)Mathf.Clamp(Mathf.RoundToInt(y * s * RotScale), -32767, 32767));
            if (big != 2) w.U16((ushort)(short)Mathf.Clamp(Mathf.RoundToInt(z * s * RotScale), -32767, 32767));
            if (big != 3) w.U16((ushort)(short)Mathf.Clamp(Mathf.RoundToInt(ww * s * RotScale), -32767, 32767));
        }

        private static Quaternion ReadRot(NetReader r)
        {
            int big = r.U8() & 3;
            float a = (short)r.U16() / RotScale, b = (short)r.U16() / RotScale, c = (short)r.U16() / RotScale;
            float d = Mathf.Sqrt(Mathf.Max(0f, 1f - a * a - b * b - c * c));
            switch (big)
            {
                case 0: return new Quaternion(d, a, b, c);
                case 1: return new Quaternion(a, d, b, c);
                case 2: return new Quaternion(a, b, d, c);
                default: return new Quaternion(a, b, c, d);
            }
        }
    }
}
