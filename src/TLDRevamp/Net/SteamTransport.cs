using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;

namespace TLDRevamp.Net
{
    /// The mod's transport: Steam Networking Sockets (in the game's own Steamworks.NET 20.2 / SDK 1.57, nothing shipped).
    /// Reliable-ordered and unreliable messages over one connection, NAT traversal/relay for P2P, encryption.
    /// Main thread only: Poll() once per frame; connection state changes arrive through Steam callbacks, which the game's
    /// SteamManager already dispatches every frame.
    /// No poll groups: CreatePollGroup/DestroyPollGroup crash the game under Proton (native crash, isolated with
    /// `net probe`, 2026-09-27); messages are received per connection instead (at most a few dozen connections).
    ///
    /// Modes: listen/connect by IP (LAN, local tests, later dedicated servers), by Steam P2P (friends, no port forwarding),
    /// or an in-process socket pair (one-process tests of everything above the wire).
    public sealed class SteamTransport : IDisposable
    {
        public const int SendUnreliable = Constants.k_nSteamNetworkingSend_Unreliable;
        public const int SendReliable = Constants.k_nSteamNetworkingSend_Reliable;

        public readonly string Mode;
        public readonly List<HSteamNetConnection> Connections = new List<HSteamNetConnection>();
        public Action<HSteamNetConnection> Connected, Disconnected;
        public Action<HSteamNetConnection, NetReader> Message;
        public long MessagesIn, MessagesOut, BytesIn, BytesOut, SendErrors;
        public string LastEvent = "";

        private HSteamListenSocket _listen = HSteamListenSocket.Invalid, _listenP2P = HSteamListenSocket.Invalid;
        private readonly NetReader _reader = new NetReader();
        private readonly IntPtr[] _msgs = new IntPtr[256];
        private IntPtr _sendBuf = IntPtr.Zero;
        private int _sendCap;
        private bool _disposed;

        // Field offsets of SteamNetworkingMessage_t, read straight from native memory (no struct marshalling garbage)
        private static readonly int OffData = (int)Marshal.OffsetOf(typeof(SteamNetworkingMessage_t), nameof(SteamNetworkingMessage_t.m_pData));
        private static readonly int OffSize = (int)Marshal.OffsetOf(typeof(SteamNetworkingMessage_t), nameof(SteamNetworkingMessage_t.m_cbSize));

        private static readonly List<SteamTransport> Live = new List<SteamTransport>();
        private static Callback<SteamNetConnectionStatusChangedCallback_t> _statusCb;

        private SteamTransport(string mode)
        {
            SteamCompat.Ensure();
            Mode = mode;
            if (_statusCb == null) _statusCb = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatus);
            Live.Add(this);
        }

        public static bool SteamReady => SteamManager.Initialized;

        public static SteamTransport ListenIP(ushort port)
        {
            var t = new SteamTransport("listen-ip");
            var addr = new SteamNetworkingIPAddr();
            addr.Clear();
            addr.m_port = port;
            t._listen = SteamNetworkingSockets.CreateListenSocketIP(ref addr, 0, null);
            return t;
        }

