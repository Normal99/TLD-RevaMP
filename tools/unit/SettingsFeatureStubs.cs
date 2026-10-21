// The stubs for the offline settings-feature test.
//
// The production files under test (SessionSettings.cs, SessionSettingsData.cs, Admin.cs) are the REAL ones,
// compiled into the test project. Only the world around them is faked: the game, the Steam transport, the
// clock, the log and the file system.
//
// Nothing here implements a rule the test checks. The stubs only stand in for what needs a running game, and
// they RECORD what the production code asked them to do (the log lines, the file writes, the password writes)
// so the test can look for a secret in the places it must not be.
#pragma warning disable CS0649, CS8981

// The game's own types are ALIASED to the fakes below: the mod patches the game's types, it never defines
// them (the privacy scan holds the repository to that). The alias names are the game's; the fakes' names are
// not, so nothing in this file claims to be game code.
global using settingsscript = TLDRevamp.Net.SettingsScript;
global using kaposztaleves = TLDRevamp.Net.DevScript;

using System;
using System.Collections.Generic;
using System.IO;

// The game's own Vector3d is in the global namespace, not in UnityEngine.
public struct Vector3d
{
    public double x, y, z;
    public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
}

namespace UnityEngine
{
    public static class Time
    {
        /// The stub's clock: the test moves it, so the console's once-a-second tick can be driven.
        public static float Now = 0f;
        public static float realtimeSinceStartup => Now;
        public static float unscaledDeltaTime => 0.016f;
    }
}

namespace BepInEx
{
    public static class Paths
    {
        /// The test points this at a temporary folder: the console's files are written there.
        public static string BepInExRootPath = Path.GetTempPath();
    }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(Type t, string name) { }
        public HarmonyPatch(Type t, string name, Type[] args) { }
    }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPrefix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPostfix : Attribute { }
}

namespace TLDRevamp
{
    static class Plugin
    {
        internal static PluginLog Log = new PluginLog();
        internal sealed class PluginLog
        {
            /// Every line the production code logged, so the test can look for a secret in the log.
            public readonly List<string> Lines = new List<string>();
            internal void LogInfo(string s) { Lines.Add(s); }
            internal void LogWarning(string s) { Lines.Add(s); }
            internal void LogError(string s) { Lines.Add(s); }
        }
    }

    static class Json { internal static string Str(string s) => s; }
}

namespace Steamworks
{
    /// The Steam connection handle (only the type is needed: the test never opens a connection).
    public struct HSteamNetConnection { public int m_HSteamNetConnection; }
}

namespace TLDRevamp.Net
{
    /// The game's own classes are ALIASED to these fakes (see the global usings at the top of the file): the
    /// mod patches the game's types, it never defines them, and the privacy scan holds the repository to that.
    public sealed class SettingsScript
    {
        public static SettingsScript s;
        public sealed class SettingsData { public bool BDevMode; }
        public SettingsData S = new SettingsData();
        public TabMisc tabMisc = new TabMisc();

        /// The settings tab: the dev-mode toggle lives at index 18.
        public sealed class TabMisc
        {
            public readonly Toggle[] toggles = new Toggle[20];
            public TabMisc() { for (int i = 0; i < toggles.Length; i++) toggles[i] = new Toggle(); }
        }
        public sealed class Toggle { public void ChangeWithoutNotify(bool on) { } }
    }

    public sealed class DevScript
    {
        public static DevScript s;
        public sealed class DevSettings { public void Disabled() { } }
        public DevSettings settings = new DevSettings();
        public void UpdDevBools() { }
    }

    /// The teleport requests (the session settings own one of them).
    public static class Tpa
    {
        public static bool Enabled = true;
        public static int CooldownS = 300;
        public static int[] Choices = { 0, 60, 300, 900 };
        public static void ClearTpaRequests() { }
        internal static void OnSettingsChanged(SessionSettingsSnapshot old, SessionSettingsSnapshot current) { }
    }

    public static class Entities
    {
        public static long ServerObjects;
    }

