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
        /// Flags byte: 1 = driven (the owner's player in its driver seat), 2 = stored (in the owner's inventory: the game
        /// hides the item there — pickupable.disableThisWhenStored — and parks it at the player's inventory point),
        /// 4 = held (in the owner's player's hands: picked up or in the right hand), 8 = spraying (a spray can firing:
    /// Entities.Spray shows the mist and the hiss on the copies), 16 = carried: held by a player seated in a car —
    /// followed by that car's net id and the pose in the car's frame (Entities.Carried).
        internal static void WriteStateHead(NetWriter w, uint net, uint epoch, ushort seq, double stamp, Vector3d g, Quaternion q, Vector3 v, bool driven, bool stored = false, bool held = false, bool spraying = false,
                                            uint carriedBy = 0, Vector3 localPos = default, Quaternion localRot = default)
        {
            w.U8(State); w.VarU32(net); w.VarU32(epoch); w.U16(seq);
            w.U32((uint)(long)Math.Round(stamp * 1000.0));
            w.I32(Mm(g.x)); w.I32(Mm(g.y)); w.I32(Mm(g.z));
            WriteRot(w, q);
            w.U16((ushort)Cm(v.x)); w.U16((ushort)Cm(v.y)); w.U16((ushort)Cm(v.z));
            w.U8((byte)((driven ? 1 : 0) | (stored ? 2 : 0) | (held ? 4 : 0) | (spraying ? 8 : 0) | (carriedBy != 0 ? 16 : 0)));
            if (carriedBy != 0) { w.VarU32(carriedBy); w.F32(localPos.x); w.F32(localPos.y); w.F32(localPos.z); WriteRot(w, localRot); }
        }

        /// The carried part of the last state head read (flag 16): the car, the pose in its frame; 0 = not carried.
        internal static uint ReadCarriedBy; internal static Vector3 ReadLocalPos; internal static Quaternion ReadLocalRot;

        internal static void ReadStateHead(NetReader r, out uint net, out uint epoch, out double stamp, out Vector3d g, out Quaternion q, out Vector3 v, out bool driven, out bool stored, out bool held, out bool spraying)
        {
            net = r.VarU32(); epoch = r.VarU32(); r.U16();
            stamp = r.U32() / 1000.0;
            g = new Vector3d(r.I32() / 1000.0, r.I32() / 1000.0, r.I32() / 1000.0);
            q = ReadRot(r);
            v = new Vector3((short)r.U16() / 100f, (short)r.U16() / 100f, (short)r.U16() / 100f);
            byte fl = r.U8();
            driven = (fl & 1) != 0; stored = (fl & 2) != 0; held = (fl & 4) != 0; spraying = (fl & 8) != 0;
            ReadCarriedBy = 0;
            if ((fl & 16) != 0) { ReadCarriedBy = r.VarU32(); ReadLocalPos = new Vector3(r.F32(), r.F32(), r.F32()); ReadLocalRot = ReadRot(r); }
            if (ReadCarriedBy != 0 && (!Finite(ReadLocalPos) || ReadLocalPos.sqrMagnitude > 1e4f)) ReadCarriedBy = 0;   // the world pose is used
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
