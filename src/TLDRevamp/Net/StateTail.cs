using System;

namespace TLDRevamp.Net
{
    /// Transport cursors must size the whole tail before either attribution or anchor replay trusts it.
    internal static class StateTail
    {
        internal enum Mode : byte { Frame = 0, Key = 1, Delta = 2, None = 255 }
        internal struct Block
        {
            public uint Net, Epoch;
            public int Offset, Count;
            public Mode Mode;
            public byte Anchor;
        }

        internal static bool TryRead(byte[] bytes, out Block block) => TryRead(bytes, 0, bytes == null ? 0 : bytes.Length, out block);

        internal static bool TryRead(byte[] bytes, int offset, int length, out Block block)
        {
            block = default;
            if (bytes == null || offset < 0 || length <= 0 || offset > bytes.Length - length) return false;
            var r = new Scan { Buf = bytes, Pos = offset, End = offset + length };
            if (r.U8() != Entities.State) return false;
            block.Net = r.Var(); block.Epoch = r.Var(); r.Skip(31);
            byte flags = r.U8();
            if ((flags & 16) != 0)
            {
                r.Var(); r.Skip(19);
                if ((flags & 64) != 0) { r.Var(); r.Var(); }
            }
            else if ((flags & 64) != 0 && r.Var() != 0) { r.Var(); r.Var(); r.Skip(19); }
            if ((flags & 128) != 0) r.Skip(13);
            int wheels = r.U8(); r.Skip((wheels & 127) * ((wheels & 128) != 0 ? 6 : 4));
            if ((r.U8() & 1) != 0) r.Skip(2);
            if (r.Pos < r.End && (r.U8() & 1) != 0) r.Skip(3); // gear, horn, steer (Entities.CarSignals); bots may omit signals and limbs
            block.Offset = r.Pos; block.Mode = Mode.None;
            if (r.Bad) return false;
            if (r.Pos == r.End) return true;
            int n = r.U8(); block.Count = n;
            if (n == 0) return r.Pos == r.End;
            if (n > 63) return false;
            byte mode = r.U8();
            if (r.Bad || mode > (byte)Mode.Delta) return false;
            block.Mode = (Mode)mode;
            if (block.Mode == Mode.Frame)
            {
                for (int i = 0; i < n && !r.Bad; i++) { Float(ref r, false); Float(ref r, false); Float(ref r, false); r.Skip(7); }
            }
            else if (block.Mode == Mode.Key)
            {
                block.Anchor = r.U8(); r.Skip(2);
                Float(ref r, true); Float(ref r, true); Float(ref r, true);
                r.Skip(7 + n * 13);
            }
            else
            {
                block.Anchor = r.U8();
                int poses = 0;
                for (int i = 0; i < (n + 7) / 8 && !r.Bad; i++)
                {
                    int mask = r.U8(), bits = Math.Min(8, n - i * 8);
                    if ((mask >> bits) != 0) return false;
                    while (mask != 0) { poses++; mask &= mask - 1; }
                }
                r.Skip(poses * 13);
            }
            return !r.Bad && r.Pos == r.End;
        }

        /// Non-finite anchors/Frame positions cannot become a replay baseline. BitConverter reads without copying.
        private static void Float(ref Scan r, bool wide)
        {
            int at = r.Pos; r.Skip(wide ? 8 : 4);
            if (r.Bad) return;
            double value = wide ? BitConverter.ToDouble(r.Buf, at) : BitConverter.ToSingle(r.Buf, at);
            if (double.IsNaN(value) || double.IsInfinity(value)) r.Bad = true;
        }

        /// Cumulative Deltas need exactly their Key; an unmatched Delta must never be replayed alone.
        internal static bool ReplayKey(byte[] state, byte[] key, uint epoch, out byte[] before)
        {
            before = null;
            if (state == null || state.Length == 0) return false;
            if (state[0] == Entities.CompactState) return true;
            if (!TryRead(state, out var tail) || tail.Epoch != epoch) return false;
            if (tail.Mode != Mode.Delta) return true;
            if (!TryRead(key, out var anchor) || anchor.Mode != Mode.Key || anchor.Epoch != epoch ||
                anchor.Net != tail.Net || anchor.Count != tail.Count || anchor.Anchor != tail.Anchor) return false;
            before = key; return true;
        }

        /// An independent cursor over transport bytes: no copy, handler side effects, strings or throwing reads.
        internal struct Scan
        {
            public byte[] Buf; public int Pos, End; public bool Bad;
            public void Skip(int n) { if (n < 0 || n > End - Pos) { Bad = true; Pos = End; } else Pos += n; }
            public byte U8() { if (Pos >= End) { Bad = true; return 0; } return Buf[Pos++]; }
            public int U16() { int a = U8(); return a | U8() << 8; }
            public uint U32() { uint a = (uint)U16(); return a | (uint)U16() << 16; }
            public uint Var()
            {
                uint n = 0;
                for (int s = 0; s < 35; s += 7)
                {
                    byte b = U8();
                    if (s == 28 && b > 15) { Bad = true; return 0; }
                    n |= (uint)(b & 127) << s;
                    if (b < 128) return n;
                }
                Bad = true; return 0;
            }
            public void Bytes() { uint n = Var(); if (n > int.MaxValue) Bad = true; else Skip((int)n); }
            public void Str() { uint n = Var(); if (n > int.MaxValue) Bad = true; else if (n > 0) Skip((int)n - 1); }
            public void Records()
            {
                int n = U16();
                for (int i = 0; i < n && !Bad; i++) { Var(); Bytes(); }
            }
        }
    }
}