    public static class RevampSettings
    {
        public sealed class Entry
        {
            public string Key;
            public Func<int> Get;
            public Action<int> Set;
        }
        public static readonly List<Entry> Entries = new List<Entry>();
        public static int Int(string key, int fallback) => fallback;
        public static void SetInt(string key, int value) { }
        public static void Save() { }
        internal static string SettingsFilePath => Path.Combine(Path.GetTempPath(), "tldrevamp-settings.txt");
    }

    /// The transport: the test never sends anything, but the production code asks the transport to.
    public sealed class SteamTransport
    {
        public const int SendReliable = 0;
        /// Every message the production code asked to be sent.
        public readonly List<string> Sent = new List<string>();
        public void Send(Steamworks.HSteamNetConnection c, NetWriter w, int flags) { Sent.Add("send"); }
    }

    public static class Mp
    {
        /// The hosting server. The test sets it (the real MpServer) to drive the console.
        public static MpServer Server;
        /// The joining client. The test sets it to make the code take the joiner's path.
        public static MpClient Client;
        public static string SaveTail() => "saved";
    }

    public sealed class MpClient
    {
        public bool Ready;
    }

    /// The players a session knows about (the production code only reads these fields).
    public static class Session
    {
        public sealed class Player
        {
            public int Id; public string Name; public ulong SteamId; public bool Host; public int Ping = -1;
        }
        public static string Clean(string s) => s ?? "";
        public static string CleanName(string s) => s ?? "";
        public static string MyName() => "tester";
    }

    /// The players who are away (the production code only reads these fields).
    public static class PlayerSave
    {
        public sealed class Away
        {
            public ulong Steam; public string Name; public bool Dead; public int Items, Worn; public Vector3d G;
        }
        public static List<Away> AwayPlayers() => new List<Away>();
        public static bool Forget(ulong steam) => true;
    }

    public static class Protocol
    {
        public const byte SessionSettings = 111;
        public const ushort Version = 48;
    }

    /// The dedicated server mode (the admin console's `stop` command belongs to it).
    public static class DedicatedServer
    {
        public static bool Enabled;
        public static long AutoSaves;
        public static bool StopRequested;
    }

    /// The hosting server's parts that live in other production files (Session.cs, Mp.cs). The session-settings
    /// half of the class is the REAL one (SessionSettings.cs) and the admin half is the REAL one (Admin.cs).
    public sealed partial class MpServer
    {
        private sealed class Peer
        {
            public bool Ready, Saved, HasPos; public ulong SteamId; public int Id;
            public Vector3d G; public Vector3d Pos;
            public string Name;
            public Steamworks.HSteamNetConnection Conn;
        }
        private readonly Dictionary<int, Peer> _peers = new Dictionary<int, Peer>();
        /// The players who were kicked this session.
        private readonly HashSet<ulong> _kicked = new HashSet<ulong>();

        /// The transport of the hosting server (the real one is created by Mp.Host).
        public readonly SteamTransport T = new SteamTransport();

        private string _password = "";
        /// How often the password field was written. The session path (SetSessionPassword) writes it once; a
        /// second, parallel writer is exactly the defect the dedicated server used to have, and the test must see
        /// that the field is written exactly once per path call.
        public int PasswordAssignments;
        /// Every publish of the settings snapshot (the transport was asked to send it).
        public int Publishes;
        /// The messages the transport was asked to send.
        public readonly List<string> Published = new List<string>();

        public string Password
        {
            get { return _password; }
            set { PasswordAssignments++; _password = SessionSettingsRules.CleanPassword(value); }
        }

        public static bool JoinAtHost = true;
        /// The most players at once (the host counts as one).
        public static int MaxPlayers = 16;

        internal void SendToAll(NetWriter w, bool reliable, int except, Vector3d? near, double range)
        {
            Publishes++;
            Published.Add("sendAll");
        }

        private bool ById(int id, out Peer p) => _peers.TryGetValue(id, out p);
        private int FindPlayer(string name, int except) => -1;
        private string NameOf(int id) => "player " + id;
        internal void Notice(string text) { }
        private void Tell(int to, string text) { }
        public bool Kick(int id, string why = "") { return true; }
        internal void Players(List<Session.Player> into) { }
    }
}
