using System;

namespace TLDRevamp.Net
{
    internal static class MarkWire
    {
        internal const int Limit = 16;
        internal static void Request(NetWriter w, string note, uint[] nets) { w.U8(92); w.Str(note); Write(w, nets); }
        internal static bool Request(NetReader r, out string note, out uint[] nets)
        {
            note = r.Str(); return Read(r, out nets) && !r.Bad && note.Length <= 500;
        }
        internal static void Broadcast(NetWriter w, string note, double st, int origin, bool self, uint[] nets)
        { w.U8(92); w.Str(note); w.F64(st); w.I32(origin); w.U8(self ? (byte)1 : (byte)0); Write(w, nets); }
        internal static bool Broadcast(NetReader r, out string note, out double st, out int origin, out byte self, out uint[] nets)
        {
            note = r.Str(); st = r.F64(); origin = r.I32(); self = r.U8();
            return Read(r, out nets) && !r.Bad && !double.IsNaN(st) && !double.IsInfinity(st) && st >= 0 && origin >= 0 && self <= 1 && note.Length <= 500;
        }
        internal static void Write(NetWriter w, uint[] nets)
        {
            if (nets.Length > Limit) throw new ArgumentOutOfRangeException(nameof(nets));
            w.U8((byte)nets.Length); foreach (uint net in nets) w.U32(net);
        }
        internal static bool Read(NetReader r, out uint[] nets)
        {
            nets = Array.Empty<uint>(); int count = r.U8();
            if (r.Bad || count > Limit || r.Remaining != count * 4) return false;
            var ids = new uint[count];
            for (int i = 0; i < count; i++) ids[i] = r.U32();
            if (r.Bad || r.Remaining != 0) return false;
            nets = ids; return true;
        }
    }
}
