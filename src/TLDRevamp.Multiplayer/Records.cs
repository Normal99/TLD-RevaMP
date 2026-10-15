using System;
using System.Text;
using UnityEngine;

namespace TLDRevamp.Multiplayer
{
    /// Writes a record: the fields of a shared object's state, in an order the mod chooses. The reader must read the
    /// same fields in the same order. Little-endian, no boxing, no reflection.
    public sealed class RecordWriter
    {
        internal byte[] Buf;
        public int Length { get; internal set; }

        public RecordWriter(int capacity = 256) { Buf = new byte[Math.Max(16, capacity)]; }

        public void Clear() => Length = 0;

        public byte[] ToArray()
        {
            var a = new byte[Length];
            Buffer.BlockCopy(Buf, 0, a, 0, Length);
            return a;
        }

        private void Ensure(int n)
        {
            if (Length + n <= Buf.Length) return;
            int size = Buf.Length;
            while (size < Length + n) size *= 2;
            Array.Resize(ref Buf, size);
        }

        public void Byte(byte v) { Ensure(1); Buf[Length++] = v; }
        public void Bool(bool v) => Byte(v ? (byte)1 : (byte)0);
        public void UShort(ushort v) { Ensure(2); Buf[Length++] = (byte)v; Buf[Length++] = (byte)(v >> 8); }
        public void UInt(uint v) { Ensure(4); for (int i = 0; i < 4; i++) Buf[Length++] = (byte)(v >> (8 * i)); }
        public void Int(int v) => UInt((uint)v);
        public void ULong(ulong v) { Ensure(8); for (int i = 0; i < 8; i++) Buf[Length++] = (byte)(v >> (8 * i)); }
        public void Long(long v) => ULong((ulong)v);
        public void Float(float v) => Int(BitConverter.SingleToInt32Bits(v));
        public void Double(double v) => Long(BitConverter.DoubleToInt64Bits(v));

        /// 1 byte for 0–127, up to 5 for the rest: counts, indices, small ids.
        public void VarUInt(uint v)
        {
            while (v >= 0x80) { Byte((byte)(v | 0x80)); v >>= 7; }
            Byte((byte)v);
        }
        public void VarInt(int v) => VarUInt((uint)((v << 1) ^ (v >> 31)));

        /// UTF-8, length first. Null is written as "".
        public void String(string s)
        {
            if (string.IsNullOrEmpty(s)) { VarUInt(0); return; }
            int n = Encoding.UTF8.GetByteCount(s);
            VarUInt((uint)n);
            Ensure(n);
            Encoding.UTF8.GetBytes(s, 0, s.Length, Buf, Length);
            Length += n;
        }

        public void Vector3(Vector3 v) { Float(v.x); Float(v.y); Float(v.z); }
        public void Quaternion(Quaternion q) { Float(q.x); Float(q.y); Float(q.z); Float(q.w); }
        public void Color(Color c) { Float(c.r); Float(c.g); Float(c.b); Float(c.a); }

        public void Bytes(byte[] src) => Bytes(src, 0, src?.Length ?? 0);
        public void Bytes(byte[] src, int offset, int count)
        {
            VarUInt((uint)count);
            if (count == 0) return;
            Ensure(count);
            Buffer.BlockCopy(src, offset, Buf, Length, count);
            Length += count;
        }
    }

    /// Reads a record written by a RecordWriter. Reading past the end, or a length that doesn't fit, never throws: it
    /// returns defaults and sets Bad. A mod should check Bad (or IsValid) before it applies anything.
    public sealed class RecordReader
    {
        private readonly byte[] _buf;
        private readonly int _end;
        public int Position { get; private set; }
        public bool Bad { get; private set; }
        public int Remaining => _end - Position;
        /// Nothing read past the end, and every float so far was a real number.
        public bool IsValid => !Bad;

        /// Longest string a record may carry (UTF-8 bytes); longer ones are Bad.
        public int MaxStringBytes = 4096;

        public RecordReader(byte[] data) : this(data, 0, data?.Length ?? 0) { }
        public RecordReader(byte[] data, int offset, int count)
        {
            _buf = data ?? Array.Empty<byte>();
            Position = Math.Max(0, offset);
            _end = Math.Min(_buf.Length, Position + Math.Max(0, count));
        }

        private bool Has(int n)
        {
            if (Position + n <= _end) return true;
            Bad = true; Position = _end;
            return false;
        }

        public byte Byte() => Has(1) ? _buf[Position++] : (byte)0;
        public bool Bool() => Byte() != 0;
        public ushort UShort() { if (!Has(2)) return 0; ushort v = (ushort)(_buf[Position] | (_buf[Position + 1] << 8)); Position += 2; return v; }
        public uint UInt() { if (!Has(4)) return 0; uint v = 0; for (int i = 0; i < 4; i++) v |= (uint)_buf[Position++] << (8 * i); return v; }
        public int Int() => (int)UInt();
        public ulong ULong() { if (!Has(8)) return 0; ulong v = 0; for (int i = 0; i < 8; i++) v |= (ulong)_buf[Position++] << (8 * i); return v; }
        public long Long() => (long)ULong();

        /// A NaN or an infinity marks the record Bad and reads as 0: a NaN placed on a transform hides the object,
        /// and in physics it spreads to everything it touches.
        public float Float()
        {
            float f = BitConverter.Int32BitsToSingle(Int());
            if (float.IsNaN(f) || float.IsInfinity(f)) { Bad = true; return 0f; }
            return f;
        }
        public double Double()
        {
            double d = BitConverter.Int64BitsToDouble(Long());
            if (double.IsNaN(d) || double.IsInfinity(d)) { Bad = true; return 0d; }
            return d;
        }

        public uint VarUInt()
        {
            uint v = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte b = Byte();
                if (Bad) return 0;
                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return v;
            }
            Bad = true;
            return 0;
        }
        public int VarInt() { uint u = VarUInt(); return (int)(u >> 1) ^ -(int)(u & 1); }

        public string String()
        {
            uint n = VarUInt();
            if (n == 0) return "";
            if (n > MaxStringBytes || !Has((int)n)) { Bad = true; return ""; }
            string s = Encoding.UTF8.GetString(_buf, Position, (int)n);
            Position += (int)n;
            return s;
        }

        public Vector3 Vector3() => new Vector3(Float(), Float(), Float());
        public Quaternion Quaternion()
        {
            var q = new Quaternion(Float(), Float(), Float(), Float());
            float m = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (m < 1e-6f) return UnityEngine.Quaternion.identity;
            float k = 1f / Mathf.Sqrt(m);
            return new Quaternion(q.x * k, q.y * k, q.z * k, q.w * k);
        }
        public Color Color() => new Color(Float(), Float(), Float(), Float());

        /// At most maxCount bytes; a longer block is Bad.
        public byte[] Bytes(int maxCount = 65536)
        {
            uint n = VarUInt();
            if (n == 0) return Array.Empty<byte>();
            if (n > maxCount || !Has((int)n)) { Bad = true; return Array.Empty<byte>(); }
            var a = new byte[n];
            Buffer.BlockCopy(_buf, Position, a, 0, (int)n);
            Position += (int)n;
            return a;
        }
    }
}
