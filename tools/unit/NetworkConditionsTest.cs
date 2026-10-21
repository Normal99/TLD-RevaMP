using System;
using System.Collections.Generic;
using System.Text.Json;
using Steamworks;
using TLDRevamp;
using TLDRevamp.Net;

static class NetworkConditionsTest
{
    private static int _checks;
    private static void Check(bool ok, string why) { _checks++; if (!ok) throw new Exception(why); }
    private static void Main()
    {
        Check(!NetworkConditions.Active, "off at process start");
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "repro-settings-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder); BepInEx.Paths.ConfigPath = folder;
        try
        {
            string file = System.IO.Path.Combine(folder, "tldrevamp-settings.txt");
            // The terrain choice (issue #32) is a normal saved setting: it has to be in the list, be unique and
            // round trip through the saved file so the player keeps their choice between games.
            var terrain = RevampSettings.Entries.Find(e => e.Key == "terrainMode");
            Check(terrain != null, "the terrain setting exists");
            if (terrain != null)
            {
                Check(terrain.Kind == RevampSettings.Kind.Choice, "the terrain setting is a choice");
                Check(terrain.Options != null && terrain.Options.Length == 2, "the terrain setting offers two shapes");
                Check(terrain.Get() == 0, "the terrain setting starts on the game's own world");
                terrain.Set(1);
                Check(terrain.Get() == 1, "the terrain setting keeps the player's choice");
                Check(TLDRevamp.WorldGen.TerrainChoice.Selected == TLDRevamp.WorldGen.TerrainMode.MainRoadOnly,
                    "the terrain setting changes the mode the new game uses");
                terrain.Set(0);
                Check(TLDRevamp.WorldGen.TerrainChoice.Selected == TLDRevamp.WorldGen.TerrainMode.Crossroads,
                    "the terrain setting goes back to the game's own world");
            }

            var runtime = RevampSettings.Entries.FindAll(e => e.RuntimeOnly);
            var keys = new HashSet<string>();
            foreach (var e in RevampSettings.Entries) if (e.Key != null) Check(keys.Add(e.Key), "unique merged setting " + e.Key);
            foreach (string key in new[] { "ownershipOverlay", "ownerGhost", "ownerGhostAll", "linkView", "objectComparison", "objectEvents", "simPreset", "simLatency", "simJitter", "simLoss", "simDuplication", "simReorder", "reproRecording" })
                Check(RevampSettings.Entries.Exists(e => e.Key == key && e.Default == 0 && e.Get() == 0), "merged debug control is present and off by default: " + key);
            Check(runtime.Count == 7, "six simulation controls and recorder registered by real settings");
            var attempted = new List<string>();
            foreach (var e in runtime) attempted.Add(e.Key + "=1");
            System.IO.File.WriteAllLines(file, attempted);
            int before = NetLab.Calls; RevampSettings.Load();
            Check(NetLab.Calls == before && ReproRecorder.Starts == 0 && !NetworkConditions.Active, "load ignores runtime controls");
            NetworkConditions.Command("mobile"); RevampSettings.Save();
            string saved = System.IO.File.ReadAllText(file);
            foreach (var e in runtime) Check(!saved.Contains(e.Key + "="), "save excludes " + e.Key);
            Check(saved.Contains("overlay="), "save writes ordinary settings");
            RevampSettings.ResetToDefaults(); Check(!NetworkConditions.Active, "reset clears simulation");
            Check(!System.IO.File.ReadAllText(file).Contains("simPreset="), "reset save excludes runtime values");
        }
        finally { System.IO.Directory.Delete(folder, true); }
        Check(RevampSettings.Entries.Find(e => e.Key == "simPreset").Help.Contains("relayed traffic is impaired twice"), "multi-client host help");
        UnityEngine.Event.current.type = UnityEngine.EventType.Repaint;
        NetworkConditions.Command("mobile"); OverlayStyle.Ensure(); NetworkConditions.Measure(); DebugLayout.Arrange(1920, 1080, new DebugLayout.Request { Network = NetworkConditions.BarHeight, NetworkWidth = NetworkConditions.BarWidth }); NetworkConditions.Draw();
        Check(UnityEngine.GUI.LastLabel.Contains("all client links"), "host warning rendered");
        NetLab.Lag(180, 2, 10, 40);
        Check(NetLab.Values[4] == 90 && NetLab.Values[5] == 0 && NetLab.Values[2] == 2 && NetLab.Values[3] == 0, "legacy send-only latency and loss");
        Check(NetLab.Values[6] == 10 && NetLab.Values[7] == 0 && NetLab.Values[8] == 40, "legacy jitter percentage preserved");
        Check(NetworkConditions.Current.SendOnly && NetworkConditions.Active && NetLab.LagInfo == "180 ms RTT, 2 % loss, 10 % +40 ms", "legacy state and label");
        OverlayStyle.Ensure(); NetworkConditions.Measure(); DebugLayout.Arrange(1920, 1080, new DebugLayout.Request { Network = NetworkConditions.BarHeight, NetworkWidth = NetworkConditions.BarWidth }); NetworkConditions.Draw(); Check(UnityEngine.GUI.LastLabel.Contains("impaired on send"), "legacy warning describes send-only relay effect");
        NetLab.FailOnce = 4; NetworkConditions.Command("mobile");
        Check(NetworkConditions.Current.SendOnly && NetLab.Values[5] == 0 && NetLab.Values[6] == 10, "rollback preserves legacy directions");
        RevampSettings.Entries.Find(e => e.Key == "simLatency").Set(3);
        Check(!NetworkConditions.Current.SendOnly && NetLab.Values[4] == 15 && NetLab.Values[5] == 15, "menu edits use documented bidirectional conditions");
        NetworkConditions.Command("off");
        NetworkConditions.Command("ocean");
        Check(NetworkConditions.Active && NetworkConditions.PresetIndex == 1, "preset active");
        Check(NetLab.Values[4] == 120 && NetLab.Values[5] == 120, "latency on sends AND receives");
        Check(NetLab.Values[2] == 0.5f && NetLab.Values[3] == 0.5f, "loss on sends AND receives");
        Check(NetLab.Values[6] == 50 && NetLab.Values[7] == 50 && NetLab.Values[8] == 15, "documented jitter approximation");
        NetworkConditions.Command("set 37 8 1.2 3.4 70");
        Check(NetLab.Values[26] == 3.4f && NetLab.Values[27] == 3.4f && NetLab.Values[6] == 70, "duplication/reorder on both directions");
        int calls = NetLab.Calls;
        Check(NetworkConditions.Command("set -1 0 0").Contains("error") && NetLab.Calls == calls, "bad input never reaches native calls");
        NetLab.FailOnce = 5;
        Check(NetworkConditions.Command("mobile").Contains("error") && NetworkConditions.Current.Latency == 37 && NetLab.Values[4] == 37 && NetLab.Values[5] == 37, "partial failure rolls back");
        NetworkConditions.Command("off");
        foreach (int key in new[] { 2, 3, 4, 5, 6, 7, 26, 27 }) Check(NetLab.Values[key] == 0, "off clears both directions");
        Check(!NetworkConditions.Active && NetLab.LagInfo == "off", "indicator and client status reset");
        NetLab.DupUnsupported = true;
        Check(!NetworkConditions.Command("wifi").Contains("error"), "older API without duplication still supports presets");
        Check(NetworkConditions.Command("set 0 0 0 1").Contains("error") && NetworkConditions.Current.Latency == 60, "unsupported duplication refused and rolled back");
        using var state = JsonDocument.Parse(NetworkConditions.Command("status"));
        Check(!state.RootElement.GetProperty("duplicationSupported").GetBoolean(), "optional capability reported");
        NetworkConditions.Command("off");
        SteamTransport.SteamReady = false;
        Check(NetworkConditions.Command("ocean").Contains("error") && !NetworkConditions.Active, "Steam unavailable leaves off");
        Console.WriteLine("ALL PASS (" + _checks + " network-condition checks)");
    }
}

