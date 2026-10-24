using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Player bots on the bridge (what they do and why: PlayerBotCore). Made in the host's game they connect over
    /// loopback; made in a joiner's game they connect to the same host the way the joiner did (Steam P2P or IP), so
    /// their messages cross the network like a real third player's. A joiner's bots share the joiner's Steam account:
    /// the host needs `set TLDRevamp.Net.MpServer.TestSameAccount True` to let them in.
    ///   mp pbot add [name]                 a bot at this player's feet
    ///   mp pbot list                       every bot: its player id, pose, seat, hands, what it owns, its counters
    ///   mp pbot <n> goto <gx> <gz>         walk there
    ///   mp pbot <n> tp <gx> <gz>           stand there at once (a test's shortcut, like the bridge's tpground)
    ///   mp pbot <n> enter <carNet>         walk to the driver's door, sit, claim the car (Driver, DriverRetry)
    ///   mp pbot <n> drive <km/h> [s]       drive straight on (0: brake to a stop)
    ///   mp pbot <n> exit                   stop, get out (the car's final state, driven off)
    ///   mp pbot <n> take <itemNet>         walk to it, pick it up (Pickup claim)
    ///   mp pbot <n> drop                   let go of it on the ground
    ///   mp pbot <n> put <carNet> <lx> <ly> <lz>   let go of it at that point in the car's frame (a bed, a roof)
    ///   mp pbot <n> leave | crash [s] | rejoin
    ///   mp pbot <n> owner <net>            who owns it as far as the bot was told (its Add and Owner messages)
    public static class PlayerBots
    {
        internal static readonly List<PlayerBot> All = new List<PlayerBot>();

        public static string Command(string[] a)
        {
            var ic = CultureInfo.InvariantCulture;
            if (a.Length == 0 || a[0] == "list") return List();
            if (a[0] == "add") return Add(a.Length > 1 ? a[1] : "pbot" + (All.Count + 1));
            if (!int.TryParse(a[0], out int n) || n < 1 || n > All.Count) return "{\"error\":\"no such player bot\"}";
            var b = All[n - 1]; var c = b.Core; string err = null;
            double now = Time.unscaledTimeAsDouble;
            string verb = a.Length > 1 ? a[1] : "status";
            switch (verb)
            {
                case "status": break;
                case "owner": { uint net = uint.Parse(a[2], ic); return "{\"net\":" + net + ",\"owner\":" + c.OwnerOf(net) + ",\"epoch\":" + c.EpochOf(net) + ",\"id\":" + c.MyId + "}"; }
                case "goto": err = c.Goto(double.Parse(a[2], ic), double.Parse(a[3], ic)); break;
                case "tp":
                {
                    double gx = double.Parse(a[2], ic), gz = double.Parse(a[3], ic);
                    if (c.SeatNet != 0) { err = "seated"; break; }
                    c.Pos = new Vector3d(gx, Entities.BotWorld.Ground(gx, gz, out double gy) ? gy : c.Pos.y, gz);
                    break;
                }
                case "enter": err = c.Enter(uint.Parse(a[2], ic), Entities.BotWorld); break;
                case "drive": err = c.Drive(float.Parse(a[2], ic) / 3.6f, a.Length > 3 ? float.Parse(a[3], ic) : 0f, now); break;
                case "exit": err = c.Exit(Entities.BotWorld, b.Send); break;
                case "take": err = c.Take(uint.Parse(a[2], ic), Entities.BotWorld); break;
                case "drop": err = c.Release(0, null, Entities.BotWorld, b.Send); break;
                case "put": err = c.Release(uint.Parse(a[2], ic), new Vector3(float.Parse(a[3], ic), float.Parse(a[4], ic), float.Parse(a[5], ic)), Entities.BotWorld, b.Send); break;
                case "leave": err = b.Leave(); break;
                case "crash": err = b.Crash(a.Length > 2 ? float.Parse(a[2], ic) : PlayerBotCore.CrashSilentS); break;
                case "rejoin": err = b.Join(); break;
                default: err = "unknown: " + verb; break;
            }
            return err != null ? "{\"error\":" + Json.Str(err) + "}" : b.ToJson();
        }

        private static string Add(string name)
        {
            if (Mp.Server == null && Mp.Client == null) return "{\"error\":\"host or join first\"}";
            var b = new PlayerBot(Session.CleanName(name), Mp.LocalGlobal());
            string err = b.Join();
            if (err != null) return "{\"error\":" + Json.Str(err) + "}";
            All.Add(b);
            return b.ToJson();
        }

        private static string List()
        {
            var parts = new List<string>();
            foreach (var b in All) parts.Add(b.ToJson());
            return "{\"bots\":[" + string.Join(",", parts) + "]}";
        }

        /// The session ended (Mp.Stop disposed every bot's connection): the bots go with it.
        internal static void Reset() { All.Clear(); }

        /// Every frame from Mp: a crashed bot's connection goes once its silence has lasted.
        internal static void Tick()
        {
            double now = Time.unscaledTimeAsDouble;
            foreach (var b in All) b.CrashTick(now);
        }
    }

    public sealed class PlayerBot
    {
        internal readonly PlayerBotCore Core;
        internal MpClient Client;
        private float _crashFor;
        public int Joins;

        internal PlayerBot(string name, Vector3d at) { Core = new PlayerBotCore(name, at); }

        internal void Send(NetWriter w, bool reliable) { if (Client != null && !Core.Silent) Client.SendRaw(w, reliable ? SteamTransport.SendReliable : SteamTransport.SendUnreliable); }

        /// Connect (again): the host's game over loopback, a joiner's game to its host the way it joined.
        internal string Join()
        {
            if (Client != null) return "already in the game";
            SteamTransport t;
            try
            {
                if (Mp.Server != null) t = SteamTransport.ConnectIP("127.0.0.1:" + Mp.Server.Port);
                else if (Mp.Client != null && Mp.Client.HostSteamId != 0) t = SteamTransport.ConnectP2P(Mp.Client.HostSteamId, Mp.P2PPort);
                else if (Mp.Client != null && !string.IsNullOrEmpty(Mp.Client.JoinAddress)) t = SteamTransport.ConnectIP(Mp.Client.JoinAddress);
                else return "no session to join";
            }
            catch (Exception e) { return "connect: " + e.Message; }
            Core.Reset();
            if (Joins > 0) Core.Pos = Mp.LocalGlobal();   // back where the player who makes it stands
            Client = new MpClient(t, new BotMotion(Core.Pos, 0f, 0f, 0f)) { Player = this, HostSteamId = Mp.Client != null ? Mp.Client.HostSteamId : 0 };
            Mp.Bots.Add(Client);
            Joins++;
            return null;
        }

        internal string Leave()
        {
            if (Client == null) return "not in the game";
            Client.Leave(); Drop();
            Core.Reset(); Core.Last = "left";
            return null;
        }

        internal string Crash(float seconds)
        {
            if (Client == null) return "not in the game";
            Core.Crash(Time.unscaledTimeAsDouble); _crashFor = Mathf.Max(0f, seconds);
            Plugin.Log.LogInfo($"player bot {Core.Name} (player {Core.MyId}) crashed: silent, the connection drops in {_crashFor:F0} s");
            return null;
        }

        internal void CrashTick(double now)
        {
            if (Client == null || !Core.Silent || now - Core.SilentSince < _crashFor) return;
            Drop();   // no goodbye: closed without linger
            Core.Reset(); Core.Last = "crashed";
        }

        private void Drop()
        {
            Mp.Bots.Remove(Client);
            Client.Dispose(); Client = null;
        }

        /// MpClient, every frame once the bot is in.
        internal void Tick(int myId)
        {
            Core.MyId = myId; Core.Clock = Time.unscaledTimeAsDouble;
            Core.Tick(Core.Clock, Time.unscaledDeltaTime, Entities.BotWorld, Send);
        }

        internal string ToJson()
        {
            var ic = CultureInfo.InvariantCulture; var c = Core;
            var owned = new List<string>(); foreach (var n in c.Owned) owned.Add(n.ToString(ic));
            var cargo = new List<string>(); foreach (var n in c.Cargo) cargo.Add(n.ToString(ic));
            return "{\"name\":" + Json.Str(c.Name) + ",\"id\":" + c.MyId + ",\"in\":" + (Client != null ? "true" : "false") + ",\"ready\":" + (Client != null && Client.Ready ? "true" : "false") +
                   ",\"pos\":[" + c.Pos.x.ToString("F2", ic) + "," + c.Pos.y.ToString("F2", ic) + "," + c.Pos.z.ToString("F2", ic) + "],\"seat\":" + c.SeatNet + ",\"seatIdx\":" + c.SeatIdx +
                   ",\"driving\":" + c.Driving + ",\"speed\":" + (c.Speed * 3.6f).ToString("F1", ic) + ",\"holding\":" + c.Holding + ",\"owned\":[" + string.Join(",", owned) + "],\"cargo\":[" + string.Join(",", cargo) +
                   "],\"doing\":" + Json.Str(c.Doing) + ",\"last\":" + Json.Str(c.Last) + ",\"silent\":" + (c.Silent ? "true" : "false") + ",\"joins\":" + Joins +
                   ",\"claims\":" + c.ClaimsSent + ",\"claimsLost\":" + c.ClaimsLost + ",\"states\":" + c.StatesSent + ",\"finals\":" + c.FinalsSent + ",\"handovers\":" + c.HandoversSent + ",\"handedToIt\":" + c.Handed + "}";
        }
    }
}
