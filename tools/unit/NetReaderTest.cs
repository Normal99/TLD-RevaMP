using System; using TLDRevamp.Net;
static class P {
  static int fails = 0;
  static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }
  static NetReader R(byte[] b) { var r = new NetReader(); r.Load(b.Length); Array.Copy(b, r.Buf, b.Length); return r; }
  static int Main() {
    var w = new NetWriter(); w.U8(7); w.U16(500); w.U32(123456); w.VarU32(300); w.Str("héllo"); w.F32(1.5f); w.U64(99);
    var r = R(w.Buf[..w.Len]);
    Check("valid round trip", r.U8()==7 && r.U16()==500 && r.U32()==123456 && r.VarU32()==300 && r.Str()=="héllo" && r.F32()==1.5f && r.U64()==99 && !r.Bad && r.Remaining==0);
    // a string whose length prefix is huge (n-1 = 0xFFFFFFFE -> negative as int)
    var w2 = new NetWriter(); w2.VarU32(0xFFFFFFFF); w2.U8(1); w2.U8(2);
    var r2 = R(w2.Buf[..w2.Len]); string s2 = null; bool threw = false;
    try { s2 = r2.Str(); } catch { threw = true; }
    Check("huge string length: no throw, Bad, null", !threw && r2.Bad && s2 == null);
    // length int.MaxValue - 1 (Pos + n overflows)
    var w3 = new NetWriter(); w3.VarU32((uint)int.MaxValue); w3.U8(1);
    var r3 = R(w3.Buf[..w3.Len]); threw = false;
    try { r3.Str(); } catch { threw = true; }
    Check("overflowing length: no throw, Bad", !threw && r3.Bad);
    // reading past the end
    var r4 = R(new byte[] { 1 }); threw = false; uint v = 1;
    try { v = r4.U32(); } catch { threw = true; }
    Check("past the end: 0, Bad", !threw && v == 0 && r4.Bad);
    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    return fails == 0 ? 0 : 1;
  }
}
