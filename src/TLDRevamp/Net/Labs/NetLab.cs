using System;
using Steamworks;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// One-process transport test (bridge `net pair <count> <size> [loopback]`, `net status`, `net stop`): both ends of a
    /// Steam socket pair send `count` reliable and `count` unreliable messages of `size` payload bytes to each other.
    /// Checks every payload byte, reliable ordering (sequence numbers must arrive consecutively) and counts delivery.
    public static class NetLab
    {
        private static SteamTransport _t;
        private static HSteamNetConnection _a, _b;
        private static int _count, _size;
        private static float _t0, _elapsed;
        private static bool _running;
        private static readonly uint[] NextRel = new uint[2];
        private static readonly int[] GotRel = new int[2], GotUnrel = new int[2];
        private static int _badPayload, _outOfOrder, _malformed;
        private static readonly NetWriter W = new NetWriter();

        public static string Pair(int count, int size, bool loopback)
        {
            Stop();
            if (!SteamTransport.SteamReady) return "{\"error\":\"steam not initialized\"}";
            _t = SteamTransport.Pair(loopback);
            _t.Message = OnMessage;
            _a = _t.Connections[0];
            _b = _t.Connections[1];
            _count = count; _size = size;
            Array.Clear(NextRel, 0, 2); Array.Clear(GotRel, 0, 2); Array.Clear(GotUnrel, 0, 2);
            _badPayload = _outOfOrder = _malformed = 0;
            for (int side = 0; side < 2; side++)
            {
                var conn = side == 0 ? _a : _b;
                for (uint i = 0; i < count; i++)
                {
                    Build(1, side, i); _t.Send(conn, W, SteamTransport.SendReliable);
                    Build(2, side, i); _t.Send(conn, W, SteamTransport.SendUnreliable);
                }
            }
            _t0 = Time.realtimeSinceStartup;
            _elapsed = 0;
            _running = true;
            return "{\"started\":true,\"mode\":\"" + _t.Mode + "\",\"sent\":" + _t.MessagesOut + ",\"sendErrors\":" + _t.SendErrors + "}";
        }

        private static void Build(byte type, int side, uint seq)
        {
            W.Reset();
            W.U8(type);
            W.U8((byte)side);
            W.U32(seq);
            for (int j = 0; j < _size; j++) W.U8(Pattern(side, seq, j));
        }

        private static byte Pattern(int side, uint seq, int j) => (byte)(seq * 31 + (uint)j * 7 + (uint)side * 101);

        private static void OnMessage(HSteamNetConnection conn, NetReader r)
        {
            byte type = r.U8();
            int side = r.U8();
            uint seq = r.U32();
            if (r.Bad || side > 1 || (type != 1 && type != 2)) { _malformed++; return; }
            bool ok = r.Remaining == _size;
            for (int j = 0; ok && j < _size; j++) if (r.U8() != Pattern(side, seq, j)) ok = false;
            if (!ok) _badPayload++;
            // a message from side s arrives on the other end's connection
            if (conn != (side == 0 ? _b : _a)) _malformed++;
            if (type == 1)
            {
                if (seq != NextRel[side]) _outOfOrder++;
                NextRel[side] = seq + 1;
                GotRel[side]++;
            }
            else GotUnrel[side]++;
        }

        /// Runner, once per frame.
        public static void Tick()
        {
            if (!_running || _t == null) return;
            _t.Poll();
            _elapsed = Time.realtimeSinceStartup - _t0;
            bool all = GotRel[0] == _count && GotRel[1] == _count && GotUnrel[0] == _count && GotUnrel[1] == _count;
            if (all || _elapsed > 10f) _running = false;
        }

        public static string Status()
        {
            if (_t == null) return "{\"running\":false}";
            var rt = _t.Status(_a);
            return "{\"running\":" + (_running ? "true" : "false") + ",\"mode\":\"" + _t.Mode + "\",\"elapsedMs\":" + (int)(_elapsed * 1000) +
                   ",\"count\":" + _count + ",\"size\":" + _size +
                   ",\"reliable\":[" + GotRel[0] + "," + GotRel[1] + "],\"unreliable\":[" + GotUnrel[0] + "," + GotUnrel[1] + "]" +
                   ",\"outOfOrder\":" + _outOfOrder + ",\"badPayload\":" + _badPayload + ",\"malformed\":" + _malformed +
                   ",\"msgIn\":" + _t.MessagesIn + ",\"msgOut\":" + _t.MessagesOut + ",\"bytesIn\":" + _t.BytesIn + ",\"sendErrors\":" + _t.SendErrors +
                   ",\"ping\":" + rt.Ping + ",\"state\":" + rt.State + ",\"compat\":" + Json.Str(SteamCompat.Info) + "}";
        }

        /// Crash isolation: one native step at a time (`net probe <n>`).
        public static string Probe(int step)
        {
            Plugin.Log.LogInfo("net probe " + step + " start");
            SteamCompat.Ensure();
            string r;
            switch (step)
            {
                case 1: { var g = SteamNetworkingSockets.CreatePollGroup(); SteamNetworkingSockets.DestroyPollGroup(g); r = "pollgroup " + g.m_HSteamNetPollGroup; break; }
                case 2: { Callback<SteamNetConnectionStatusChangedCallback_t>.Create(_ => { }); r = "callback ok"; break; }
                case 3:
                {
                    var me = new SteamNetworkingIdentity(); me.SetSteamID(SteamUser.GetSteamID()); var me2 = me;
                    bool ok = SteamNetworkingSockets.CreateSocketPair(out var a, out var b, false, ref me, ref me2);
                    r = "pair(steamid) " + ok + " " + a.m_HSteamNetConnection + "," + b.m_HSteamNetConnection;
                    SteamNetworkingSockets.CloseConnection(a, 0, null, false); SteamNetworkingSockets.CloseConnection(b, 0, null, false);
                    break;
                }
                case 4:
                {
                    var i1 = new SteamNetworkingIdentity(); i1.Clear(); var i2 = i1;
                    bool ok = SteamNetworkingSockets.CreateSocketPair(out var a, out var b, false, ref i1, ref i2);
                    r = "pair(clear) " + ok + " " + a.m_HSteamNetConnection + "," + b.m_HSteamNetConnection;
                    SteamNetworkingSockets.CloseConnection(a, 0, null, false); SteamNetworkingSockets.CloseConnection(b, 0, null, false);
                    break;
                }
                case 5:
                {
                    var addr = new SteamNetworkingIPAddr(); addr.Clear(); addr.m_port = 27060;
                    var ls = SteamNetworkingSockets.CreateListenSocketIP(ref addr, 0, null);
                    r = "listen ip " + ls.m_HSteamListenSocket;
                    SteamNetworkingSockets.CloseListenSocket(ls);
                    break;
                }
                default: r = "?"; break;
            }
            Plugin.Log.LogInfo("net probe " + step + " done: " + r);
            return "{\"probe\":" + step + ",\"result\":" + Json.Str(r) + "}";
        }

        /// Simulated bad internet for every Steam socket in this process (bridge `net lag <rttMs> [loss%] [jitter%] [jitterMs]`).
        /// Send-side settings: in one process both ends send through them, so each direction gets half the RTT.
        /// This interface version has no jitter setting; "jitter" = that percentage of packets delayed by jitterMs extra
        /// (the reorder setting), which is how uneven arrival looks to the receiver. `net lag 0` = off.
        public static string Lag(int rttMs, float lossPct, float jitterPct, int jitterMs)
        {
            SteamCompat.Ensure();
            bool ok = SetInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLag_Send, rttMs / 2)
                    & SetFloat(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLoss_Send, lossPct)
                    & SetFloat(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketReorder_Send, jitterPct)
                    & SetInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketReorder_Time, jitterMs);
            LagInfo = rttMs + " ms RTT, " + lossPct + " % loss, " + jitterPct + " % +" + jitterMs + " ms";
            return "{\"ok\":" + (ok ? "true" : "false") + ",\"lag\":" + Json.Str(LagInfo) + "}";
        }

        /// Real internet instead of simulated lag (bridge `net relay <cluster|off>`): Steam P2P connections made AFTER
        /// this go through Valve's relay network only (no direct/LAN route), pinned to relay cluster `cluster` (a
        /// Steam datacenter code, e.g. "sgp", "gru", "syd") — two machines on one LAN then talk across the world.
        /// Both players' machines set it before `mp host` / `mp join <steamid>`. `net relay off` restores defaults.
        public static string Relay(string cluster)
        {
            SteamCompat.Ensure();
            bool off = cluster == "off";
            bool ok = SetInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable,
                             off ? Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Default : Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Disable)
                    & SetString(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SDRClient_ForceRelayCluster, off ? "" : cluster);
            RelayInfo = off ? "off" : "relay only via " + cluster;
            return "{\"ok\":" + (ok ? "true" : "false") + ",\"relay\":" + Json.Str(RelayInfo) + "}";
        }

        public static string RelayInfo = "off";

        private static bool SetString(ESteamNetworkingConfigValue v, string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
            var p = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, p, bytes.Length);
                return SteamNetworkingUtils.SetConfigValue(v, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_String, p);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(p); }
        }

        public static string LagInfo = "off";

        private static bool SetInt(ESteamNetworkingConfigValue v, int value)
        {
            var p = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
            try
            {
                System.Runtime.InteropServices.Marshal.WriteInt32(p, value);
                return SteamNetworkingUtils.SetConfigValue(v, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, p);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(p); }
        }

        private static bool SetFloat(ESteamNetworkingConfigValue v, float value)
        {
            var p = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
            try
            {
                System.Runtime.InteropServices.Marshal.WriteInt32(p, BitConverter.SingleToInt32Bits(value));
                return SteamNetworkingUtils.SetConfigValue(v, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Float, p);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(p); }
        }

        public static string Stop()
        {
            _running = false;
            if (_t != null) { _t.Dispose(); _t = null; }
            return "{\"stopped\":true}";
        }
    }
}
