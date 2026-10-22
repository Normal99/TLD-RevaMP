using System;
using TLDRevamp.Net;
using UnityEngine;

static class RagdollWireTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static NetReader Reader(NetWriter w) => new NetReader { Buf = w.Buf, End = w.Len };
    static ulong All(int n) => (1UL << n) - 1;
    static Quaternion Neg(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, -q.w);
    static Quaternion Unit(double x, double y, double z, double w)
    {
        double length = Math.Sqrt(x * x + y * y + z * z + w * w);
        return new Quaternion((float)(x / length), (float)(y / length), (float)(z / length), (float)(w / length));
    }
    static double Angle(Quaternion a, Quaternion b)
    {
        double dot = (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w;
        double aa = (double)a.x * a.x + (double)a.y * a.y + (double)a.z * a.z + (double)a.w * a.w;
        double bb = (double)b.x * b.x + (double)b.y * b.y + (double)b.z * b.z + (double)b.w * b.w;
        return 2 * Math.Acos(Math.Min(1, Math.Abs(dot) / Math.Sqrt(aa * bb))) * 180 / Math.PI;
    }
    static bool Near(Vector3 a, Vector3 b, double tolerance) =>
        Math.Abs((double)a.x - b.x) <= tolerance && Math.Abs((double)a.y - b.y) <= tolerance && Math.Abs((double)a.z - b.z) <= tolerance;
    static void Poses(int n, out Vector3[] pos, out Quaternion[] rot)
    {
        pos = new Vector3[n]; rot = new Quaternion[n];
        var random = new System.Random(3701);
        for (int i = 0; i < n; i++)
        {
            pos[i] = new Vector3((float)(random.NextDouble() * 60 - 30), (float)(random.NextDouble() * 60 - 30), (float)(random.NextDouble() * 60 - 30));
            rot[i] = Unit(random.NextDouble() - .5, random.NextDouble() - .5, random.NextDouble() - .5, random.NextDouble() - .5);
        }
        pos[0] = new Vector3(RagdollWire.MaxOffsetM, -RagdollWire.MaxOffsetM, .00049f);
    }
    static void Write(NetWriter w, byte mode, Vector3[] pos, Quaternion[] rot, ulong mask)
    {
        if (mode == RagdollWire.Frame) RagdollWire.WriteFrame(w, pos, rot);
        else if (mode == RagdollWire.Key) RagdollWire.WriteKey(w, 255, 0xA17B, pos, rot);
        else RagdollWire.WriteDelta(w, 255, mask, pos, rot);
    }
    static void RoundTrips()
    {
        Poses(63, out var pos, out var rot);
        ulong sparse = 1UL | (1UL << 7) | (1UL << 8) | (1UL << 62);
        var w = new NetWriter();
        var into = new RagdollWire.Block { Pos = new Vector3[70], Rot = new Quaternion[70] };
        var reusedPos = into.Pos; var reusedRot = into.Rot;
        var sentinelPos = new Vector3(101, 102, 103); var sentinelRot = new Quaternion(1, 2, 3, 4);
        foreach (byte mode in new[] { RagdollWire.Frame, RagdollWire.Key, RagdollWire.Delta })
        foreach (bool negative in new[] { false, true })
        {
            var sentRot = Array.ConvertAll(rot, q => negative ? Neg(q) : q);
            for (int i = 0; i < 70; i++) { into.Pos[i] = sentinelPos; into.Rot[i] = sentinelRot; }
            w.Reset(); Write(w, mode, pos, sentRot, sparse);
            int size = w.Len; w.U8(91);
            var r = Reader(w);
            Check(RagdollWire.Read(r, into), "round trip reads");
            Check(into.N == 63 && into.Mode == mode && into.Mask == (mode == RagdollWire.Delta ? sparse : All(63)), "count/mode/mask");
            Check(into.Anchor == (mode == RagdollWire.Frame ? 0 : 255) && into.Hash == (mode == RagdollWire.Key ? 0xA17B : 0), "anchor/hash reset across modes");
            Check(ReferenceEquals(into.Pos, reusedPos) && ReferenceEquals(into.Rot, reusedRot), "oversized arrays reused");
            for (int i = 0; i < 70; i++)
            {
                bool present = i < 63 && (into.Mask & (1UL << i)) != 0;
                Check(Near(into.Pos[i], present ? pos[i] : sentinelPos, mode == RagdollWire.Frame ? 0 : .0005), "position precision / absent entries");
                Check(present ? Angle(into.Rot[i], sentRot[i]) < .1 : into.Rot[i].w == sentinelRot.w && into.Rot[i].x == sentinelRot.x && into.Rot[i].y == sentinelRot.y && into.Rot[i].z == sentinelRot.z,
                    "rotation precision / absent entries");
            }
            Check(r.Pos == size && r.U8() == 91 && !r.Bad, "reads exactly one block");
            if (mode == RagdollWire.Delta)
                Check(w.Buf[3] == 0x81 && w.Buf[4] == 1 && w.Buf[10] == 0x40, "mask bits 0,7,8,62 in LSB order");
        }
        for (int i = 0; i < 70; i++) { into.Pos[i] = sentinelPos; into.Rot[i] = sentinelRot; }
        w.Reset(); RagdollWire.WriteDelta(w, 17, 0, pos, rot);
        Check(RagdollWire.Read(Reader(w), into) && into.Mask == 0 && into.Anchor == 17 && w.Len == 11, "empty delta reads its header only");
        for (int i = 0; i < 70; i++)
            Check(Near(into.Pos[i], sentinelPos, 0) && into.Rot[i].x == 1 && into.Rot[i].y == 2 && into.Rot[i].z == 3 && into.Rot[i].w == 4, "empty delta leaves every pose alone");
        var grown = new RagdollWire.Block { Pos = new Vector3[1], Rot = new Quaternion[1] };
        w.Reset(); RagdollWire.WriteKey(w, 0, 1, pos, rot);
        Check(RagdollWire.Read(Reader(w), grown) && grown.Pos.Length >= 63 && grown.Rot.Length >= 63, "small arrays grow");
        grown = new RagdollWire.Block();
        Check(RagdollWire.Read(Reader(w), grown) && grown.Pos.Length >= 63 && grown.Rot.Length >= 63, "null arrays allocated");
        pos[0] = new Vector3(1e20f, -1000, 123.4567f);
        w.Reset(); RagdollWire.WriteFrame(w, pos, rot);
        Check(RagdollWire.Read(Reader(w), grown) && Near(pos[0], grown.Pos[0], 0), "Frame supports offsets outside compact range");
        Console.WriteLine("PASS Frame / Key / sparse Delta round trips, q and -q, array reuse");
    }
    static void Sizes()
    {
        var w = new NetWriter(); var into = new RagdollWire.Block();
        for (int n = 1; n <= 63; n++)
        {
            Poses(n, out var pos, out var rot);
            w.Reset(); RagdollWire.WriteKey(w, 11, 4321, pos, rot);
            Check(w.Len == RagdollWire.KeySize(n), "KeySize matches bytes");
            foreach (ulong mask in new[] { 0UL, 1UL << (n - 1), All(n), ulong.MaxValue })
            {
                w.Reset(); RagdollWire.WriteDelta(w, 11, mask, pos, rot);
                Check(w.Len == RagdollWire.DeltaSize(n, mask), "DeltaSize matches bytes");
                var r = Reader(w);
                Check(RagdollWire.Read(r, into) && into.Mask == (mask & All(n)) && r.Remaining == 0, "mask clipped to limb count, including empty");
            }
        }
        Poses(15, out var rabbitPos, out var rabbitRot);
        w.Reset(); RagdollWire.WriteFrame(w, rabbitPos, rabbitRot); int frame = w.Len;
        w.Reset(); RagdollWire.WriteKey(w, 1, 2, rabbitPos, rabbitRot); int key = w.Len;
        w.Reset(); RagdollWire.WriteDelta(w, 1, 1UL << 7, rabbitPos, rabbitRot); int delta = w.Len;
        Check(frame == 287 && key == 200 && delta == 18 && delta <= 1 + 1 + 1 + 2 + 13, "15-limb rabbit budget");
        Console.WriteLine("PASS sizes for all counts and empty/full masks; 15 limbs: Frame=" + frame + " Key=" + key + " Delta(1)=" + delta + " bytes");
    }
    static void Malformed()
    {
        var w = new NetWriter(); var into = new RagdollWire.Block();
        RagdollWire.WriteNone(w); var r = Reader(w);
        Check(w.Len == 1 && w.Buf[0] == 0 && !RagdollWire.Read(r, into) && !r.Bad && r.Pos == 1, "None is a single byte");
        Poses(63, out var pos, out var rot);
        foreach (byte mode in new[] { RagdollWire.Frame, RagdollWire.Key, RagdollWire.Delta })
        {
            w.Reset(); Write(w, mode, pos, rot, All(63));
            for (int length = 0; length < w.Len; length++)
            {
                var bytes = new byte[length]; Array.Copy(w.Buf, bytes, length);
                r = new NetReader { Buf = bytes, End = length };
                Check(!RagdollWire.Read(r, into) && r.Bad, "every truncated prefix rejected without throwing");
            }
        }
        w.Reset(); RagdollWire.WriteDelta(w, 1, 0, pos, rot);
        for (int length = 0; length < w.Len; length++)
            Check(!RagdollWire.Read(new NetReader { Buf = w.Buf, End = length }, into), "truncated empty delta");
        for (int mode = 3; mode <= 255; mode++)
            Check(!RagdollWire.Read(new NetReader { Buf = new byte[] { 1, (byte)mode }, End = 2 }, into), "unknown mode");
        for (int n = 64; n <= 255; n++)
            Check(!RagdollWire.Read(new NetReader { Buf = new byte[] { (byte)n }, End = 1 }, into), "invalid count");
        Check(!RagdollWire.Read(new NetReader { Buf = new byte[] { 1, 2, 1, 2 }, End = 4 }, into), "mask padding rejected");
        w.Reset(); RagdollWire.WriteKey(w, 1, 1, pos, rot); r = Reader(w); r.Bad = true;
        Check(!RagdollWire.Read(r, into), "preexisting Bad cannot succeed");
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        for (int axis = 0; axis < 3; axis++)
        {
            var p = new Vector3(axis == 0 ? bad : 0, axis == 1 ? bad : 0, axis == 2 ? bad : 0);
            w.Reset(); RagdollWire.WriteFrame(w, new[] { p }, new[] { new Quaternion(0, 0, 0, 1) });
            Check(!RagdollWire.Read(Reader(w), into), "non-finite position rejected");
        }
        Console.WriteLine("PASS None, all truncated prefixes, invalid modes/counts/padding, non-finite values");
    }
    static void RangeAndHash()
    {
        float limit = RagdollWire.MaxOffsetM;
        Check(RagdollWire.Fits(new Vector3(limit, -limit, 0)), "inclusive range edges");
        Check(RagdollWire.Fits(new Vector3(-limit, 0, limit)), "inclusive edges on all axes");
        for (int axis = 0; axis < 3; axis++)
        foreach (float bad in new[] { limit + .00001f, -limit - .00001f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Check(!RagdollWire.Fits(new Vector3(axis == 0 ? bad : 0, axis == 1 ? bad : 0, axis == 2 ? bad : 0)), "outside range or non-finite");
        var w = new NetWriter(); var into = new RagdollWire.Block();
        RagdollWire.WriteKey(w, 0, 0, new[] { new Vector3(float.MaxValue, -float.MaxValue, .00151f) }, new[] { new Quaternion(0, 0, 0, 1) });
        Check(RagdollWire.Read(Reader(w), into) && Near(into.Pos[0], new Vector3(32.767f, -32.768f, .002f), 0), "safety clamp and nearest millimetre");
        ushort hash = RagdollWire.HierarchyHash(new[] { "root", "ear.L", "ear.R" });
        Check(hash == 0xA638 && RagdollWire.HierarchyHash(new[] { "root", "ear.L", "ear.R" }) == hash, "fixed hierarchy hash literal");
        Check(RagdollWire.HierarchyHash(new[] { "root", "ear.R", "ear.L" }) != hash, "reordered names differ");
        Check(RagdollWire.HierarchyHash(new[] { "root", "ear.L", "tail" }) != hash, "renamed limb differs");
        Check(RagdollWire.HierarchyHash(new[] { "ab", "c" }) != RagdollWire.HierarchyHash(new[] { "a", "bc" }), "name separators matter");
        Check(RagdollWire.HierarchyHash(new[] { "\u0101" }) != RagdollWire.HierarchyHash(new[] { "\u0001" }), "full UTF-16 code units matter");
        Console.WriteLine("PASS Fits, millimetre rounding/clamping, stable hierarchy hash");
    }
    static void RotationBytes()
    {
        var w = new NetWriter();
        var cases = new[] { new Quaternion(1, 0, 0, 0), new Quaternion(0, 1, 0, 0), new Quaternion(0, 0, 1, 0), new Quaternion(0, 0, 0, 1), new Quaternion(.5f, .5f, .5f, .5f) };
        for (int i = 0; i < cases.Length; i++)
        foreach (var q in new[] { cases[i], Neg(cases[i]) })
        {
            w.Reset(); RagdollWire.WriteKey(w, 0, 0, new[] { new Vector3() }, new[] { q });
            Check(w.Buf[11] == (i == 4 ? 3 : i), "smallest-three dominant index and tie rule");
            for (int j = 0; j < 3; j++)
                Check(w.Buf[12 + j * 2] == (i == 4 ? 0x82 : 0) && w.Buf[13 + j * 2] == (i == 4 ? 0x5A : 0), "golden little-endian rotation bytes, q and -q");
        }
        var random = new System.Random(517);
        for (int i = 0; i < 1000; i++)
        {
            var q = Unit(random.NextDouble() - .5, random.NextDouble() - .5, random.NextDouble() - .5, random.NextDouble() - .5);
            w.Reset(); RagdollWire.WriteKey(w, 0, 0, new[] { new Vector3() }, new[] { q });
            var bytes = w.Buf.AsSpan(11, 7).ToArray();
            var into = new RagdollWire.Block();
            Check(RagdollWire.Read(Reader(w), into) && Angle(q, into.Rot[0]) < .1, "random rotation accuracy");
            w.Reset(); RagdollWire.WriteKey(w, 0, 0, new[] { new Vector3() }, new[] { Neg(q) });
            Check(w.Buf.AsSpan(11, 7).SequenceEqual(bytes), "q/-q byte identity");
        }
        Console.WriteLine("PASS rotation golden bytes, all dominant components, 1000 random q/-q pairs");
    }
    static int Main()
    {
        RoundTrips(); Sizes(); Malformed(); RangeAndHash(); RotationBytes();
        Console.WriteLine("ALL PASS (" + checks + " checks)");
        return 0;
    }
}
