using System;
using System.Text;

namespace TLDRevamp.Net
{
    /// Compact little-endian message writer. One instance is reused for every send (no garbage per message).
    public sealed class NetWriter
    {
        public byte[] Buf = new byte[1024];
        public int Len;

        public NetWriter Reset() { Len = 0; return this; }

        private void Ensure(int n)
        {
            if (Len + n <= Buf.Length) return;
            var b = new byte[Math.Max(Buf.Length * 2, Len + n)];
            Buffer.BlockCopy(Buf, 0, b, 0, Len);
            Buf = b;
        }

        public void U8(byte v) { Ensure(1); Buf[Len++] = v; }
        public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);
        public void U16(ushort v) { Ensure(2); Buf[Len++] = (byte)v; Buf[Len++] = (byte)(v >> 8); }
        public void U32(uint v) { Ensure(4); for (int i = 0; i < 4; i++) Buf[Len++] = (byte)(v >> (8 * i)); }
        public void I32(int v) => U32((uint)v);
        public void U64(ulong v) { Ensure(8); for (int i = 0; i < 8; i++) Buf[Len++] = (byte)(v >> (8 * i)); }
        public void I64(long v) => U64((ulong)v);
        public void F32(float v) => I32(BitConverter.SingleToInt32Bits(v));
        public void F64(double v) => I64(BitConverter.DoubleToInt64Bits(v));

        /// Unsigned LEB128: 1 byte below 128, 2 below 16384, …
        public void VarU32(uint v)
        {
            Ensure(5);
            while (v >= 0x80) { Buf[Len++] = (byte)(v | 0x80); v >>= 7; }
            Buf[Len++] = (byte)v;
        }

        /// Zig-zag varint: small magnitudes of either sign stay short.
        public void VarI32(int v) => VarU32((uint)((v << 1) ^ (v >> 31)));

        public void Str(string s)
        {
            if (s == null) { VarU32(0); return; }
            int n = Encoding.UTF8.GetByteCount(s);
            VarU32((uint)n + 1);
            Ensure(n);
            Encoding.UTF8.GetBytes(s, 0, s.Length, Buf, Len);
            Len += n;
        }

        public void Bytes(byte[] src, int off, int n) { Ensure(n); Buffer.BlockCopy(src, off, Buf, Len, n); Len += n; }
    }

    /// Reader over a received message. Reading past the end sets Bad and returns zeros (a malformed message from a peer
    /// must never throw into the game loop).
    public sealed class NetReader
    {
        public byte[] Buf = new byte[1024];
        public int Pos, End;
        public bool Bad;

        public void Load(int length)
        {
            if (Buf.Length < length) Buf = new byte[Math.Max(length, Buf.Length * 2)];
            Pos = 0; End = length; Bad = false;
        }

        public int Remaining => End - Pos;

        private bool Has(int n) { if (Pos + n <= End) return true; Bad = true; Pos = End; return false; }

        public byte U8() => Has(1) ? Buf[Pos++] : (byte)0;
        public bool Bool() => U8() != 0;
        public ushort U16() { if (!Has(2)) return 0; ushort v = (ushort)(Buf[Pos] | (Buf[Pos + 1] << 8)); Pos += 2; return v; }
        public uint U32() { if (!Has(4)) return 0; uint v = 0; for (int i = 0; i < 4; i++) v |= (uint)Buf[Pos++] << (8 * i); return v; }
        public int I32() => (int)U32();
        public ulong U64() { if (!Has(8)) return 0; ulong v = 0; for (int i = 0; i < 8; i++) v |= (ulong)Buf[Pos++] << (8 * i); return v; }
        public long I64() => (long)U64();
        public float F32() => BitConverter.Int32BitsToSingle(I32());
        public double F64() => BitConverter.Int64BitsToDouble(I64());

        public uint VarU32()
        {
            uint v = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (!Has(1)) return 0;
                byte b = Buf[Pos++];
                v |= (uint)(b & 0x7F) << shift;
                if (b < 0x80) return v;
            }
            Bad = true;
            return 0;
        }

        public int VarI32() { uint u = VarU32(); return (int)(u >> 1) ^ -(int)(u & 1); }

        public string Str()
        {
            uint n = VarU32();
            if (n == 0) return null;
            n--;
            if (!Has((int)n)) return null;
            var s = Encoding.UTF8.GetString(Buf, Pos, (int)n);
            Pos += (int)n;
            return s;
        }
    }
}
