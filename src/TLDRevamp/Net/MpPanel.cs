using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// F7: a small multiplayer panel for testing with friends (dev UI, not the final menu). Host = listen for Steam
    /// friends (P2P, no port forwarding) and on UDP 27070 for LAN; Join = a friend who is in The Long Drive right now,
    /// or an IP address.
    public static class MpPanel
    {
        public static bool Open;
        private static string _ip = "127.0.0.1:27070";
        private static readonly List<(ulong id, string name)> Friends = new List<(ulong, string)>();
        private static float _friendsAt = -10f;
        private static Rect _rect = new Rect(20, 160, 380, 480);
        private static string _msg = "";

        public static void Toggle() => Open = !Open;

        public static void Draw()
        {
            if (!Open) return;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            _rect = GUI.Window(0x7D5, _rect, Window, "TLD Revamp multiplayer (test) — F7");
        }

        private static void Window(int id)
        {
            GUILayout.Label("Mod " + Plugin.Version + ", protocol " + Protocol.Version + (SteamTransport.SteamReady ? ", Steam: " + SteamFriends.GetPersonaName() : ", Steam not ready"));
            if (Mp.Server != null)
            {
                GUILayout.Label("Hosting — friends join via Steam, LAN via UDP " + Mp.Server.Port);
                foreach (var n in Mp.Server.PlayerNames()) GUILayout.Label("  • " + n);
                GUILayout.Label("Remote players shown: " + RemotePlayers.Count + ", sent " + (Mp.Server.T.BytesOut / 1024) + " KB");
                if (GUILayout.Button("Stop hosting")) Mp.Stop();
            }
            else if (Mp.Client != null)
            {
                var c = Mp.Client;
                GUILayout.Label(c.RejectReason != null ? "Rejected: " + c.RejectReason
                    : c.MyId > 0 ? "Connected as player #" + c.MyId + (c.ServerSeed != Mp.Seed() ? "  — DIFFERENT WORLD SEED (host " + c.ServerSeed + ")" : "")
                    : "Connecting… " + c.T.LastEvent);
                GUILayout.Label("Remote players shown: " + RemotePlayers.Count + ", received " + (c.T.BytesIn / 1024) + " KB");
                if (GUILayout.Button("Leave")) Mp.Stop();
            }
            else
            {
                if (GUILayout.Button("Host")) _msg = Try(() => Mp.Host(27070));
                GUILayout.Space(8);
                GUILayout.Label("Friends in The Long Drive now:");
                RefreshFriends();
                if (Friends.Count == 0) GUILayout.Label("  (none)");
                foreach (var f in Friends)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("  " + f.name, GUILayout.Width(220));
                    if (GUILayout.Button("Join")) _msg = Try(() => Mp.JoinFriend(f.id));
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(8);
                GUILayout.BeginHorizontal();
                _ip = GUILayout.TextField(_ip, GUILayout.Width(220));
                if (GUILayout.Button("Join IP")) _msg = Try(() => Mp.Join(_ip));
                GUILayout.EndHorizontal();
            }
            if (_msg.Length > 0) GUILayout.Label(_msg.Length > 200 ? _msg.Substring(0, 200) : _msg);
            GUILayout.Space(8);
            GUILayout.Label("Something wrong? Save a report (F9) and send the zip to the developer.");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save report")) Plugin.Instance.Feedback.Capture("panel");
            if (GUILayout.Button("Open reports folder")) FeedbackReporter.OpenFolder();
            GUILayout.EndHorizontal();
            if (FeedbackReporter.LastZip != null) GUILayout.Label("Last: " + System.IO.Path.GetFileName(FeedbackReporter.LastZip));
            GUI.DragWindow();
        }

        private static string Try(System.Func<string> f)
        {
            try { f(); return ""; }
            catch (System.Exception e) { Plugin.Log.LogError("MpPanel: " + e); return "Error: " + e.Message; }
        }

        private static void RefreshFriends()
        {
            if (!SteamTransport.SteamReady || Time.realtimeSinceStartup - _friendsAt < 3f) return;
            _friendsAt = Time.realtimeSinceStartup;
            Friends.Clear();
            uint app = SteamUtils.GetAppID().m_AppId;
            int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < n; i++)
            {
                var fid = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (SteamFriends.GetFriendGamePlayed(fid, out var gi) && gi.m_gameID.AppID().m_AppId == app)
                    Friends.Add((fid.m_SteamID, SteamFriends.GetFriendPersonaName(fid)));
            }
        }
    }
}
