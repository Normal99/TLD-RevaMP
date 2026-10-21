using System; using TLDRevamp.Net;
static class P {
  static int fails = 0;
  static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }
  static NetReader R(NetWriter w) { var r = new NetReader(); r.Load(w.Len); Array.Copy(w.Buf, r.Buf, w.Len); return r; }
  // the Hello tail after name/password/game build: the mod list (as ModObjects.WriteModList), then optionally the build hash
  static NetWriter Tail(string build) {
    var w = new NetWriter(); w.VarU32(1); w.Str("anonymoustld.mountanything"); w.Str("0.2.0"); w.Str("abcd"); w.VarU32(1); w.Str("mount.pose"); w.U8(2);
    if (build != null) w.Str(build);
    return w;
  }
  // the old host's read of the mod list (ModObjects.CheckModList): it stops there
  static void ReadModList(NetReader r) {
    uint n = r.VarU32(); for (uint i = 0; i < n; i++) { r.Str(); r.Str(); r.Str(); }
    uint nt = r.VarU32(); for (uint i = 0; i < nt; i++) { r.Str(); r.U8(); }
  }
  static int Main() {
    var r = R(Tail("0123456789ab")); ReadModList(r);
    Check("new joiner: hash read after the mod list", BuildCheck.Read(r) == "0123456789ab" && !r.Bad && r.Remaining == 0);
    r = R(Tail("0123456789ab")); ReadModList(r);
    Check("old host: mod list still reads, the hash is just left over", !r.Bad && r.Remaining > 0);
    r = R(Tail(null)); ReadModList(r);
    Check("old joiner: no field -> empty", BuildCheck.Read(r) == "" && !r.Bad);
    var w = Tail(null); w.VarU32(50); w.U8(1); r = R(w); ReadModList(r);
    Check("cut-off field -> empty, no throw", BuildCheck.Read(r) == "");
    Check("same", BuildCheck.Compare("0123456789ab", "0123456789ab") == BuildCheck.Result.Same);
    Check("differs", BuildCheck.Compare("0123456789ab", "ba9876543210") == BuildCheck.Result.Differs);
    Check("old joiner unknown", BuildCheck.Compare("", "ba9876543210") == BuildCheck.Result.Unknown);
    Check("joiner couldn't hash", BuildCheck.Compare("unknown", "ba9876543210") == BuildCheck.Result.Unknown);
    Check("host couldn't hash", BuildCheck.Compare("0123456789ab", "unknown") == BuildCheck.Result.Unknown);
    string a = new string('a', 40), b = new string('b', 40);
    string Id(string commit, bool dirty, string hash) => commit + ":" + (dirty ? "dirty" : "clean") + ":" + hash;
    Check("clean commit across SDKs", BuildCheck.Compare(Id(a, false, "123"), Id(a, false, "456")) == BuildCheck.Result.Same);
    Check("clean commit without DLL hash", BuildCheck.Compare(Id(a, false, "unknown"), Id(a, false, "456")) == BuildCheck.Result.Same);
    Check("different commits even with identical hash", BuildCheck.Compare(Id(a, false, "123"), Id(b, false, "123")) == BuildCheck.Result.Differs);
    foreach (bool hostDirty in new[] { false, true }) {
      Check("dirty differs " + hostDirty, BuildCheck.Compare(Id(a, true, "123"), Id(a, hostDirty, "456")) == BuildCheck.Result.Differs);
      Check("dirty identical DLL " + hostDirty, BuildCheck.Compare(Id(a, true, "123"), Id(a, hostDirty, "123")) == BuildCheck.Result.Same);
    }
    Check("dirty cannot hash", BuildCheck.Compare(Id(a, true, "unknown"), Id(a, true, "456")) == BuildCheck.Result.Unknown);
    Check("unknown commit falls back to DLL", BuildCheck.Compare(Id("unknown", false, "123"), Id(a, false, "123")) == BuildCheck.Result.Same);
    Check("unknown commit different DLL", BuildCheck.Compare(Id("unknown", false, "123"), Id(a, false, "456")) == BuildCheck.Result.Differs);
    string commitMessage = BuildCheck.Refusal("0.69.1", Id(a, false, "123"), Id(b, false, "456"));
    Check("refusal names both commits", commitMessage.Contains("aaaaaaa") && commitMessage.Contains("bbbbbbb"));
    Check("shown hash: hex only, capped", BuildCheck.Show("<x>DEADbeef</x>0123456789abcdef") == "deadbeef01234567" && BuildCheck.Show("") == "(none)" && BuildCheck.Show("<>") == "(unreadable)");
    string msg = BuildCheck.Refusal("0.69.0", "0123456789ab", "ba9876543210");
    Check("refusal names both builds", msg.Contains("0.69.0") && msg.Contains("0123456789ab") && msg.Contains("ba9876543210"));
    Check("unknown build, other version: refused", BuildCheck.RefuseUnknown(BuildCheck.Result.Unknown, "0.69.0", "0.69.1"));
    Check("unknown build, same version: announced only", !BuildCheck.RefuseUnknown(BuildCheck.Result.Unknown, "0.69.1", "0.69.1"));
    Check("known builds are not this check's", !BuildCheck.RefuseUnknown(BuildCheck.Result.Same, "0.69.0", "0.69.1"));
    BuildCheck.Refuse = false;
    Check("Refuse off: announced only", !BuildCheck.RefuseUnknown(BuildCheck.Result.Unknown, "0.69.0", "0.69.1"));
    BuildCheck.Refuse = true;
    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    return fails == 0 ? 0 : 1;
  }
}
