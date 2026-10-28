using System;
using System.Globalization;
using UnityEngine;

namespace TLDRevamp
{
    internal static class ReproPlayback
    {
        internal static bool Active;
        private static bool _oldSave, _oldBackground;
        internal static string Command(string arg)
        {
            var a = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0 || a[0] == "status") return "{\"playback\":" + (Active ? "true" : "false") + ",\"isolated\":" + (Isolated ? "true" : "false") +
                ",\"dedicated\":" + (Net.DedicatedServer.Enabled ? "true" : "false") + ",\"bridge\":" + (Plugin.BridgeEnabled ? "true" : "false") +
                ",\"localPlayer\":" + (mainscript.s != null && mainscript.s.player != null ? "true" : "false") + ",\"protocol\":" + Net.Protocol.Version + ",\"session\":" + Json.Str(SessionLog.Sid) + ",\"recorder\":" + ReproRecorder.State() + "}";
            if (a[0] == "start" || a[0] == "on") return ReproRecorder.Start();
            if (a[0] == "stop" || a[0] == "off") return ReproRecorder.Stop();
            if (a[0] == "arm")
            {
                ReproRecorder.DiscardAutomatic();
                if (ReproRecorder.Recording || !Isolated || Net.DedicatedServer.Enabled || !Plugin.BridgeEnabled || !Net.Entities.InSession || mainscript.s == null || mainscript.s.player == null)
                    return "{\"error\":\"playback needs an isolated player copy with bridge enabled in a multiplayer world; stop recording first\"}";
                if (!Active) { _oldSave = TestSafety.BlockAutoSave; _oldBackground = Application.runInBackground; }
                TestSafety.BlockAutoSave = true; Application.runInBackground = true; Active = true; return Command("status");
            }
            if (a[0] == "end")
            {
                if (Active) { TestSafety.BlockAutoSave = _oldSave; Application.runInBackground = _oldBackground; Net.Entities.ReproReleaseHeld(); }
                Active = false; return Command("status");
            }
            if (!Active) return "{\"error\":\"repro arm first\"}";
            if (a[0] == "resolve" && a.Length == 5) return Net.Entities.ReproResolve(int.Parse(a[1], CultureInfo.InvariantCulture), N(a[2]), N(a[3]), N(a[4]));
            if (a[0] == "check" && a.Length == 2) return Net.Entities.ReproCheck(uint.Parse(a[1], CultureInfo.InvariantCulture));
            if (a[0] == "peer" && a.Length == 1) return Peer();
            if (a[0] == "players" && a.Length == 1) return Net.RemotePlayers.ReproState();
            if (a[0] == "poicheck" && a.Length == 6) return Net.Entities.ReproPoiCheck(int.Parse(a[1], CultureInfo.InvariantCulture), N(a[2]), N(a[3]), N(a[4]), int.Parse(a[5], CultureInfo.InvariantCulture));
            if (a[0] == "act" && a.Length == 17)
            {
                var r = new ReproRow { Kind = (ReproKind)int.Parse(a[1], CultureInfo.InvariantCulture), Net = uint.Parse(a[2], CultureInfo.InvariantCulture),
                    Other = uint.Parse(a[3], CultureInfo.InvariantCulture), Item = int.Parse(a[4], CultureInfo.InvariantCulture), Slot = int.Parse(a[5], CultureInfo.InvariantCulture),
                    X = N(a[6]), Y = N(a[7]), Z = N(a[8]), A = N(a[9]), B = N(a[10]), C = N(a[11]), D = N(a[12]), E = N(a[13]), F = N(a[14]), G = N(a[15]), H = N(a[16]) };
                if (!r.Valid) return "{\"error\":\"invalid repro row\"}";
                if (r.Kind == ReproKind.Command) return ReplayCommand((int)r.A);
                if (r.Kind == ReproKind.Mark) return ReplayMark();
                if (r.Kind == ReproKind.Network) return NetworkConditions.Apply(new NetworkSimulation { Latency = (int)r.X, Jitter = (int)r.Y, Loss = (float)r.A, Duplication = (float)r.B, Reorder = (float)r.C, SendOnly = r.D == 1 });
                bool was = Net.Entities.TestAsPlayer;
                try { Net.Entities.TestAsPlayer = true; return Net.Entities.ReproAct(r); }
                finally { Net.Entities.TestAsPlayer = was; }
            }
            return "{\"error\":\"repro [start|stop|status|arm|end|resolve prefab x y z|check net|peer|act row]\"}";
        }
        private static double N(string s)
        {
            double n = double.Parse(s, CultureInfo.InvariantCulture);
            if (double.IsNaN(n) || double.IsInfinity(n)) throw new FormatException("nonfinite repro number"); return n;
        }
        // TLD Copies' CopyIsolation, or a private build of it under another prefix: any *.copyisolation plugin
        private static bool Isolated
        {
            get
            {
                foreach (var guid in BepInEx.Bootstrap.Chainloader.PluginInfos.Keys)
                    if (guid.EndsWith(".copyisolation", System.StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }
        private static string ReplayMark()
        {
            string reply = Net.TelemetryMarks.Local("");
            return reply == "{\"error\":\"one mark per 10 seconds\"}" ? "{\"ok\":true,\"skipped\":true}" : reply;
        }
        private static string Peer()
        {
            int peers = 0;
            foreach (var p in Net.Session.Players()) if (!p.Me) peers++;
            return "{\"peers\":" + peers + "}";
        }
        private static string ReplayCommand(int code)
        {
            if (code == 0) return "{\"error\":\"command arguments were omitted for privacy; command cannot be replayed\"}";
            if (code == 1)
            {
                string target = null;
                foreach (var p in Net.Session.Players()) if (!p.Me) { if (target != null) return "{\"error\":\"tpa requires exactly one peer\"}"; target = p.Name; }
                if (target == null) return "{\"error\":\"no tpa peer\"}";
                return Net.Session.Say("/tpa " + target) ? "{\"ok\":true}" : "{\"error\":\"command refused\"}";
            }
            if (code == 4) return ReplayMark();
            string command = code == 2 ? "/tpaccept" : code == 3 ? "/tpdeny" : null;
            if (command == null) return "{\"error\":\"unknown command code\"}";
            return Net.Session.Say(command) ? "{\"ok\":true}" : "{\"error\":\"command refused\"}";
        }
    }
}