namespace TLDRevamp.Net
{
    static class SteamTransport { internal static bool SteamReady = true; }
    static class SteamCompat { internal static void Ensure() { } }
    public static partial class NetLab
    {
        internal static readonly Dictionary<int, float> Values = new Dictionary<int, float>();
        internal static int Calls, FailOnce = -1;
        internal static bool DupUnsupported;
        internal static string LagInfo;
        internal static bool SetInt(ESteamNetworkingConfigValue key, int value) => SetFloat(key, value);
        internal static bool SetFloat(ESteamNetworkingConfigValue key, float value)
        {
            Calls++; int n = (int)key;
            if (FailOnce == n) { FailOnce = -1; return false; }
            if (DupUnsupported && n >= 26 && n <= 28) return false;
            Values[n] = value; return true;
        }
    }
}
namespace TLDRevamp
{
        static class ReproRecorder { internal static bool Recording => false; internal static void Add(ReproRow row) { } internal static int Starts; internal static string Start() { Starts++; return ""; } internal static string Stop() => ""; }

}
namespace UnityEngine
{
    enum EventType { Repaint }
    class Event { internal static Event current = new Event(); internal EventType type; }
    static class GUI
    {
        internal static string LastLabel; internal static GUISkin skin = new GUISkin(); internal static Color color, backgroundColor; internal static int depth;
        internal static void DrawTexture(Rect r, object texture) { }
        internal static void Label(Rect r, GUIContent text, GUIStyle style) { LastLabel = text.text; }
        internal static bool Button(Rect r, string text, GUIStyle style) => false;
        public static string TextArea(Rect r, string text, int limit, GUIStyle style) => text;
    }
    static class Mathf { internal static float Clamp01(float n) => Math.Clamp(n, 0, 1); }
}