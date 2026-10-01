using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Steamworks;

namespace TLDRevamp.Net
{
    /// The game ships Steamworks.NET 20.2 (written for Steam SDK 1.57) together with an older steam_api64.dll whose flat
    /// networking functions were compiled for ISteamNetworkingSockets008 / ISteamNetworkingUtils003. Steamworks.NET asks
    /// the Steam client for SteamNetworkingSockets012, so the old flat functions call into a newer function table: calls
    /// whose slot didn't move work, others land in the wrong function (CreatePollGroup crashed the game;
    /// GetConnectionRealTimeStatus doesn't exist in the DLL at all). Found 2026-09-27 with `net probe`.
    ///
    /// Fix: request the interface versions this DLL was built for, the same way Steamworks.NET does
    /// (SteamInternal_FindOrCreateUserInterface), and put them into its context. The game itself never uses these
    /// interfaces (checked: no SteamNetworkingSockets/Utils/Messages in Assembly-CSharp), so nothing else is affected.
    public static class SteamCompat
    {
        public const string SocketsVersion = "SteamNetworkingSockets008";
        public const string UtilsVersion = "SteamNetworkingUtils003";
        public static bool Applied;
        public static string Info = "";

        [DllImport("steam_api64", EntryPoint = "SteamInternal_FindOrCreateUserInterface", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FindOrCreateUserInterface(int hSteamUser, [MarshalAs(UnmanagedType.LPStr)] string version);

        [DllImport("steam_api64", EntryPoint = "SteamAPI_ISteamNetworkingSockets_GetQuickConnectionStatus", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool GetQuickConnectionStatusNative(IntPtr self, uint hConn, ref QuickStatus status);

        /// SteamNetworkingQuickConnectionStatus as in SDK 1.48 (the DLL's era).
        [StructLayout(LayoutKind.Sequential)]
        public struct QuickStatus
        {
            public int State, Ping;
            public float QualityLocal, QualityRemote, OutPacketsPerSec, OutBytesPerSec, InPacketsPerSec, InBytesPerSec;
            public int SendRateBytesPerSecond, PendingUnreliable, PendingReliable, SentUnackedReliable;
            public long QueueTimeUsec;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public uint[] Reserved;
        }

        public static void Ensure()
        {
            if (Applied) return;
            var ctx = typeof(SteamClient).Assembly.GetType("Steamworks.CSteamAPIContext");
            var fs = ctx?.GetField("m_pSteamNetworkingSockets", BindingFlags.NonPublic | BindingFlags.Static);
            var fu = ctx?.GetField("m_pSteamNetworkingUtils", BindingFlags.NonPublic | BindingFlags.Static);
            if (fs == null || fu == null) throw new InvalidOperationException("Steamworks.NET context fields not found");
            int user = SteamAPI.GetHSteamUser().m_HSteamUser;
            IntPtr s = FindOrCreateUserInterface(user, SocketsVersion), u = FindOrCreateUserInterface(user, UtilsVersion);
            if (s == IntPtr.Zero || u == IntPtr.Zero) throw new InvalidOperationException("Steam client has no " + SocketsVersion + "/" + UtilsVersion);
            Info = "sockets " + fs.GetValue(null) + " → " + s + ", utils " + fu.GetValue(null) + " → " + u;
            fs.SetValue(null, s);
            fu.SetValue(null, u);
            Applied = true;
            Plugin.Log.LogInfo("SteamCompat: " + Info);
        }

        public static QuickStatus Status(HSteamNetConnection c)
        {
            var st = new QuickStatus { Reserved = new uint[16] };
            GetQuickConnectionStatusNative(CSteamSockets(), c.m_HSteamNetConnection, ref st);
            return st;
        }

        // ---- relay network (Valve's SDR): datacenter pings and which relay a connection runs through. Flat functions of
        // the same DLL era, called directly (as GetQuickConnectionStatus above).
        [DllImport("steam_api64", EntryPoint = "SteamAPI_ISteamNetworkingUtils_GetPOPCount", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetPOPCountNative(IntPtr self);
        [DllImport("steam_api64", EntryPoint = "SteamAPI_ISteamNetworkingUtils_GetPOPList", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetPOPListNative(IntPtr self, [Out] uint[] list, int n);
        [DllImport("steam_api64", EntryPoint = "SteamAPI_ISteamNetworkingUtils_GetPingToDataCenter", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetPingToDataCenterNative(IntPtr self, uint pop, out uint viaRelay);
        [DllImport("steam_api64", EntryPoint = "SteamAPI_ISteamNetworkingUtils_GetDirectPingToPOP", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetDirectPingToPOPNative(IntPtr self, uint pop);
        [DllImport("steam_api64", EntryPoint = "SteamAPI_ISteamNetworkingSockets_GetConnectionInfo", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool GetConnectionInfoNative(IntPtr self, uint hConn, IntPtr info);

        /// "sgp", "fra", … from a SteamNetworkingPOPID (the characters packed big-endian).
        public static string PopName(uint id)
        {
            var sb = new System.Text.StringBuilder();
            for (int sh = 24; sh >= 0; sh -= 8) { char c = (char)((id >> sh) & 0xFF); if (c != 0) sb.Append(c); }
            return sb.ToString();
        }

        /// Every relay datacenter with this machine's ping to it (through the best relay route, and direct).
        public static string Pops()
        {
            Ensure();
            SteamNetworkingUtils.InitRelayNetworkAccess();
            var u = CSteamUtils();
            int n = GetPOPCountNative(u);
            var ids = new uint[Math.Max(0, n)];
            if (n > 0) n = GetPOPListNative(u, ids, n);
            var rows = new System.Collections.Generic.List<string>();
            for (int i = 0; i < n; i++)
            {
                int ping = GetPingToDataCenterNative(u, ids[i], out uint via), direct = GetDirectPingToPOPNative(u, ids[i]);
                rows.Add("{\"pop\":" + Json.Str(PopName(ids[i])) + ",\"ping\":" + ping + ",\"via\":" + Json.Str(PopName(via)) + ",\"direct\":" + direct + "}");
            }
            return "{\"count\":" + n + ",\"pops\":[" + string.Join(",", rows) + "]}";
        }

        /// A connection's route (SteamNetConnectionInfo_t, SDK 1.48 layout, Windows packing): the remote end's datacenter
        /// and the relay it runs through (0 = direct), state and Steam's own description of the connection.
        public static string Route(HSteamNetConnection c)
        {
            Ensure();
            var buf = Marshal.AllocHGlobal(2048);
            try
            {
                for (int i = 0; i < 2048; i += 8) Marshal.WriteInt64(buf, i, 0);
                bool ok = GetConnectionInfoNative(CSteamSockets(), c.m_HSteamNetConnection, buf);
                uint popRemote = (uint)Marshal.ReadInt32(buf, 168), popRelay = (uint)Marshal.ReadInt32(buf, 172);
                int state = Marshal.ReadInt32(buf, 176);
                string desc = Marshal.PtrToStringAnsi(IntPtr.Add(buf, 312)) ?? "";
                var q = Status(c);
                return "{\"ok\":" + (ok ? "true" : "false") + ",\"state\":" + state + ",\"ping\":" + q.Ping + ",\"popRemote\":" + Json.Str(PopName(popRemote))
                       + ",\"popRelay\":" + Json.Str(PopName(popRelay)) + ",\"relayed\":" + (popRelay != 0 ? "true" : "false") + ",\"desc\":" + Json.Str(desc) + "}";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static IntPtr CSteamUtils()
        {
            var ctx = typeof(SteamClient).Assembly.GetType("Steamworks.CSteamAPIContext");
            return (IntPtr)ctx.GetMethod("GetSteamNetworkingUtils", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        }

        private static IntPtr CSteamSockets()
        {
            var ctx = typeof(SteamClient).Assembly.GetType("Steamworks.CSteamAPIContext");
            return (IntPtr)ctx.GetMethod("GetSteamNetworkingSockets", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        }
    }
}
