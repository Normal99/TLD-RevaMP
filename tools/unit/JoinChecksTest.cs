using System; using System.Net; using System.Net.Sockets; using TLDRevamp.Net;
static class P {
  static int fails = 0;
  static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }
  static ushort FreePort() { using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) { s.Bind(new IPEndPoint(IPAddress.Any, 0)); return (ushort)((IPEndPoint)s.LocalEndPoint).Port; } }
  static int Main() {
    // Hello refusals: each cause says what it is
    Check("same build: admitted", JoinChecks.HelloRefusal(false, 31, "0.69.1", 31, "0.69.1") == null);
    Check("source identity decides across version labels", JoinChecks.HelloRefusal(false, 36, "0.69.1", 36, "0.69.1-dev.abcdef0", true) == null);
    Check("source identity still requires protocol", JoinChecks.HelloRefusal(false, 35, "0.69.1", 36, "0.69.1-dev.abcdef0", true).Contains("protocol 36"));
    string v = JoinChecks.HelloRefusal(false, 31, "0.68.4", 31, "0.69.1");
    Check("other version: names both versions", v != null && v.Contains("0.68.4") && v.Contains("same version"));
    string p = JoinChecks.HelloRefusal(false, 30, "0.69.1", 31, "0.69.1");
    Check("same version, other protocol: names both protocols", p != null && p.Contains("protocol 30") && p.Contains("protocol 31") && p.Contains("same build") && !p.Contains("need the same version"));
    string b = JoinChecks.HelloRefusal(true, 31, "0.69.1", 31, "0.69.1");
    Check("unreadable Hello: says so", b != null && b.Contains("couldn't read"));
    Check("unreadable, no version at all", JoinChecks.HelloRefusal(true, 0, null, 31, "0.69.1").Contains("couldn't read"));
    Check("version wins over protocol", JoinChecks.HelloRefusal(false, 20, "0.60.0", 31, "0.69.1").Contains("same version"));
    foreach (ushort previous in new ushort[] { 40, 41, 42, 43, 44 })
      Check("protocol 45 refuses previous wire " + previous, JoinChecks.HelloRefusal(false, previous, "same", 45, "same", true).Contains("protocol 45"));
    Check("protocol 45 admits matching peer", JoinChecks.HelloRefusal(false, 45, "same", 45, "same", true) == null);
    // the port: free, held on IPv4, held on IPv6 (what Steam's first listen socket takes)
    ushort port = FreePort();
    Check("free port: not taken", !JoinChecks.PortTaken(port));
    using (var s4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) {
      s4.Bind(new IPEndPoint(IPAddress.Any, port));
      Check("IPv4 holder: taken", JoinChecks.PortTaken(port));
    }
    Check("released: free again", !JoinChecks.PortTaken(port));
    if (Socket.OSSupportsIPv6) {
      using (var s6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)) {
        s6.DualMode = true; s6.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        Check("IPv6 dual-stack holder: taken", JoinChecks.PortTaken(port));
      }
    }
    Check("probe leaves the port free", !JoinChecks.PortTaken(port));
    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    return fails == 0 ? 0 : 1;
  }
}
