using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Anchored millimetre poses let a twitch send just its limbs; Frame keeps the float fallback.
    internal static class RagdollWire
    {
        public const byte Frame = 0, Key = 1, Delta = 2;
        public const float MaxOffsetM = 32.767f;

        public static bool Fits(Vector3 p) => Finite(p) && Mathf.Abs(p.x) <= MaxOffsetM && Mathf.Abs(p.y) <= MaxOffsetM && Mathf.Abs(p.z) <= MaxOffsetM;
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static bool Finite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);

        public static void WriteNone(NetWriter w) => w.U8(0);

        public static void WriteFrame(NetWriter w, Vector3[] pos, Quaternion[] rot)
        {
            CheckArrays(pos, rot);
            w.U8((byte)pos.Length); w.U8(Frame);
            for (int i = 0; i < pos.Length; i++)
            {
                w.F32(pos[i].x); w.F32(pos[i].y); w.F32(pos[i].z); WriteRot(w, rot[i]);
            }
        }

        public static void WriteKey(NetWriter w, byte anchor, ushort hash, Vector3[] pos, Quaternion[] rot)
        {
            CheckArrays(pos, rot);
            w.U8((byte)pos.Length); w.U8(Key); w.U8(anchor); w.U16(hash);
            for (int i = 0; i < pos.Length; i++) WritePose(w, pos[i], rot[i]);
        }

        public static void WriteDelta(NetWriter w, byte anchor, ulong mask, Vector3[] pos, Quaternion[] rot)
        {
            CheckArrays(pos, rot);
            int n = pos.Length;
            mask &= All(n);
            w.U8((byte)n); w.U8(Delta); w.U8(anchor);
            for (int i = 0; i < (n + 7) / 8; i++) w.U8((byte)(mask >> (i * 8)));
            for (int i = 0; i < n; i++) if ((mask & (1UL << i)) != 0) WritePose(w, pos[i], rot[i]);
        }

        public static int DeltaSize(int n, ulong mask)
        {
            CheckCount(n);
            mask &= All(n);
            int size = 3 + (n + 7) / 8;
            while (mask != 0) { size += 13; mask &= mask - 1; }
            return size;
        }

        public static int KeySize(int n) { CheckCount(n); return 5 + 13 * n; }
        private static ulong All(int n) => (1UL << n) - 1;
        private static void CheckCount(int n) { if (n < 1 || n > 63) throw new ArgumentOutOfRangeException(nameof(n)); }
        private static void CheckArrays(Vector3[] pos, Quaternion[] rot)
        {
            if (pos == null || rot == null || pos.Length != rot.Length) throw new ArgumentException("Limb pose arrays must have matching lengths.");
            CheckCount(pos.Length);
        }

        /// Clamp before converting to an integer so even an unchecked huge offset cannot wrap.
        private static short Mm(float v) => (short)Math.Round(Math.Max(short.MinValue, Math.Min(short.MaxValue, (double)v * 1000)));
        private static void WritePose(NetWriter w, Vector3 p, Quaternion q)
        {
            w.U16((ushort)Mm(p.x)); w.U16((ushort)Mm(p.y)); w.U16((ushort)Mm(p.z)); WriteRot(w, q);
        }

        public sealed class Block
        {
            public byte Mode, Anchor;
            public ushort Hash;
            public int N;
            public ulong Mask;
            public Vector3[] Pos;
            public Quaternion[] Rot;
        }

        /// A false result invalidates the whole block. Sparse reads leave absent limbs alone for the caller's baseline.
        public static bool Read(NetReader r, Block into)
        {
            int n = r.U8();
            if (r.Bad || n == 0) return false;
            if (n > 63) { r.Bad = true; return false; }
            byte mode = r.U8();
            if (r.Bad) return false;
            if (mode > Delta) { r.Bad = true; return false; }
            byte anchor = 0; ushort hash = 0;
            ulong mask = All(n);
            if (mode == Key) { anchor = r.U8(); hash = r.U16(); }
            else if (mode == Delta)
            {
                anchor = r.U8(); mask = 0;
                for (int i = 0; i < (n + 7) / 8; i++) mask |= (ulong)r.U8() << (i * 8);
                if ((mask & ~All(n)) != 0) { r.Bad = true; return false; }
            }
            if (r.Bad) return false;
            if (into.Pos == null || into.Pos.Length < n) into.Pos = new Vector3[n];
            if (into.Rot == null || into.Rot.Length < n) into.Rot = new Quaternion[n];
            into.N = n; into.Mode = mode; into.Anchor = anchor; into.Hash = hash; into.Mask = mask;
            for (int i = 0; i < n; i++)
            {
                if ((mask & (1UL << i)) == 0) continue;
                var p = mode == Frame ? new Vector3(r.F32(), r.F32(), r.F32()) :
                    new Vector3((short)r.U16() / 1000f, (short)r.U16() / 1000f, (short)r.U16() / 1000f);
                var q = ReadRot(r);
                if (r.Bad || !Finite(p) || !Finite(q.x) || !Finite(q.y) || !Finite(q.z) || !Finite(q.w)) return false;
                into.Pos[i] = p; into.Rot[i] = q;
            }
            return !r.Bad;
        }

        /// FNV-1a steps: count, each UTF-16 code unit, then zero after each name; fold high/low halves.
        /// Explicit unchecked arithmetic keeps hierarchy identity stable across runtimes and checked builds.
        public static ushort HierarchyHash(string[] names)
        {
            unchecked
            {
                uint h = (2166136261u ^ (uint)names.Length) * 16777619u;
                foreach (var name in names)
                {
                    foreach (char c in name) h = (h ^ c) * 16777619u;
                    h *= 16777619u;
                }
                return (ushort)(h ^ (h >> 16));
            }
        }

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
