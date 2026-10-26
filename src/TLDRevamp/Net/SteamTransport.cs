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
        internal readonly bool ServerSide;
        public readonly List<HSteamNetConnection> Connections = new List<HSteamNetConnection>();
        public Action<HSteamNetConnection> Connected, Disconnected;
        public Action<HSteamNetConnection, NetReader> Message;
        public long MessagesIn, MessagesOut, BytesIn, BytesOut, SendErrors;
        public string LastEvent = "";
        /// Why Steam ended the last connection (its end reason code and debug text), "" while none ended.
        public string LastEnd = "";
        /// The last connection that ended never got to Connected: the other side was never reached.
        public bool LastEndUnreached;

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

        private SteamTransport(string mode, bool server = false)
        {
            SteamCompat.Ensure();
            Mode = mode; ServerSide = server;
            if (_statusCb == null) _statusCb = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatus);
            Live.Add(this);
        }

        public static bool SteamReady => SteamManager.Initialized;

        public static SteamTransport ListenIP(ushort port)
        {
            var t = new SteamTransport("listen-ip", true);
            var addr = new SteamNetworkingIPAddr();
            addr.Clear();
            addr.m_port = port;
            t._listen = SteamNetworkingSockets.CreateListenSocketIP(ref addr, 0, null);
            // no listen socket = no one can join; it used to "host" anyway, silently
            if (t._listen == HSteamListenSocket.Invalid) { t.Dispose(); throw new InvalidOperationException("Steam could not open port " + port + " for hosting."); }
            return t;
        }

        /// Also accept Steam P2P connections (friends, NAT traversal/relay) on this transport.
        public void AddListenP2P(int virtualPort)
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            _listenP2P = SteamNetworkingSockets.CreateListenSocketP2P(virtualPort, 0, null);
            // friends join through this socket: without it they time out on "Connecting" and the host hears nothing
            if (_listenP2P == HSteamListenSocket.Invalid) Plugin.Log.LogWarning("net: Steam could not open the P2P listen socket: friends can't join, only joins by address");
        }

        public static SteamTransport ListenP2P(int virtualPort)
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            var t = new SteamTransport("listen-p2p", true);
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
                        // the host's log used to stay silent until a Hello was handled: a join that never got that far
                        // left no trace on this side (a friend stuck on "Connecting", 2026-10-26)
                        var accepted = SteamNetworkingSockets.AcceptConnection(c);
                        Plugin.Log.LogInfo($"net: incoming connection {s.m_info.m_szConnectionDescription}: {(accepted == EResult.k_EResultOK ? "accepted" : "accept failed (" + accepted + ")")}");
                        if (accepted == EResult.k_EResultOK) Add(c);
                        else SteamNetworkingSockets.CloseConnection(c, 0, "accept failed", false);
                    }
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    Connected?.Invoke(c);
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    LastEnd = s.m_info.m_eEndReason + (string.IsNullOrEmpty(s.m_info.m_szEndDebug) ? "" : " " + s.m_info.m_szEndDebug);
                    LastEndUnreached = s.m_eOldState != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected;
                    Plugin.Log.LogInfo($"net: connection {s.m_info.m_szConnectionDescription} ended{(LastEndUnreached ? " before it connected" : "")}: {s.m_info.m_eState}, reason {LastEnd}");
                    SteamNetworkingSockets.CloseConnection(c, 0, null, false);
                    Connections.Remove(c);
                    Disconnected?.Invoke(c);
                    break;
            }
        }

        // Reports yield whenever reliable game traffic is waiting. At most one report chunk enters an empty queue.
        internal bool SendBackground(HSteamNetConnection c, NetWriter w)
        {
            var status = Status(c);
            return status.PendingReliable == 0 && status.QueueTimeUsec < 20000 && Send(c, w, SendReliable);
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
            long t0 = NetMeter.Now();
            var r = SteamNetworkingSockets.SendMessageToConnection(c, _sendBuf, (uint)w.Len, flags, out _);
            if (r != EResult.k_EResultOK) { SendErrors++; return false; }
            MessagesOut++;
            BytesOut += w.Len;
            NetMeter.Sent(w.Buf, w.Len, c.m_HSteamNetConnection, t0, ServerSide);
            return true;
        }

        /// Deliver everything received since the last call. Returns the number of messages.
        public static long HandlerErrors;
        private static float _handlerErrAt;
        /// Per connection and frame, at most this many messages / bytes are handled (0 = no limit); the rest waits in
        /// Steam's queue for the next frame. The host sets it (MpServer): one player sending without pause could
        /// otherwise keep the host's frame in this loop. A player's normal traffic is a few messages a frame, an area
        /// taken over a few hundred at once.
        public int MaxMsgsPerConnFrame, MaxBytesPerConnFrame;
        public long Throttled;
        public double MaxPollMs;   // client only; hosts retain their per-peer message/byte limits
        private readonly FrameWorkBudget _pollWork = new FrameWorkBudget();
        public int Poll()
        {
            int total = 0;
            bool timed = MaxPollMs > 0;
            if (timed) _pollWork.Begin(UnityEngine.Time.frameCount, MaxPollMs);
            for (int ci = 0; ci < Connections.Count; ci++)
            {
                var conn = Connections[ci];
                int leftN = MaxMsgsPerConnFrame > 0 ? MaxMsgsPerConnFrame : int.MaxValue;
                long leftB = MaxBytesPerConnFrame > 0 ? MaxBytesPerConnFrame : long.MaxValue;
                while (true)
                {
                    if (leftN <= 0 || leftB <= 0) { Throttled++; break; }
                    if (timed && !_pollWork.CanRun) { Fixes.StreamReceiveBudget.Deferred++; return total; }
                    // Fetch one when timed: never strand native messages from a partially consumed batch.
                    int want = timed ? 1 : Math.Min(_msgs.Length, leftN);
                    long start = timed ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    int n = SteamNetworkingSockets.ReceiveMessagesOnConnection(conn, _msgs, want);
                    if (n <= 0) break;
                    leftN -= n;
                    for (int i = 0; i < n; i++)
                    {
                        var p = _msgs[i];
                        int size = Marshal.ReadInt32(p, OffSize);
                        leftB -= size;
                        _reader.Load(size);
                        Marshal.Copy(Marshal.ReadIntPtr(p, OffData), _reader.Buf, 0, size);
                        SteamNetworkingMessage_t.Release(p);
                        MessagesIn++;
                        BytesIn += size;
                        // a Batch counts its own type byte here; each message in it is counted where it's handled (MpClient)
                        byte type = size > 0 ? _reader.Buf[0] : (byte)0;
                        long t0 = NetMeter.Now();
                        if (type == Protocol.Batch) NetMeter.Received(type, _reader.Buf, 0, size, 1, conn.m_HSteamNetConnection, t0, ServerSide);
                        try { Message?.Invoke(conn, _reader); }
                        catch (Exception e)
                        {
                            // one bad message is dropped; a peer sending a stream of them must not fill the log: the first
                            // 20, then one every 10 s
                            HandlerErrors++;
                            float now = UnityEngine.Time.realtimeSinceStartup;
                            if (HandlerErrors <= 20 || now - _handlerErrAt > 10f) { _handlerErrAt = now; Plugin.Log.LogError($"net message handler ({HandlerErrors} so far): " + e); }
                        }
                        if (type != Protocol.Batch) NetMeter.Received(type, _reader.Buf, 0, size, size, conn.m_HSteamNetConnection, t0, ServerSide);
                    }
                    if (timed)
                    {
                        long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
                        _pollWork.Charge(ticks);
                        Fixes.StreamReceiveBudget.Messages += n;
                        long us = ticks * 1000000 / System.Diagnostics.Stopwatch.Frequency;
                        if (us > Fixes.StreamReceiveBudget.MaxMessageUs) Fixes.StreamReceiveBudget.MaxMessageUs = us;
                    }
                    total += n;
                    if (n < want) break;
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
