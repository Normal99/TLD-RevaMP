using System.Net;
using System.Net.Sockets;

namespace TLDRevamp.Net
{
    /// What decides whether a join can work before anything of the world moves: the host's port, and the joiner's Hello.
    /// No game or Steam types, so tools/unit/JoinChecksTest.cs runs it offline.
    public static class JoinChecks
    {
        /// Another program (another game hosting, a second copy on this PC, a server still shutting down) already holds the
        /// UDP port. Steam's CreateListenSocketIP does not fail then: the first game holds [::]:port, the second one gets
        /// 0.0.0.0:port, both say they host, and an IPv4 joiner reaches whichever holds the IPv4 address. On one PC that
        /// was the other game (2026-10-18: two test hosts on 27180, joiners landed in the wrong world or were refused
        /// with "Both need the same version"). Checked by binding the port ourselves, IPv6 and IPv4, for a moment.
        public static bool PortTaken(ushort port)
        {
            return Taken(AddressFamily.InterNetworkV6, IPAddress.IPv6Any, port) || Taken(AddressFamily.InterNetwork, IPAddress.Any, port);
        }

        private static bool Taken(AddressFamily family, IPAddress any, ushort port)
        {
            try
            {
                using (var s = new Socket(family, SocketType.Dgram, ProtocolType.Udp))
                {
                    // exclusive, and IPv6 dual-stack as Steam's own: on Windows a plain probe binds right next to a dual-stack holder
                    try { s.ExclusiveAddressUse = true; } catch (SocketException) { } catch (System.NotSupportedException) { }
                    if (family == AddressFamily.InterNetworkV6) try { s.DualMode = true; } catch (SocketException) { } catch (System.NotSupportedException) { }
                    s.Bind(new IPEndPoint(any, port));
                }
                return false;
            }
            catch (SocketException e) { return e.SocketErrorCode == SocketError.AddressAlreadyInUse || e.SocketErrorCode == SocketError.AccessDenied; }   // anything else: no IPv6 here, say
        }

        public static string PortTakenReason(ushort port) =>
            "Port " + port + " is already in use by another program (another game hosting on it?). Close it or host on another port.";

        /// Why the host refuses a joiner's Hello, or null. The mod version first (what a player can fix: update), then the
        /// protocol (two builds of the same version: dev builds share a version number), then a message the host couldn't
        /// read. Before, all three said "The host plays TLD Revamp 0.69.1, you 0.69.1. Both need the same version."
        public static string HelloRefusal(bool bad, ushort protocol, string mod, ushort hostProtocol, string hostVersion)
        {
            if (mod != null && mod != hostVersion)
                return "The host plays TLD Revamp " + hostVersion + ", you " + mod + ". Both need the same version.";
            if (mod != null && protocol != hostProtocol)
                return "The host plays TLD Revamp " + hostVersion + " (network protocol " + hostProtocol + "), you " + mod + " (protocol " + protocol +
                       "): different builds of the same version. Both need the same build.";
            if (bad || mod == null)
                return "The host couldn't read your join request. It plays TLD Revamp " + hostVersion + " (network protocol " + hostProtocol + "); both need the same build.";
            return null;
        }
    }
}
