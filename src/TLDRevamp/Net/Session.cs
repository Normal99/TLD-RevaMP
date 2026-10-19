using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// The session around the game: who may join (password, kicked players, a full game), text chat with join/leave
    /// notices, the player list with pings (Roster), what friends see in Steam (rich presence), and how a session ends
    /// for a client — kicked, rejected or the host gone: back to the main menu with the reason, never left standing in
    /// a copy of the host's world that would autosave over the player's own saves.
    public sealed partial class MpServer
    {
        public string Password = "";
        public static int MaxPlayers = 16;   // the host included
        public const int ChatMaxLen = 160;
        private readonly HashSet<ulong> _kicked = new HashSet<ulong>();
        private Peer _kickedNow;
        private readonly List<(HSteamNetConnection c, float at)> _closing = new List<(HSteamNetConnection, float)>();
        private float _rosterAcc;

        /// Who is joining. The Hello carries a Steam ID the joiner chose: it picked the player save (where they stand,
        /// what is in their pocket), the kick list and the voice mutes, so anyone could join as someone else and take
        /// their pocket. The connection itself says who Steam authenticated (SteamCompat.RemoteSteamId): that one counts.
        /// Refused: no ID claimed, a claim that isn't the connection's account, an account already in this game, a
        /// player whose ID changes on a later Hello. Admitted without a player save: a connection Steam didn't
        /// authenticate (an IP join without it) and this machine's own account (the test bots, a second copy here).
        /// TestSameAccount (off; only the debug bridge sets it, never saved): another machine's bots share that machine's
        /// account, so a test host lets an account in more than once, the extra ones without a player save.
        public static long IdentityRefused, IdentityUnsaved;
        public static bool TestSameAccount;
        private static bool _unsavedLogged;
        private bool Identify(HSteamNetConnection c, Peer p, ulong claimed, string name, out ulong steamId, out bool saved)
        {
            steamId = 0; saved = false;
            ulong auth = SteamCompat.RemoteSteamId(c, out int type);
            string who = Session.CleanName(name);
            Plugin.Log.LogInfo($"identity: {who} — the connection's identity type {type}, {(auth == 0 ? "no Steam ID" : auth == claimed ? "the Steam ID claimed" : "ANOTHER Steam ID than claimed")}");
            string why = null;
            if (claimed == 0) why = "Your game sent no Steam ID.";
            else if (auth != 0 && auth != claimed) why = "Your Steam ID doesn't match your connection.";
            ulong id = auth != 0 ? auth : claimed;
            bool local = auth != 0 && auth == Mp.MySteamId();
            if (why == null && p != null && p.SteamId != 0 && p.SteamId != id) why = "Your Steam ID changed.";
            bool again = false;
            if (why == null && auth != 0 && !local)
                foreach (var o in _peers.Values)
                    if (o != p && o.SteamId == auth) { again = true; if (!TestSameAccount) why = "Your Steam account is already in this game."; break; }
            if (why != null)
            {
                IdentityRefused++;
                Plugin.Log.LogWarning($"[anomaly] identity: {who} refused: {why}");
                Reject(c, why, Protocol.RejectOther);
                return false;
            }
            steamId = id; saved = auth != 0 && !local && !again;
            if (!saved)
            {
                IdentityUnsaved++;
                if (!_unsavedLogged) { _unsavedLogged = true; Plugin.Log.LogInfo($"identity: {who} has {(local ? "this machine's own Steam account" : again ? "an account already in the game (test bots)" : "no Steam-authenticated identity")}: joins without a player save (logged once)"); }
            }
            return true;
        }

        /// Hello passed the version check and Identify: may this player in?
        private bool Admit(HSteamNetConnection c, Peer p, ulong sid, string pw)
        {
            // a ban first: banned also kicks, and the player should read why (serveradmin run 3: "kicked")
            if (sid != 0 && Bans.Contains(sid)) { Reject(c, Banned, Protocol.RejectKicked); return false; }
            if (sid != 0 && _kicked.Contains(sid)) { Reject(c, "You were kicked from this game.", Protocol.RejectKicked); return false; }
            if (Password.Length > 0 && pw != Password)
            {
                Reject(c, pw.Length == 0 ? "This game has a password." : "Wrong password.", Protocol.RejectPassword);
                return false;
            }
            int others = 0;
            foreach (var o in _peers.Values) if (o != p) others++;
            if (others + 1 >= MaxPlayers) { Reject(c, "The game is full (" + MaxPlayers + " players).", Protocol.RejectFull); return false; }
            return true;
        }

        /// The reason goes out reliably; the connection closes a moment later (closing at once can drop the message).
        private void Reject(HSteamNetConnection c, string reason, byte code)
        {
            _w.Reset(); _w.U8(Protocol.Reject); _w.Str(reason); _w.U8(code);
            T.Send(c, _w, SteamTransport.SendReliable);
            Rejected++;
            _closing.Add((c, Time.realtimeSinceStartup + 2f));
        }

        /// The host's kick: out now, and not back in for the rest of this session.
        public bool Kick(int id, string why = "You were kicked by the host.")
        {
            Peer p = null;
            foreach (var o in _peers.Values) if (o.Id == id) { p = o; break; }
            if (p == null) return false;
            if (p.Saved) _kicked.Add(p.SteamId);   // an unauthenticated claim could put someone else's account on the list
            Reject(p.Conn, why, Protocol.RejectKicked);
            _kickedNow = p;
            try { OnDisconnected(p.Conn); } finally { _kickedNow = null; }
            return true;
        }

        private void SessionTick()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = _closing.Count - 1; i >= 0; i--)
            {
                if (now < _closing[i].at) continue;
                T.Close(_closing[i].c, "rejected");
                _closing.RemoveAt(i);
            }
            _rosterAcc += Time.unscaledDeltaTime;
            if (_rosterAcc < 2f) return;
            _rosterAcc = 0f;
            // Roster: everyone's ping, for the player list (the host's own is 0)
            _w.Reset(); _w.U8(Protocol.Roster);
            int n = 0; foreach (var o in _peers.Values) if (o.Ready) n++;
            _w.U8((byte)Mathf.Min(n, 255));
            foreach (var o in _peers.Values)
            {
                if (!o.Ready) continue;
                int ping = T.Status(o.Conn).Ping;
                _w.VarU32((uint)o.Id); _w.U16((ushort)Mathf.Clamp(ping, 0, 65535));
                Session.Pings[o.Id] = ping;
            }
            foreach (var o in _peers.Values) if (o.Ready) T.Send(o.Conn, _w, SteamTransport.SendReliable);
        }

        private void ChatFrom(Peer p, NetReader r)
        {
            string text = Session.Clean(r.Str());
            if (r.Bad || text.Length == 0) return;
            float now = Time.realtimeSinceStartup;
            while (p.ChatTimes.Count > 0 && now - p.ChatTimes.Peek() > 5f) p.ChatTimes.Dequeue();
            if (p.ChatTimes.Count >= 5) { if (Tpa.CommandKind(text) != null) SessionLog.Event("tpa-failure", from: p.Id, result: "command flood limit"); ChatDropped++; return; }   // flood: 5 lines in 5 s
            p.ChatTimes.Enqueue(now);
            if (text.StartsWith("/")) { if (!AdminChat(p.Id, text) && !MarkCommand(p.Id, text)) TpaCommand(p.Id, text); return; }   // a command (Admin, Tpa): for the server only
            Broadcast(0, p.Name, text);
        }

        internal void HostChat(string text) { if (text.StartsWith("/")) { if (!AdminChat(0, text) && !MarkCommand(0, text)) TpaCommand(0, text); } else Broadcast(0, Session.MyName(), text); }
        internal void Notice(string text) => Broadcast(1, "", text);
        public long ChatRelayed, ChatDropped;

        private void Broadcast(byte kind, string name, string text)
        {
            _w.Reset(); _w.U8(Protocol.Chat); _w.U8(kind); _w.Str(name); _w.Str(text);
            foreach (var o in _peers.Values) if (o.Ready) T.Send(o.Conn, _w, SteamTransport.SendReliable);
            ChatRelayed++;
            Session.Show(kind, name, text);
        }

        /// The players in this session for the menu: the host first.
        internal void Players(List<Session.Player> into)
        {
            into.Add(new Session.Player { Id = 0, Name = Session.MyName(), SteamId = Mp.MySteamId(), Ping = 0, Host = true, Me = true });
            foreach (var o in _peers.Values)
                if (o.Ready) into.Add(new Session.Player { Id = o.Id, Name = o.Name, SteamId = o.SteamId, Ping = T.Status(o.Conn).Ping });
        }

        internal int ReadyCount() { int n = 0; foreach (var o in _peers.Values) if (o.Ready) n++; return n; }
    }

    public static class Session
    {
        public sealed class Player { public int Id; public string Name; public ulong SteamId; public int Ping; public bool Host, Me; }
        public sealed class Line { public byte Kind; public string Name, Text; public float At; }

        public static readonly List<Line> Lines = new List<Line>();
        internal static readonly Dictionary<int, int> Pings = new Dictionary<int, int>();
        /// Why the last session ended for this player (kicked, wrong password, host gone), shown in the menu once.
        public static string EndReason; public static byte EndCode;
        internal static bool ShowInMenu; internal static menuhandler ShowInMenuNotOn;   // the old scene's, until the menu scene replaces it
        /// The last game this player tried to join, so the menu can ask for its password and try again.
        public static ulong LastJoinSteamId; public static string LastJoinAddress;
        public static long ChatShown;
        public static Action<Line> LineAdded;

        public static string MyName() { string name = SteamTransport.SteamReady ? SteamFriends.GetPersonaName() : "player"; TelemetryPrivacy.Name(name); return name; }

        /// What a player may say: one line, no control characters, at most ChatMaxLen.
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(Math.Min(s.Length, MpServer.ChatMaxLen));
            foreach (char ch in s)
            {
                if (sb.Length >= MpServer.ChatMaxLen) break;
                if (char.IsControl(ch)) continue;
                // the chat box renders rich text: a player's "<size=300>" or "<color=...>" took over everyone's screen
                sb.Append(ch == '<' ? '\u2039' : ch);
            }
            return sb.ToString().Trim();
        }

        /// A player's name as everyone shows it (notices, the player list, the name over their head): cleaned like a chat
        /// line, at most 32 characters (Steam's own limit) — names came through as sent, any length, tags and all.
        public const int NameMaxLen = 32;
        public static string CleanName(string s)
        {
            TelemetryPrivacy.Name(s);
            var c = Clean(s);
            if (c.Length > NameMaxLen) c = c.Substring(0, NameMaxLen).Trim();
            TelemetryPrivacy.Name(c);
            return c.Length > 0 ? c : "Player";
        }

        /// Say something (the chat box): the host broadcasts it itself, a client sends it to the host.
        public static bool Say(string text)
        {
            text = Clean(text);
            if (text.Length == 0) return false;
            if (Mp.Server == null && Mp.Client == null && (text.Equals("/mark", System.StringComparison.OrdinalIgnoreCase) || text.StartsWith("/mark ", System.StringComparison.OrdinalIgnoreCase))) { TelemetryMarks.Local(text.Length > 5 ? text.Substring(6) : ""); return true; }
            if (Mp.Server != null) { Mp.Server.HostChat(text); return true; }
            var c = Mp.Client;
            if (c == null || !c.Ready) { if (Tpa.CommandKind(text) != null) SessionLog.Event("tpa-failure", result: "not connected or ready"); return false; }
            c.SendChat(text);
            return true;
        }

        internal static void ChatIn(NetReader r)
        {
            byte kind = r.U8(); string name = r.Str(); string text = r.Str();
            if (r.Bad) return;
            Show(kind, Clean(name), Clean(text));
        }

        internal static void RosterIn(NetReader r)
        {
            int n = r.U8();
            for (int i = 0; i < n && !r.Bad; i++) { int id = (int)r.VarU32(); int ping = r.U16(); if (!r.Bad) Pings[id] = ping; }
        }

        internal static void Show(byte kind, string name, string text)
        {
            var l = new Line { Kind = kind, Name = name, Text = text, At = Time.unscaledTime };
            Lines.Add(l);
            if (Lines.Count > 100) Lines.RemoveAt(0);
            ChatShown++;
            Plugin.Log.LogInfo("chat: " + (kind == 0 ? name + ": " : "* ") + text);
            try { LineAdded?.Invoke(l); } catch (Exception e) { Plugin.Log.LogWarning("chat line: " + e.Message); }
        }

        /// Everyone in this session (the host first), for the menu.
        public static List<Player> Players()
        {
            var list = new List<Player>();
            if (Mp.Server != null) { Mp.Server.Players(list); return list; }
            var c = Mp.Client;
            if (c == null || !c.Ready) return list;
            foreach (var rp in RemotePlayers.Each())
                list.Add(new Player { Id = rp.id, Name = rp.name, SteamId = rp.steamId, Host = rp.id == 0, Ping = Pings.TryGetValue(rp.id, out int pg) ? pg : -1 });
            list.Add(new Player { Id = c.MyId, Name = MyName(), SteamId = Mp.MySteamId(), Me = true, Ping = Pings.TryGetValue(c.MyId, out int mp) ? mp : -1 });
            list.Sort((a, b) => a.Host != b.Host ? (a.Host ? -1 : 1) : a.Id.CompareTo(b.Id));
            return list;
        }

        public static bool InGame => (Mp.Server != null) || (Mp.Client != null && Mp.Client.Ready);

        /// Runner (Mp.Tick): a client's session that ended — rejected, kicked, or the host gone.
        internal static void Tick()
        {
            Presence();
            var c = Mp.Client;
            if (c == null) return;
            bool rejected = c.RejectReason != null && !c.Lost && c.MyId <= 0;   // before the game: wrong password, full...
            bool kicked = c.RejectCode == Protocol.RejectKicked;
            if (!c.Lost && !rejected && !kicked) return;
            EndReason = c.RejectReason ?? "Lost connection to the host.";
            EndCode = c.RejectCode;
            bool inHostWorld = c.MyId > 0 && TestSafety.InHostWorld;
            Plugin.Log.LogInfo("MP session ended: " + EndReason + (inHostWorld ? " — back to the main menu" : ""));
            Mp.Stop();
            if (inHostWorld) TestSafety.LeavingHostWorld = true;   // still the host's world until the menu is in
            Show(1, "", EndReason);
            // the world here is the host's (never saved): back to the menu, as when leaving
            if (inHostWorld) { ShowInMenu = true; ShowInMenuNotOn = menuhandler.s; LeaveToMenu(); }
        }

        /// Leaving the host's world: the main menu (the copy of the host's world must not autosave over the player's own).
        public static void LeaveToMenu()
        {
            try
            {
                if (menuhandler.s != null && DataFromMenuScript.s != null && !DataFromMenuScript.s.mainmenu) menuhandler.s.PressedMainMenu();
            }
            catch (Exception e) { Plugin.Log.LogWarning("leave to menu: " + e.Message); }
        }

        /// The player's own Leave (menu): a client goes back to the main menu; a host stops hosting and plays on.
        public static void Leave()
        {
            bool client = Mp.Client != null && Mp.Client.MyId > 0;
            bool inHostWorld = client && TestSafety.InHostWorld;
            Mp.Stop();
            if (inHostWorld) TestSafety.LeavingHostWorld = true;   // still the host's world until the menu is in
            if (client) LeaveToMenu();
        }

        internal static void Reset()
        {
            Pings.Clear();
            Voice.Reset();
            if (Chat.Typing) Chat.Close();
        }

        // ---- Steam rich presence: friends' menus see who hosts, its version and whether it has a password
        public const string PresenceKey = "tldrevamp";
        private static string _presence = "";
        private static float _presenceAt;

        private static void Presence()
        {
            if (!SteamTransport.SteamReady || Time.realtimeSinceStartup - _presenceAt < 2f) return;
            _presenceAt = Time.realtimeSinceStartup;
            string v = "";
            if (Mp.Server != null && !DedicatedServer.Enabled)
                v = Protocol.Version + "|" + (Mp.Server.Password.Length > 0 ? 1 : 0) + "|" + (Mp.Server.ReadyCount() + 1) + "|" + MpServer.MaxPlayers + "|" + Plugin.Version + "|" + GameMode.Code();
            if (v == _presence) return;
            _presence = v;
            try { SteamFriends.SetRichPresence(PresenceKey, v.Length > 0 ? v : null); }
            catch (Exception e) { Plugin.Log.LogWarning("rich presence: " + e.Message); }
        }

        /// A friend's hosted game, from their rich presence: null when they don't host.
        public sealed class Hosted { public int Protocol; public bool Locked; public int Players, Max; public string Version; public bool HasMode; public GameMode.Mode Mode; }
        public static Hosted FriendHosts(ulong friend)
        {
            string v;
            try { v = SteamFriends.GetFriendRichPresence(new CSteamID(friend), PresenceKey); } catch { return null; }
            if (string.IsNullOrEmpty(v)) return null;
            var f = v.Split('|');
            if (f.Length < 5 || !int.TryParse(f[0], out int proto)) return null;
            int.TryParse(f[2], out int n); int.TryParse(f[3], out int max);
            var h = new Hosted { Protocol = proto, Locked = f[1] == "1", Players = n, Max = max, Version = f[4] };
            if (f.Length > 5 && GameMode.FromCode(f[5], out var m)) { h.HasMode = true; h.Mode = m; }
            return h;
        }

        /// Diagnostics (bridge `mp session`).
        public static string Status()
        {
            var rows = new List<string>();
            foreach (var p in Players())
                rows.Add("[" + p.Id + "," + Json.Str(p.Name) + "," + p.SteamId + "," + p.Ping + "," + (p.Host ? "true" : "false") + "," + (p.Me ? "true" : "false") + "]");
            var lines = new List<string>();
            for (int i = Math.Max(0, Lines.Count - 10); i < Lines.Count; i++) lines.Add("[" + Lines[i].Kind + "," + Json.Str(Lines[i].Name) + "," + Json.Str(Lines[i].Text) + "]");
            var c = Mp.Client;
            return "{\"inGame\":" + (InGame ? "true" : "false") + ",\"players\":[" + string.Join(",", rows) + "],\"lines\":[" + string.Join(",", lines) + "]" +
                   ",\"shown\":" + ChatShown + ",\"endReason\":" + Json.Str(EndReason) + ",\"endCode\":" + EndCode +
                   ",\"password\":" + (Mp.Server != null ? (Mp.Server.Password.Length > 0 ? "true" : "false") : "null") +
                   ",\"relayed\":" + (Mp.Server != null ? Mp.Server.ChatRelayed : 0) + ",\"dropped\":" + (Mp.Server != null ? Mp.Server.ChatDropped : 0) +
                   ",\"reject\":" + Json.Str(c != null ? c.RejectReason : null) + ",\"rejectCode\":" + (c != null ? c.RejectCode : 0) + ",\"presence\":" + Json.Str(_presence) + "}";
        }
    }

    public sealed partial class MpClient
    {
        internal void SendChat(string text)
        {
            string tpa = Tpa.CommandKind(text);
            if (tpa != null) SessionLog.Event(tpa, from: Entities.MyId);
            _w.Reset(); _w.U8(Protocol.Chat); _w.Str(text);
            T.Send(_conn, _w, SteamTransport.SendReliable);
        }
    }
}
