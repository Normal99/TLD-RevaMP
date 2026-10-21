using System;
using System.Globalization;
using Steamworks;
using UnityEngine;

namespace TLDRevamp
{
    internal static class NetworkConditions
    {
        internal static NetworkSimulation Current;
        internal static int PresetIndex;
        private static bool _uncertain;
        private static bool? _duplicationSupported;
        internal static bool Active => Current.Active || _uncertain;

        private static readonly CompactOverlay Bar = new CompactOverlay();
        internal static float BarHeight => Active ? Bar.Height : 0;
        internal static float BarWidth => Bar.Width;
        internal static void Measure()
        {
            if (Active) Bar.Measure("RevaMP · Network conditions", "Simulated network", Current.SendOnly ? "Host: all client sends · relays impaired on send" : "Host: all client links · relays impaired twice");
        }
        internal static void Settings()
        {
            RevampSettings.Entries.Add(new RevampSettings.Entry { Key = "simPreset", Label = "Network conditions", Kind = RevampSettings.Kind.Choice, RuntimeOnly = true,
                Options = new[] { "Off", "Ocean (120 / 15 ms, 0.5%)", "Bad wifi (60 / 40 ms, 3%)", "Mobile (200 / 60 ms, 5%)", "Custom" },
                Help = "Simulate conditions on this machine's Steam sends AND receives. Latency is added per direction, not an RTT target. " +
                    "On a host this affects every client link; relayed traffic is impaired twice. Only this machine shows the warning. " +
                    "Always off at startup. Steam's older API approximates jitter by delaying half the packets by the jitter value; this can reorder them.",
                Get = () => PresetIndex, Set = n => { if (n >= 0 && n < 4) Apply(NetworkSimulation.Preset(n), n); } });
            Milliseconds("simLatency", "Added latency (ms)", false);
            Milliseconds("simJitter", "Jitter (ms)", true);
            Percentage("simLoss", "Packet loss (%)", 0);
            Percentage("simDuplication", "Packet duplication (%)", 1);
            Percentage("simReorder", "Packet reorder (%)", 2);
        }
        private static void Milliseconds(string key, string label, bool jitter)
        {
            int[] values = { 0, 5, 10, 15, 20, 30, 40, 50, 60, 80, 100, 120, 150, 200, 300, 400, 600, 1000, 2000 };
            var options = new string[values.Length + 1]; for (int i = 0; i < values.Length; i++) options[i] = values[i].ToString(CultureInfo.InvariantCulture); options[values.Length] = "Custom (bridge)";
            RevampSettings.Entries.Add(new RevampSettings.Entry { Key = key, Label = label, Kind = RevampSettings.Kind.Choice, RuntimeOnly = true, Options = options,
                Help = jitter ? "Extra delay on half the packets, on sending and receiving. Uses Steam's reorder delay; always off at startup." : "Milliseconds added to both sending and receiving on this machine. Always off at startup.",
                Get = () => { int i = Array.IndexOf(values, jitter ? Current.Jitter : Current.Latency); return i >= 0 ? i : values.Length; },
                Set = n => { if (n < 0 || n >= values.Length) return; var v = Current; v.SendOnly = false; if (jitter) v.Jitter = values[n]; else v.Latency = values[n]; Apply(v); } });
        }
        private static void Percentage(string key, string label, int field)
        {
            float[] values = { 0, 0.1f, 0.5f, 1, 2, 3, 5, 10, 20, 50, 100 };
            var options = new string[values.Length + 1]; for (int i = 0; i < values.Length; i++) options[i] = values[i].ToString(CultureInfo.InvariantCulture); options[values.Length] = "Custom (bridge)";
            RevampSettings.Entries.Add(new RevampSettings.Entry { Key = key, Label = label, Kind = RevampSettings.Kind.Choice, RuntimeOnly = true, Options = options,
                Help = field == 2 ? "Packets given an extra delay (jitter delay, or 15 ms). Jitter already delays half the packets; a lower percentage cannot reduce that. Both directions; off at startup." :
                    "Percentage of packets lost or duplicated on both sending and receiving. Always off at startup.",
                Get = () => { int i = Array.IndexOf(values, field == 0 ? Current.Loss : field == 1 ? Current.Duplication : Current.Reorder); return i >= 0 ? i : values.Length; },
                Set = n => { if (n < 0 || n >= values.Length) return; var v = Current; v.SendOnly = false; if (field == 0) v.Loss = values[n]; else if (field == 1) v.Duplication = values[n]; else v.Reorder = values[n]; Apply(v); } });
        }
        internal static string State() => "{\"active\":" + (Active ? "true" : "false") + ",\"uncertain\":" + (_uncertain ? "true" : "false") +
            ",\"send\":true,\"receive\":" + (Current.SendOnly ? "false" : "true") + ",\"persist\":false,\"jitterUsesReorder\":true,\"duplicationSupported\":" +
            (_duplicationSupported.HasValue ? _duplicationSupported.Value ? "true" : "false" : "null") + ",\"conditions\":" + Current.Json() + "}";
        internal static string Apply(NetworkSimulation value, int preset = 4)
        {
            if (!value.Valid) return "{\"error\":\"latency/jitter 0..2000 ms; percentages 0..100\"}";
            if (!Net.SteamTransport.SteamReady) return "{\"error\":\"steam not initialized\"}";
            try
            {
                Net.SteamCompat.Ensure();
                if (!Native(value))
                {
                    _uncertain = !Native(Current);
                    return "{\"error\":\"Steam simulation config refused\",\"state\":" + State() + "}";
                }
                Current = value; PresetIndex = value.Active ? preset : 0; _uncertain = false;
                Net.NetLab.LagInfo = !value.Active ? "off" : value.SendOnly ?
                    (value.Latency * 2) + " ms RTT, " + value.Loss + " % loss, " + value.Reorder + " % +" + value.Jitter + " ms" : value.Json();
                if (ReproRecorder.Recording) ReproRecorder.Add(ReproRow(value));
                return State();
            }
            catch { _uncertain = true; return "{\"error\":\"Steam simulation config failed\",\"state\":" + State() + "}"; }
        }
        internal static ReproRow ReproRow(NetworkSimulation v) => new ReproRow { Kind = ReproKind.Network, X = v.Latency, Y = v.Jitter, A = v.Loss, B = v.Duplication, C = v.Reorder, D = v.SendOnly ? 1 : 0 };
        private static bool Native(NetworkSimulation v)
        {
            // Utils003 has no jitter control. Half the packets get an extra jitter-sized reorder delay.
            float reorder = !v.SendOnly && v.Jitter > 0 ? Math.Max(50f, v.Reorder) : v.Reorder;
            bool ok = Net.NetLab.SetInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLag_Send, v.Latency)
                & Net.NetLab.SetInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLag_Recv, v.SendOnly ? 0 : v.Latency)
                & Net.NetLab.SetFloat(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLoss_Send, v.Loss)
                & Net.NetLab.SetFloat(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLoss_Recv, v.SendOnly ? 0 : v.Loss)
                & Net.NetLab.SetFloat(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketReorder_Send, reorder)
                & Net.NetLab.SetFloat(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketReorder_Recv, v.SendOnly ? 0 : reorder)
                & Net.NetLab.SetInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketReorder_Time, v.SendOnly || v.Jitter > 0 ? v.Jitter : 15);
            bool dup = Net.NetLab.SetFloat(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketDup_Send, v.Duplication)
                & Net.NetLab.SetFloat(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketDup_Recv, v.SendOnly ? 0 : v.Duplication)
                & Net.NetLab.SetInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketDup_TimeMax, 15);
            _duplicationSupported = dup;
            return ok && (dup || v.Duplication == 0);
        }
        internal static string Command(string arg)
        {
            var a = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0 || a[0] == "status") return State();
            if (a.Length == 1)
            {
                int p = a[0] == "off" ? 0 : a[0] == "ocean" ? 1 : a[0] == "wifi" ? 2 : a[0] == "mobile" ? 3 : -1;
                if (p >= 0) return Apply(NetworkSimulation.Preset(p), p);
            }
            if (a.Length >= 4 && a.Length <= 7 && a[0] == "set")
                return Apply(new NetworkSimulation { Latency = int.Parse(a[1], CultureInfo.InvariantCulture), Jitter = int.Parse(a[2], CultureInfo.InvariantCulture),
                    Loss = float.Parse(a[3], CultureInfo.InvariantCulture), Duplication = a.Length > 4 ? float.Parse(a[4], CultureInfo.InvariantCulture) : 0,
                    Reorder = a.Length > 5 ? float.Parse(a[5], CultureInfo.InvariantCulture) : 0, SendOnly = a.Length > 6 && a[6] == "1" });
            return "{\"error\":\"net conditions [status|off|ocean|wifi|mobile|set latencyMs jitterMs lossPct [dupPct reorderPct [sendOnly:0|1]]]\"}";
        }
        internal static void Draw()
        {
            if (!Active || Event.current.type != EventType.Repaint) return;
            if (DebugLayout.Network.H <= 0) return;
            Color color = GUI.color; int depth = GUI.depth; GUI.depth = -10000;
            Bar.Draw(DebugLayout.Network, OverlayKind.Warning);
            GUI.color = color; GUI.depth = depth;
        }
    }
}