        /// Also accept Steam P2P connections (friends, NAT traversal/relay) on this transport.
        public void AddListenP2P(int virtualPort)
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            _listenP2P = SteamNetworkingSockets.CreateListenSocketP2P(virtualPort, 0, null);
        }

        public static SteamTransport ListenP2P(int virtualPort)
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            var t = new SteamTransport("listen-p2p");
            t._listen = SteamNetworkingSockets.CreateListenSocketP2P(virtualPort, 0, null);
            return t;
        }

        public const ushort DefaultPort = 27070;

        public static SteamTransport ConnectIP(string address)
        {
            var t = new SteamTransport("connect-ip");
            var addr = new SteamNetworkingIPAddr();
            addr.Clear();
            if (!addr.ParseString(address)) throw new ArgumentException("bad address " + address);
            // no port typed: the mod's own (the multiplayer screen hosts on it). Before v0.64.4 "1.2.3.4" parsed to port 0 and the join
            // silently went nowhere (a friend's first IP join)
            if (addr.m_port == 0) addr.m_port = DefaultPort;
            t.Add(SteamNetworkingSockets.ConnectByIPAddress(ref addr, 0, null));
            return t;
        }

        public static SteamTransport ConnectP2P(ulong steamId, int virtualPort)
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            var t = new SteamTransport("connect-p2p");
            var id = new SteamNetworkingIdentity();
            id.SetSteamID64(steamId);
            t.Add(SteamNetworkingSockets.ConnectP2P(ref id, virtualPort, 0, null));
            return t;
        }

        /// Two already connected ends inside this process (through the real network code when loopback is true).
        public static SteamTransport Pair(bool loopback)
        {
            var t = new SteamTransport(loopback ? "pair-loopback" : "pair");
            var me = new SteamNetworkingIdentity();
            me.SetSteamID(SteamUser.GetSteamID());
            var me2 = me;
            if (!SteamNetworkingSockets.CreateSocketPair(out var a, out var b, loopback, ref me, ref me2))
                throw new InvalidOperationException("CreateSocketPair failed");
            t.Add(a);
            t.Add(b);
            return t;
        }

        private void Add(HSteamNetConnection c)
        {
            if (c == HSteamNetConnection.Invalid) throw new InvalidOperationException("invalid connection");
            if (!Connections.Contains(c)) Connections.Add(c);
        }

        private static void OnStatus(SteamNetConnectionStatusChangedCallback_t s)
        {
            foreach (var t in Live.ToArray())
            {
                bool mine = t.Connections.Contains(s.m_hConn) || t.Owns(s.m_info.m_hListenSocket);
                if (mine) { t.Status(s); return; }
            }
        }

        private bool Owns(HSteamListenSocket ls) =>
            ls != HSteamListenSocket.Invalid && (ls == _listen || ls == _listenP2P);

        private void Status(SteamNetConnectionStatusChangedCallback_t s)
        {
            var c = s.m_hConn;
            LastEvent = s.m_eOldState + " → " + s.m_info.m_eState;
            switch (s.m_info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    // Incoming on our listen socket: accept (the protocol's handshake decides whether they may stay)
                    if (Owns(s.m_info.m_hListenSocket))
                    {
                        if (SteamNetworkingSockets.AcceptConnection(c) == EResult.k_EResultOK) Add(c);
                        else SteamNetworkingSockets.CloseConnection(c, 0, "accept failed", false);
                    }
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    Connected?.Invoke(c);
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    SteamNetworkingSockets.CloseConnection(c, 0, null, false);
                    Connections.Remove(c);
                    Disconnected?.Invoke(c);
                    break;
            }
        }

        public bool Send(HSteamNetConnection c, NetWriter w, int flags)
        {
            if (w.Len > _sendCap)
            {
                if (_sendBuf != IntPtr.Zero) Marshal.FreeHGlobal(_sendBuf);
                _sendCap = Math.Max(w.Len, 4096);
                _sendBuf = Marshal.AllocHGlobal(_sendCap);
            }
            Marshal.Copy(w.Buf, 0, _sendBuf, w.Len);
            var r = SteamNetworkingSockets.SendMessageToConnection(c, _sendBuf, (uint)w.Len, flags, out _);
            if (r != EResult.k_EResultOK) { SendErrors++; return false; }
            MessagesOut++;
            BytesOut += w.Len;
            return true;
        }

        /// Deliver everything received since the last call. Returns the number of messages.
        public static long HandlerErrors;
        private static float _handlerErrAt;
        public int Poll()
        {
            int total = 0;
            for (int ci = 0; ci < Connections.Count; ci++)
            {
                var conn = Connections[ci];
                while (true)
                {
                    int n = SteamNetworkingSockets.ReceiveMessagesOnConnection(conn, _msgs, _msgs.Length);
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        var p = _msgs[i];
                        int size = Marshal.ReadInt32(p, OffSize);
                        _reader.Load(size);
                        Marshal.Copy(Marshal.ReadIntPtr(p, OffData), _reader.Buf, 0, size);
                        SteamNetworkingMessage_t.Release(p);
                        MessagesIn++;
                        BytesIn += size;
                        try { Message?.Invoke(conn, _reader); }
                        catch (Exception e)
                        {
                            // one bad message is dropped; a peer sending a stream of them must not fill the log: the first
                            // 20, then one every 10 s
                            HandlerErrors++;
                            float now = UnityEngine.Time.realtimeSinceStartup;
                            if (HandlerErrors <= 20 || now - _handlerErrAt > 10f) { _handlerErrAt = now; Plugin.Log.LogError($"net message handler ({HandlerErrors} so far): " + e); }
                        }
                    }
                    total += n;
                    if (n < _msgs.Length) break;
                }
            }
            return total;
        }

        public SteamCompat.QuickStatus Status(HSteamNetConnection c) => SteamCompat.Status(c);

        /// Close one connection (a rejected or kicked player). Linger: what was already sent still goes out.
        public void Close(HSteamNetConnection c, string reason)
        {
            if (!Connections.Remove(c)) return;
            SteamNetworkingSockets.CloseConnection(c, 0, reason, true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var c in Connections) SteamNetworkingSockets.CloseConnection(c, 0, "closed", false);
            Connections.Clear();
            if (_listen != HSteamListenSocket.Invalid) SteamNetworkingSockets.CloseListenSocket(_listen);
            if (_listenP2P != HSteamListenSocket.Invalid) SteamNetworkingSockets.CloseListenSocket(_listenP2P);
            if (_sendBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_sendBuf); _sendBuf = IntPtr.Zero; }
            Live.Remove(this);
        }
    }
}
