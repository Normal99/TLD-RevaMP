using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// The server's admin console (architecture §8): a short, fixed list of commands, never the debug bridge's
    /// reflection. Three ways in:
    ///  - the dedicated server's console file: lines appended to BepInEx/TLDRevampServer/commands.txt run in order,
    ///    their answers go to console.txt next to it (and the log). A server has no window to type in.
    ///  - chat: the host, and players whose Steam IDs are listed in [Server] Admins, type /kick, /ban, /save and the
    ///    rest; anyone else is told it is for admins.
    ///  - the bridge (`admin <command>`), for the tests.
    /// Bans last across sessions (bans.txt in the same folder, one Steam ID per line, written by /ban and /unban);
    /// a kick lasts until the session ends.
    public sealed partial class MpServer
    {
        public static readonly HashSet<ulong> Admins = new HashSet<ulong>();
        public static readonly HashSet<ulong> Bans = new HashSet<ulong>();
        public static long AdminCommands, AdminRefused;
        private const string Banned = "You are banned from this server.";
        public static string Dir => Path.Combine(BepInEx.Paths.BepInExRootPath, "TLDRevampServer");
        private static string BansFile => Path.Combine(Dir, "bans.txt");
        private static readonly float _startedAt = Time.realtimeSinceStartup;

        public static void LoadBans()
        {
            Bans.Clear();
            try
            {
                if (!File.Exists(BansFile)) return;
                foreach (var line in File.ReadAllLines(BansFile))
                {
                    var t = line.Split('#')[0].Trim();
                    if (ulong.TryParse(t, out var id) && id != 0) Bans.Add(id);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("admin: reading bans: " + e.Message); }
        }

        private static void SaveBans()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder("# Steam IDs banned from this server (one per line; /unban removes one)\n");
                foreach (var id in Bans) sb.Append(id).Append('\n');
                File.WriteAllText(BansFile, sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning("admin: writing bans: " + e.Message); }
        }

        /// [Server] Admins: Steam IDs separated by commas or spaces.
        public static void SetAdmins(string list)
        {
            Admins.Clear();
            foreach (var t in (list ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (ulong.TryParse(t.Trim(), out var id) && id != 0) Admins.Add(id);
        }

        private bool IsAdmin(int from) => from == 0 || ById(from, out var p) && p.Saved && Admins.Contains(p.SteamId);

        /// A chat command that isn't /tpa's: an admin command when the player is one.
        private bool AdminChat(int from, string text)
        {
            var parts = text.Substring(1).Trim().Split(new[] { ' ' }, 2);
            var name = parts[0].ToLowerInvariant();
            if (Array.IndexOf(Commands, name) < 0) return false;
            // "/tpa on" and "/tpa off" are the admin switch; "/tpa <name>" is anyone's teleport request (TpaCommand)
            if (name == "tpa")
            {
                string a = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "";
                if (a != "on" && a != "off") return false;
            }
            if (!IsAdmin(from)) { AdminRefused++; Tell(from, "/" + name + " is for the server's admins."); return true; }
            foreach (var line in Run(text.Substring(1), from).Split('\n')) Tell(from, line);
            return true;
        }

        private static readonly string[] Commands = { "help", "players", "kick", "ban", "unban", "bans", "say", "save", "password", "maxplayers", "tpa", "saved", "forget", "status", "stop" };

        /// Run one admin command; the answer (lines). `by`: who asked (0 = the server itself or its console).
        public string Run(string line, int by = 0)
        {
            var parts = (line ?? "").Trim().Split(new[] { ' ' }, 2);
            string cmd = parts[0].ToLowerInvariant().TrimStart('/'), arg = parts.Length > 1 ? parts[1].Trim() : "";
            AdminCommands++;
            // The password never goes into the log: the command line is shown with the secret replaced.
            var logArg = cmd == "password" ? SessionSettingsRules.RedactedPassword : SessionSettingsRules.RedactSecret(arg, Password);
            Plugin.Log.LogInfo($"admin: {NameOf(by)}: {cmd} {logArg}");
            switch (cmd)
            {
                case "help":
                    return "players | kick <name> | ban <name or Steam ID> | unban <Steam ID> | bans | say <text> | save | password <text or off> | " +
                           "maxplayers <n> | tpa on/off | saved | forget <name> | status | stop";
                case "players":
                {
                    var sb = new StringBuilder();
                    var list = new List<Session.Player>(); Players(list);
                    sb.Append(list.Count).Append(" player(s)");
                    foreach (var p in list)
                    {
                        sb.Append('\n').Append(p.Id).Append("  ").Append(p.Name).Append(p.Host ? "  (server)" : "  ping " + p.Ping + " ms").Append("  ").Append(p.SteamId);
                        if (!p.Host && ById(p.Id, out var peer) && peer.HasPos) sb.Append($"  at {peer.Pos.x / 1000.0:F1}, {peer.Pos.z / 1000.0:F1} km");
                    }
                    return sb.ToString();
                }
                case "kick":
                {
                    int id = Who(arg, out var err); if (id < 0) return err;
                    if (id == 0) return "The server can't kick itself.";
                    string n = NameOf(id);
                    return Kick(id) ? $"{n} was kicked (back after this session)." : "Couldn't kick " + n + ".";
                }
                case "ban":
                {
                    if (ulong.TryParse(arg, out var sid) && sid > 1000)
                    {
                        Bans.Add(sid); SaveBans();
                        foreach (var o in _peers.Values) if (o.Ready && o.Saved && o.SteamId == sid) { Kick(o.Id, Banned); break; }
                        return $"Steam ID {sid} is banned.";
                    }
                    int id = Who(arg, out var err); if (id < 0) return err;
                    if (id == 0) return "The server can't ban itself.";
                    if (!ById(id, out var p)) return "No such player.";
                    if (!p.Saved) return p.Name + " has no Steam-checked ID: kicked instead.\n" + (Kick(id) ? "Kicked." : "");
                    string n = p.Name; ulong s = p.SteamId;
                    Bans.Add(s); SaveBans(); Kick(id, Banned);
                    return $"{n} is banned ({s}).";
                }
                case "unban":
                    if (!ulong.TryParse(arg, out var u) || !Bans.Remove(u)) return "Not banned: " + arg;
                    SaveBans();
                    _kicked.Remove(u);   // the ban's kick too: unbanned, they can come back now
                    return $"{u} is no longer banned.";
                case "bans":
                    return Bans.Count == 0 ? "Nobody is banned." : string.Join("\n", Bans);
                case "say":
                    if (arg.Length == 0) return "say what?";
                    Notice("Server: " + Session.Clean(arg));
                    return "Said.";
                case "save":
                    return "Saved: " + Mp.SaveTail();
                case "password":
                    SetSessionPassword(arg == "off" || arg == "-" ? "" : arg, persistDefault: false);
                    return Password.Length == 0 ? "No password now." : "Password set (for new joins).";
                case "maxplayers":
                    if (!int.TryParse(arg, out var mx) || mx < 2 || mx > 64) return "maxplayers 2 to 64";
                    MaxPlayers = mx;
                    return "At most " + mx + " players (the server counts as one).";
                case "tpa":
                    if (arg == "on") Tpa.Enabled = true; else if (arg == "off") Tpa.Enabled = false;
                    SessionSettings.TpaEnabled = Tpa.Enabled;
                    if (Tpa.Enabled) SessionSettings.TpaCooldownS = Tpa.CooldownS;
                    SessionSettings.Revision++;
                    PublishSessionSettings();
                    return "Teleport requests " + (Tpa.Enabled ? "on" : "off") + ".";
                case "saved":
                {
                    var away = PlayerSave.AwayPlayers();
                    if (away.Count == 0) return "No saved players away.";
                    var sb = new StringBuilder();
                    foreach (var a in away) sb.Append(a.Name).Append(a.Dead ? "  (died)" : "").Append($"  carries {a.Items}").Append("  ").Append(a.Steam).Append('\n');
                    return sb.ToString().TrimEnd();
                }
                case "forget":
                {
                    var away = PlayerSave.AwayPlayers();
                    var hit = away.FindAll(a => string.Equals(a.Name, arg, StringComparison.OrdinalIgnoreCase) || a.Steam.ToString() == arg);
                    if (hit.Count != 1) return hit.Count == 0 ? "No saved player away named " + arg + "." : "More than one matches: use the Steam ID.";
                    return PlayerSave.Forget(hit[0].Steam) ? hit[0].Name + " was forgotten: they join as new next time." : "Couldn't forget " + hit[0].Name + ".";
                }
                case "status":
                {
                    var list = new List<Session.Player>(); Players(list);
                    return $"up {(Time.realtimeSinceStartup - _startedAt) / 60f:F0} min, {list.Count} player(s) of {MaxPlayers}, frame {Time.unscaledDeltaTime * 1000f:F1} ms, " +
                           $"{Entities.ServerObjects} shared objects, password {(Password.Length > 0 ? "on" : "off")}, teleport requests {(Tpa.Enabled ? "on" : "off")}, " +
                           $"bans {Bans.Count}, autosaves {DedicatedServer.AutoSaves}{(DedicatedServer.Enabled ? ", dedicated" : "")}";
                }
                case "stop":
                    if (!DedicatedServer.Enabled) return "stop is for a dedicated server (a host quits from the menu).";
                    DedicatedServer.StopRequested = true;
                    return "Saving and stopping.";
                default:
                    return "Unknown command: " + cmd + " (help lists them)";
            }
        }

        private int Who(string arg, out string err)
        {
            err = "";
            if (arg.Length == 0) { err = "Which player?"; return -1; }
            if (int.TryParse(arg, out var id) && (id == 0 || ById(id, out _))) return id;
            int f = FindPlayer(arg, -1);
            if (f == -2) err = "More than one player matches \"" + arg + "\".";
            else if (f < 0) err = "No player named \"" + arg + "\" here.";
            return f;
        }
    }

    /// The dedicated server's console file (MpServer.Run): commands.txt in, console.txt out.
    public static class AdminConsole
    {
        private static long _offset = -1;
        private static float _at;
        public static string CommandsFile => Path.Combine(MpServer.Dir, "commands.txt");
        public static string OutFile => Path.Combine(MpServer.Dir, "console.txt");

        /// Once per frame on a dedicated server (DedicatedServer.Tick): new lines, every second.
        public static void Tick()
        {
            if (Mp.Server == null || Time.realtimeSinceStartup - _at < 1f) return;
            _at = Time.realtimeSinceStartup;
            try
            {
                if (_offset < 0)
                {
                    Directory.CreateDirectory(MpServer.Dir);
                    if (!File.Exists(CommandsFile)) File.WriteAllText(CommandsFile, "");
                    _offset = new FileInfo(CommandsFile).Length;   // what was there before this start is not run again
                    Out("server up: type commands into commands.txt (one per line); help lists them");
                    return;
                }
                var len = new FileInfo(CommandsFile).Length;
                if (len < _offset) _offset = 0;   // emptied: from the top
                if (len == _offset) return;
                string text;
                using (var f = new FileStream(CommandsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    f.Seek(_offset, SeekOrigin.Begin);
                    var buf = new byte[Math.Min(len - _offset, 64 * 1024)];
                    int n = f.Read(buf, 0, buf.Length);
                    int end = Array.LastIndexOf(buf, (byte)'\n', n - 1);
                    if (end < 0) return;   // a line still being written
                    text = Encoding.UTF8.GetString(buf, 0, end + 1);
                    _offset += end + 1;
                }
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    // The console echo is written to the log AND appended to BepInEx/TLDRevampServer/console.txt,
                    // so a raw echo would persist the host's password on disk. The command is shown with the
                    // secret replaced; every other command is left alone.
                    // The console echo is written to the log AND appended to BepInEx/TLDRevampServer/console.txt,
                    // so a raw echo would persist the host's password on disk. The command is shown with the
                    // secret replaced; every other command is left alone.
                    Out("> " + Net.SessionSettingsRules.RedactCommand(line, Mp.Server?.Password));
                    Out(Mp.Server.Run(line));
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("admin console: " + e.Message); }
        }

        private static void Out(string s)
        {
            Plugin.Log.LogInfo("admin console: " + s.Replace("\n", " | "));
            try { File.AppendAllText(OutFile, DateTime.Now.ToString("HH:mm:ss ") + s.Replace("\n", "\n         ") + "\n"); } catch { }
        }

        public static void Reset() { _offset = -1; }
    }
}
