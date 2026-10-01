using System;
using System.Collections.Generic;
using System.Linq;
using Steamworks;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Milestone 1 of the mod's multiplayer (docs/MULTIPLAYER-ARCHITECTURE.md §10): handshake, players visible and
    /// moving. Listen server = the host's game; clients connect by IP (LAN/tests) or Steam P2P. Bots are clients inside
    /// the host's own process (real sockets over loopback), each moving like a player — the 10–16-player protocol test
    /// without 16 game copies.
    ///
    /// Bridge: `mp host [port]`, `mp join <ip:port>`, `mp bots <n> [radius] [speed]`, `mp status`, `mp stop`.
    public static class Mp
    {
        public static MpServer Server;
        public static MpClient Client;
        public static readonly List<MpClient> Bots = new List<MpClient>();

        public static string Host(ushort port)
        {
            Stop();
            Server = new MpServer(port);
            Entities.ResetCounters();
            Entities.HostStarted();   // the host's world becomes the session's: its buildings are done, its items shared
            return Status();
        }

        public static string Join(string address)
        {
            Stop();
            Entities.ResetCounters();
            Client = new MpClient(SteamTransport.ConnectIP(address), null);
            return Status();
        }

        public static string JoinFriend(ulong steamId)
        {
            Stop();
            Entities.ResetCounters();
            Client = new MpClient(SteamTransport.ConnectP2P(steamId, P2PPort), null);
            return Status();
        }

        /// The client's connection route (bridge `net route`): relay datacenter, ping — proves a relay test really is one.
        public static string Route() => Client != null && Client.T.Connections.Count > 0 ? SteamCompat.Route(Client.T.Connections[0]) : "{\"error\":\"not connected as a client\"}";

        // ---- the overlay's network line (F8 overlay, while in a session): ping, loss, download/upload, route
        private static long _nlIn, _nlOut; private static float _nlAt, _nlRouteAt; private static string _nlRates = "", _nlRoute = "";

        /// One line for the overlay, or null outside a session. Rates over >= 1 s so they don't flicker.
        public static string NetStatsLine()
        {
            SteamTransport t = Client != null ? Client.T : Server != null ? Server.T : null;
            if (t == null) return null;
            float now = Time.realtimeSinceStartup;
            if (now - _nlAt >= 1f)
            {
                float dt = _nlAt > 0 ? now - _nlAt : 0f;
                if (dt > 0 && dt < 10f) _nlRates = $"down {(t.BytesIn - _nlIn) / 1024f / dt:F1} KB/s  up {(t.BytesOut - _nlOut) / 1024f / dt:F1} KB/s";
                _nlIn = t.BytesIn; _nlOut = t.BytesOut; _nlAt = now;
            }
            if (Client != null)
            {
                if (t.Connections.Count == 0) return "MP  connecting…";
                var q = t.Status(t.Connections[0]);
                if (now - _nlRouteAt >= 2f)
                {
                    _nlRouteAt = now;
                    try { var r = SteamCompat.Route(t.Connections[0]); _nlRoute = r.Contains("\"relayed\":true") ? "relay " + Between(r, "\"popRelay\":\"", "\"") : "direct"; }
                    catch { _nlRoute = ""; }
                }
                float loss = Mathf.Clamp01(1f - q.QualityLocal) * 100f;
                return $"MP  <b>ping {q.Ping} ms</b>  loss {loss:F0}%  {_nlRates}  {_nlRoute}";
            }
            int n = 0, sum = 0, max = 0;
            foreach (var c in t.Connections) { int pg = t.Status(c).Ping; if (pg >= 0) { n++; sum += pg; if (pg > max) max = pg; } }
            return $"MP host  {n} player{(n == 1 ? "" : "s")}  <b>ping {(n > 0 ? sum / n : 0)} ms</b> (worst {max})  {_nlRates}";
        }

        /// Round trip to the server (client) or the worst player's (host), ms; 0 without a session.
        public static int PingMs()
        {
            SteamTransport t = Client != null ? Client.T : Server != null ? Server.T : null;
            if (t == null) return 0;
            int max = 0;
            foreach (var c in t.Connections) { int p = t.Status(c).Ping; if (p > max) max = p; }
            return max;
        }

        /// Bridge `net stats`: the transport's totals and each connection's Steam status (ping, quality, queues) — the
        /// relay test samples it to see latency build up behind a send queue, not just the ping.
        public static string NetStats()
        {
            SteamTransport t = Client != null ? Client.T : Server != null ? Server.T : null;
            if (t == null) return "{\"error\":\"no session\"}";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var c in t.Connections)
            {
                var q = t.Status(c);
                rows.Add("{\"ping\":" + q.Ping + ",\"qualityLocal\":" + q.QualityLocal.ToString("F3", ic) + ",\"qualityRemote\":" + q.QualityRemote.ToString("F3", ic) +
                         ",\"outBps\":" + q.OutBytesPerSec.ToString("F0", ic) + ",\"inBps\":" + q.InBytesPerSec.ToString("F0", ic) + ",\"sendRate\":" + q.SendRateBytesPerSecond +
                         ",\"pendingUnreliable\":" + q.PendingUnreliable + ",\"pendingReliable\":" + q.PendingReliable + ",\"unackedReliable\":" + q.SentUnackedReliable +
                         ",\"queueUs\":" + q.QueueTimeUsec + "}");
            }
            return "{\"role\":" + Json.Str(Client != null ? "client" : "host") + ",\"bytesIn\":" + t.BytesIn + ",\"bytesOut\":" + t.BytesOut + ",\"msgIn\":" + t.MessagesIn +
                   ",\"msgOut\":" + t.MessagesOut + ",\"sendErrors\":" + t.SendErrors + ",\"line\":" + Json.Str(NetStatsLine() ?? "") + ",\"conns\":[" + string.Join(",", rows) + "]}";
        }

        private static string Between(string s, string a, string b)
        {
            int i = s.IndexOf(a); if (i < 0) return "";
            i += a.Length; int j = s.IndexOf(b, i); return j < 0 ? "" : s.Substring(i, j - i);
        }

        public const int P2PPort = 0;

        /// radius < 0: spread the bots over distance tiers (|radius|·{0.01, 0.1, 1}: e.g. −200000 → 2 km, 20 km, 200 km).
        /// Load test: bots that drive — each shares a copy of BotCarRecord (bridge `mp botcar`: the car nearest the player)
        /// and sends that car's state at the players' rate, wheels and engine included (torques too when BotTorques: as
        /// a car within 40 m of another player's). To the host and every client they cost what a driving player costs.
        public static byte[] BotCarRecord;
        public static bool BotCars, BotTorques = true;
        /// bots report themselves seated in their car's driver seat, as a driving player does (before v0.57.89 they were
        /// "standing" players: their positions went out at 20 Hz instead of a seated player's 2 Hz — at 16 players
        /// ~4,500 extra messages a second in the load test that real sessions don't have)
        public static bool BotsSeated = true;
        public static int BotDriverSeat;
        public static string CaptureBotCar()
        {
            var root = Entities.NearestCar();
            if (root == null) return "{\"error\":\"no car near\"}";
            BotCarRecord = Entities.CaptureRecord(root);
            var seats = root.GetComponentsInChildren<seatscript>(true);
            BotDriverSeat = System.Array.FindIndex(seats, x => x.driverSeat0);
            return "{\"car\":" + Json.Str(root.name) + ",\"bytes\":" + BotCarRecord.Length + ",\"driverSeat\":" + BotDriverSeat + "}";
        }

        public static string AddBots(int n, float radius, float speed)
        {
            if (Server == null) return "{\"error\":\"host first\"}";
            if (BotCars && BotCarRecord == null) return "{\"error\":\"mp botcar first\"}";
            var home = LocalGlobal();
            for (int i = 0; i < n; i++)
            {
                float a = (Bots.Count + i) * Mathf.PI * 2f / Mathf.Max(1, Bots.Count + n);
                float r = radius >= 0 ? radius : -radius * (i % 3 == 0 ? 0.01f : i % 3 == 1 ? 0.1f : 1f);
                Bots.Add(new MpClient(SteamTransport.ConnectIP("127.0.0.1:" + Server.Port), new BotMotion(home, r, a, speed / Mathf.Max(1f, r))) { CarRecord = BotCars ? BotCarRecord : null });
            }
            return Status();
        }

        public static string Stop(bool fromSceneLoad = false)
        {
            bool wasHost = Server != null;
            foreach (var b in Bots) b.Dispose();
            Bots.Clear();
            if (Client != null) TestSafety.InHostWorld = false;   // leaving the host's world: the player's own saving rules again (a test's block stays)
            Client?.Dispose(); Client = null;
            Server?.Dispose(); Server = null;
            RemotePlayers.Clear();
            Entities.Reset(wasHost);
            // the server owns the save: a host that ends a session keeps the tail (lease-spawned buildings, client
            // edits — all of it lives in the host's world). Vanilla has no final save on quit, so it would lose
            // everything since the last autosave. Skipped for scene loads (vanilla loses that tail too, and saving
            // mid-transition risks writing half-reset state).
            if (wasHost && !fromSceneLoad && !TestSafety.BlockAutoSave) SaveTail();
            return "{\"stopped\":true}";
        }

        /// One vanilla save rotation, right now, SYNCHRONOUS. The host's session state lives in its world, so this is
        /// what makes the world survive a restart. AutoSave() itself writes async — a host that stops right after
        /// (or a test that restores the save folder) would lose the file, so we call the game's Save with _async: false.
        public static float LastSaveMs; public static int LastSaveItems;
        // togglable diagnostics: `set TLDRevamp.Net.Mp.RemoteTrace True` (bridge) — logs remote-player lifecycle
        public static bool RemoteTrace;
        public static string SaveTail()
        {
            try
            {
                var scr = savedatascript.s != null ? savedatascript.s.saveScreen : null;
                if (scr == null) scr = UnityEngine.Object.FindObjectOfType<newSaveScreenScript>(true);
                if (scr == null) return "{\"error\":\"no save screen\"}";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int items = savedatascript.s.items.Count;
                scr.Save(new savedata(savedatascript.s.data), mainscript.s.menuBackGroundSaveSprite, pathscript.autosaveName1, _async: false);
                sw.Stop();
                LastSaveMs = (float)sw.Elapsed.TotalMilliseconds; LastSaveItems = items;
                Entities.SaveLeases();
                return "{\"saved\":true,\"sync\":true,\"items\":" + items + ",\"ms\":" + LastSaveMs.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "}";
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("save tail: " + e.Message);
                return "{\"error\":" + Json.Str(e.Message) + "}";
            }
        }

        /// A session belongs to the world it was started in. Any other scene load — back to the menu, a new game, loading a
        /// save — ends it; left running, the next world would come up empty (building contents wait for leases from a
        /// session that isn't there). The one exception is the client loading the host's world on Welcome.
        internal static bool HostWorldLoad;
        internal static bool PendingSceneStop;
        internal static bool SceneTearingDown;

        [HarmonyLib.HarmonyPatch(typeof(menuhandler), nameof(menuhandler.LoadScene))]
        private static class EndSessionOnSceneLoad
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix()
            {
                SceneTearingDown = true;   // the unload destroys everything: no RemoveItem echoes
                if (HostWorldLoad) { HostWorldLoad = false; return; }
                if (Server != null || Client != null)
                {
                    Plugin.Log.LogInfo("Scene load: ending the MP session (deferred)");
                    PendingSceneStop = true;   // not inside the load: teardown touches Steamworks and scene objects
                }
            }
        }

        /// The deferred scene-load stop: teardown out of the game's call, contained — a throw here must never
        /// wedge the scene load (a wedged load = no ground = the player falls through the world).
        private static void StopIfScenePending()
        {
            if (!PendingSceneStop) return;
            PendingSceneStop = false;
            SceneTearingDown = false;
            try { Stop(fromSceneLoad: true); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Scene-load session stop failed, forcing: " + e);
                try { Client?.Dispose(); } catch { }
                try { Server?.Dispose(); } catch { }
                Client = null; Server = null;
                try { RemotePlayers.Clear(); } catch { }
                try { Entities.Reset(Server != null); } catch { }
            }
        }

        /// Runner, once per frame.
        public static void Tick()
        {
            StopIfScenePending();
            // the teardown guard must not outlive the teardown: once the new world stands (host or client),
            // lifecycle hooks (spawn-share, deletion) go back to work
            if (SceneTearingDown && DataFromMenuScript.s != null && !DataFromMenuScript.s.mainmenu
                && mainscript.s != null && mainscript.s.player != null)
                SceneTearingDown = false;
            try
            {
                Server?.Tick();
                Client?.Tick();
                for (int i = 0; i < Bots.Count; i++) Bots[i].Tick();
                RemotePlayers.Tick();
                Entities.Tick();
                Server?.FlushBatches();   // everything relayed this frame (players' states, the host's own) in one message per peer
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Mp.Tick: " + e);
                Stop();
            }
        }

        public static string Status()
        {
            var parts = new List<string>();
            if (Server != null) parts.Add("\"server\":" + Server.StatusJson());
            if (Client != null) parts.Add("\"client\":" + Client.StatusJson());
            if (Bots.Count > 0)
            {
                int ready = 0; long inB = 0, outB = 0; int pingSum = 0, pingN = 0, pingMax = 0;
                foreach (var b in Bots)
                {
                    if (b.MyId > 0) ready++;
                    inB += b.T.BytesIn; outB += b.T.BytesOut;
                    if (b.T.Connections.Count > 0) { int pg = b.T.Status(b.T.Connections[0]).Ping; if (pg >= 0) { pingSum += pg; pingN++; if (pg > pingMax) pingMax = pg; } }
                }
                parts.Add("\"bots\":{\"count\":" + Bots.Count + ",\"ready\":" + ready + ",\"bytesIn\":" + inB + ",\"bytesOut\":" + outB +
                          ",\"pingAvg\":" + (pingN > 0 ? pingSum / pingN : -1) + ",\"pingMax\":" + pingMax + ",\"lag\":" + Json.Str(NetLab.LagInfo) + "}");
            }
            parts.Add("\"remotePlayers\":" + RemotePlayers.Count);
            return "{" + string.Join(",", parts) + "}";
        }

        internal static Vector3d LocalGlobal() =>
            mainscript.s != null && mainscript.s.player != null ? mainscript.GlobalFromUnityPos(mainscript.s.player.transform.position) : new Vector3d(0, 0, 0);

        /// Body facing, as the official MP sends it (BodyRot), not the camera.
        internal static float LocalYaw() =>
            mainscript.s == null || mainscript.s.player == null ? 0f
            : mainscript.s.player.BodyRot != null ? mainscript.s.player.BodyRot.eulerAngles.y : mainscript.s.player.transform.eulerAngles.y;

        internal static int Seed() => menuhandler.s != null && menuhandler.s.currentMainMap != null ? menuhandler.s.currentMainMap.seed : 0;

        internal static int MapType() => gamemodesettingsscript.s != null && gamemodesettingsscript.s.S != null ? (int)gamemodesettingsscript.s.S.map.map.tip : 0; // standard/image/custom map type (custom map names not compared yet)

        internal static ulong MySteamId() => SteamTransport.SteamReady ? SteamUser.GetSteamID().m_SteamID : 0;
    }

    /// A bot's movement: a circle around a centre (global coordinates), like MpLab's fake players.
    public sealed class BotMotion
    {
        private readonly Vector3d _center;
        private readonly float _radius, _omega;
        private float _angle;
        public BotMotion(Vector3d center, float radius, float angle, float omega) { _center = center; _radius = radius; _angle = angle; _omega = omega; }
        public Vector3d Step(float dt, out float yaw)
        {
            _angle += _omega * dt;
            yaw = -_angle * Mathf.Rad2Deg;
            return new Vector3d(_center.x + Math.Cos(_angle) * _radius, _center.y, _center.z + Math.Sin(_angle) * _radius);
        }
    }

    public sealed class MpServer : IDisposable
    {
        private sealed class Peer
        {
            public HSteamNetConnection Conn;
            public int Id;
            public ulong SteamId;
            public string Name;
            public bool Ready;
            public bool HasPos;
            public Vector3d Pos;
            public float Yaw;
            public ushort Seq;
            public byte[] Outfit;   // latest outfit payload, for newcomers
            public uint SeatNet; public int SeatIdx;
            public readonly NetWriter Batch = new NetWriter(); public int BatchN, FirstOff, FirstLen;   // unreliable messages waiting for this peer
            public readonly Dictionary<uint, float> LastCarState = new Dictionary<uint, float>();   // car states: when this peer last got one, per car
        }

        public readonly ushort Port;
        public readonly SteamTransport T;
        private readonly Dictionary<uint, Peer> _peers = new Dictionary<uint, Peer>();
        private readonly NetWriter _w = new NetWriter();
        private int _nextId = 1;           // 0 = the host's own player
        private float _sendAcc;
        private ushort _seq;
        private int _tick;
        private int _hostOutfitHash;
        private float _outfitCheck;
        private readonly NetWriter _pose = new NetWriter();
        public long StatesRelayed, StatesSkipped, Rejected, PosesRelayed;
        public long[] TierSends = new long[3];

        public MpServer(ushort port)
        {
            Port = port;
            T = SteamTransport.ListenIP(port);
            T.AddListenP2P(Mp.P2PPort);
            T.Message = OnMessage;
            T.Disconnected = OnDisconnected;
        }

        private void OnMessage(HSteamNetConnection c, NetReader r)
        {
            byte type = r.U8();
            _peers.TryGetValue(c.m_HSteamNetConnection, out var p);
            switch (type)
            {
                case Protocol.Hello:
                {
                    ushort ver = r.U16(); string mod = r.Str(); ulong sid = r.U64(); string name = r.Str();
                    if (r.Bad || ver != Protocol.Version || mod != Plugin.Version)
                    {
                        _w.Reset(); _w.U8(Protocol.Reject); _w.Str("version mismatch: server " + Protocol.Version + "/" + Plugin.Version + ", you " + ver + "/" + mod);
                        T.Send(c, _w, SteamTransport.SendReliable);
                        Rejected++;
                        return;
                    }
                    if (p == null) { p = new Peer { Conn = c, Id = _nextId++ }; _peers[c.m_HSteamNetConnection] = p; }
                    p.SteamId = sid; p.Name = name; p.Ready = false;
                    // the client now loads the host's world (seed + start car + map) and sends Ready when it stands in it
                    _w.Reset(); _w.U8(Protocol.Welcome); _w.VarU32((uint)p.Id); _w.I32(Mp.Seed()); _w.U16(Protocol.StateHz);
                    _w.I32((int)mainscript.s.StartCar); _w.I32(Mp.MapType());
                    _w.F32(mainscript.s != null ? mainscript.s.GetCurrentT() : 0f);   // world time: a joiner must not land at dawn in the host's dusk
                    _w.F32(savedatascript.s.data.sandstormLastTime); _w.F32(savedatascript.s.data.sandstormNextTime);   // E5: the storm schedule rides along
                    T.Send(c, _w, SteamTransport.SendReliable);
                    break;
                }
                case Protocol.Ready:
                {
                    if (p == null || p.Ready) return;
                    p.Ready = true;
                    // the newcomer learns about everyone (host = id 0), everyone learns about the newcomer.
                    // Dedicated server: the phantom is not a player — no announcement, no ghost in the list.
                    if (!DedicatedServer.Enabled) Joined(c, 0, Mp.MySteamId(), "host");
                    foreach (var o in _peers.Values) if (o != p && o.Ready) Joined(c, o.Id, o.SteamId, o.Name);
                    foreach (var o in _peers.Values) if (o != p && o.Ready) Joined(o.Conn, p.Id, p.SteamId, p.Name);
                    RemotePlayers.Joined(p.Id, p.Name, p.SteamId);
                    Entities.ServerOnJoin(p.Id);
                    // what everyone looks like
                    if (PlayerLook.LocalOutfit(out int sel, out var en, out var col))
                    {
                        _w.Reset(); _w.U8(Protocol.OtherOutfit); _w.VarU32(0); PlayerLook.WriteOutfit(_w, sel, en, col);
                        T.Send(c, _w, SteamTransport.SendReliable);
                    }
                    foreach (var o in _peers.Values)
                        if (o != p && o.Ready && o.Outfit != null)
                        {
                            _w.Reset(); _w.U8(Protocol.OtherOutfit); _w.VarU32((uint)o.Id); _w.Bytes(o.Outfit, 0, o.Outfit.Length);
                            T.Send(c, _w, SteamTransport.SendReliable);
                        }
                    break;
                }
                case Protocol.State:
                {
                    if (p == null || !p.Ready) return;
                    ushort seq = r.U16(); var pos = Protocol.ReadPos(r); float yaw = Protocol.ReadYaw(r);
                    Protocol.ReadSeat(r, out uint seatNet, out int seatIdx);
                    if (r.Bad) return;
                    p.Pos = pos; p.Yaw = yaw; p.Seq = seq; p.HasPos = true; p.SeatNet = seatNet; p.SeatIdx = seatIdx;
                    RemotePlayers.State(p.Id, seq, pos, yaw, seatNet, seatIdx);
                    // relayed in Tick by interest tier
                    break;
                }
                case Protocol.Pose:
                {
                    if (p == null || !p.Ready || !p.HasPos) return;
                    int start = r.Pos, len = r.Remaining;
                    var pose = PlayerLook.ReadPose(r);
                    if (pose == null) return;
                    RemotePlayers.Pose(p.Id, pose);
                    // limbs only matter to players who can see them
                    _w.Reset(); _w.U8(Protocol.OtherPose); _w.VarU32((uint)p.Id); _w.Bytes(r.Buf, start, len);
                    var host = Mp.LocalGlobal();
                    foreach (var o in _peers.Values)
                    {
                        if (o == p || !o.Ready) continue;
                        var v = o.HasPos ? o.Pos : host;
                        double dx = v.x - p.Pos.x, dz = v.z - p.Pos.z;
                        if (dx * dx + dz * dz > PlayerLook.PoseRangeM * PlayerLook.PoseRangeM) continue;
                        Queue(o, _w);
                        PosesRelayed++;
                    }
                    break;
                }
                case Entities.Share: case Entities.State: case Entities.Claim: case Entities.PartOff: case Entities.Resync: case Entities.Edit: case Entities.ShotgunIn:
                case Entities.RemoveItem: case Entities.SleepSync: case Entities.ContactImpulse: case Entities.DetachReq: case Entities.AttachSync: case Entities.DetachSync: case PlayerCombat.PlayerDamage: case Entities.ShotFx: case Entities.ExplodeReq: case Entities.ExplosionFx: case Entities.BreakHit: case Entities.BreakFx: case Entities.AiState: case Entities.AiSound: case Entities.PoiUsable:
                case Entities.CrashClaim: case Entities.CrashRelease:
                    // one path for everything from players: the host's own messages enter ServerReceive directly
                    if (p != null && p.Ready) { r.Pos = 0; r.U8(); Entities.ServerReceive(p.Id, type, r); }
                    break;
                case Entities.LeaseReq: case Entities.LeaseDone:
                    // lease traffic is valid before Ready: the gate fires while the client's world is still loading
                    // (it asked then, was dropped, and its LeaseAsked set would keep it from ever asking again)
                    if (p != null) { r.Pos = 0; r.U8(); Entities.ServerReceive(p.Id, type, r); }
                    break;
                case Protocol.Outfit:
                {
                    if (p == null || !p.Ready) return;
                    int start = r.Pos, len = r.Remaining;
                    if (!PlayerLook.ReadOutfit(r, out int sel, out var en, out var col)) return;
                    p.Outfit = new byte[len];
                    Buffer.BlockCopy(r.Buf, start, p.Outfit, 0, len);
                    RemotePlayers.Outfit(p.Id, sel, en, col);
                    _w.Reset(); _w.U8(Protocol.OtherOutfit); _w.VarU32((uint)p.Id); _w.Bytes(p.Outfit, 0, len);
                    foreach (var o in _peers.Values) if (o != p && o.Ready) T.Send(o.Conn, _w, SteamTransport.SendReliable);
                    break;
                }
            }
        }

        private void Joined(HSteamNetConnection to, int id, ulong sid, string name)
        {
            _w.Reset(); _w.U8(Protocol.PlayerJoined); _w.VarU32((uint)id); _w.U64(sid); _w.Str(name);
            T.Send(to, _w, SteamTransport.SendReliable);
        }

        private void OnDisconnected(HSteamNetConnection c)
        {
            if (!_peers.TryGetValue(c.m_HSteamNetConnection, out var p)) return;
            _peers.Remove(c.m_HSteamNetConnection);
            RemotePlayers.Left(p.Id);
            Entities.ServerOnLeave(p.Id);
            _w.Reset(); _w.U8(Protocol.PlayerLeft); _w.VarU32((uint)p.Id);
            foreach (var o in _peers.Values) if (o.Ready) T.Send(o.Conn, _w, SteamTransport.SendReliable);
        }

        public void Tick()
        {
            T.Poll();
            _sendAcc += Time.unscaledDeltaTime;
            if (_sendAcc < 1f / Protocol.StateHz) return;
            _sendAcc -= 1f / Protocol.StateHz;
            if (_sendAcc > 1f / Protocol.StateHz) _sendAcc = 0; // don't burst after a long frame
            var host = Mp.LocalGlobal();
            float hostYaw = Mp.LocalYaw();
            Entities.LocalSeat(out uint hostSeatNet, out int hostSeatIdx);
            _seq++;
            _tick++;
            // the host's own look: pose to everyone near, outfit when it changes.
            // Dedicated server: the phantom player is suppressed — clients see nobody at the spawn.
            if (!DedicatedServer.Enabled)
            {
                _pose.Reset(); _pose.U8(Protocol.OtherPose); _pose.VarU32(0);
                if (PlayerLook.WriteLocalPose(_pose))
                    foreach (var o in _peers.Values)
                    {
                        if (!o.Ready || !o.HasPos) continue;
                        double dx = o.Pos.x - host.x, dz = o.Pos.z - host.z;
                        if (dx * dx + dz * dz <= PlayerLook.PoseRangeM * PlayerLook.PoseRangeM) { Queue(o, _pose); PosesRelayed++; }
                    }
                _outfitCheck += 1f / Protocol.StateHz;
                if (_outfitCheck >= 2f && PlayerLook.LocalOutfit(out int hsel, out var hen, out var hcol))
                {
                    _outfitCheck = 0;
                    int hh = PlayerLook.OutfitHash(hsel, hen, hcol);
                    if (hh != _hostOutfitHash)
                    {
                        _hostOutfitHash = hh;
                        _w.Reset(); _w.U8(Protocol.OtherOutfit); _w.VarU32(0); PlayerLook.WriteOutfit(_w, hsel, hen, hcol);
                        foreach (var o in _peers.Values) if (o.Ready) T.Send(o.Conn, _w, SteamTransport.SendReliable);
                    }
                }
            }
            // every ready peer gets every other player (and the host, id 0) at the rate of their distance tier.
            // Dedicated server: no id-0 relay — the phantom is not a player.
            foreach (var to in _peers.Values)
            {
                if (!to.Ready) continue;
                Vector3d viewer = to.HasPos ? to.Pos : host;
                if (!DedicatedServer.Enabled)
                {
                    Relay(to, 0, host, hostYaw, _seq, hostSeatNet, hostSeatIdx, viewer);
                    if (Mp.RemoteTrace && (_tick % 40) == 0)
                        Plugin.Log.LogInfo($"RemoteTrace: relay id 0 seq {_seq} pos {host.x:F0},{host.z:F0} seat {hostSeatNet}/{hostSeatIdx} to {to.Id}");
                }
                foreach (var from in _peers.Values)
                    if (from != to && from.Ready && from.HasPos) Relay(to, from.Id, from.Pos, from.Yaw, from.Seq, from.SeatNet, from.SeatIdx, viewer);
            }
        }

        private void Relay(Peer to, int id, Vector3d pos, float yaw, ushort seq, uint seatNet, int seatIdx, Vector3d viewer)
        {
            double dx = pos.x - viewer.x, dz = pos.z - viewer.z, d = Math.Sqrt(dx * dx + dz * dz);
            int tier = d < Protocol.NearM ? 0 : d < Protocol.MidM ? 1 : 2;
            float hz = tier == 0 ? Protocol.NearHz : tier == 1 ? Protocol.MidHz : Protocol.FarHz;
            // every k-th server tick, phased by player id so far players don't all go out on the same tick
            int k = Mathf.Max(1, Mathf.RoundToInt(Protocol.StateHz / hz));
            // seated: the body rides the seat of its car's copy (the car's own state moves it) — the position only keeps
            // the seat reference fresh: 2 Hz (at 16 players these were ~1/3 of all the host's messages)
            if (seatNet != 0) k = Mathf.Max(k, Mathf.RoundToInt(Protocol.StateHz / SeatedHz));
            if ((_tick + id) % k != 0) { StatesSkipped++; return; }
            _w.Reset(); _w.U8(Protocol.OtherState); _w.VarU32((uint)id); _w.U16(seq); Protocol.WritePos(_w, pos); Protocol.WriteYaw(_w, yaw);
            Protocol.WriteSeat(_w, seatNet, seatIdx);
            Queue(to, _w);
            StatesRelayed++;
            TierSends[tier]++;
        }

        internal void SendToPlayer(int id, NetWriter w, bool reliable)
        {
            foreach (var o in _peers.Values)
                if (o.Id == id && o.Ready) { if (reliable) T.Send(o.Conn, w, SteamTransport.SendReliable); else Queue(o, w); return; }
        }

        /// To every ready player except `except`; with `near`, only players within `range` metres of it.
        internal void SendToAll(NetWriter w, bool reliable, int except, Vector3d? near, double range)
        {
            foreach (var o in _peers.Values)
            {
                if (!o.Ready || o.Id == except) continue;
                if (near.HasValue && o.HasPos)
                {
                    double dx = o.Pos.x - near.Value.x, dz = o.Pos.z - near.Value.z;
                    if (dx * dx + dz * dz > range * range) continue;
                }
                if (reliable) T.Send(o.Conn, w, SteamTransport.SendReliable); else Queue(o, w);
            }
        }

        /// Unreliable traffic to a peer goes out as one Batch message per tick (FlushBatches), not one Steam message each:
        /// at 16 players the host made ~11,000 sends a second. A batch stays within one UDP packet (an unreliable message
        /// split over packets is lost whole when one part is).
        public const int BatchMax = 1100;
        public const float SeatedHz = 2f;
        public long BatchesSent, BatchedMsgs;
        private void Queue(Peer to, NetWriter w)
        {
            if (to.BatchN > 0 && to.Batch.Len + w.Len + 3 > BatchMax) FlushPeer(to);
            if (w.Len + 4 > BatchMax) { T.Send(to.Conn, w, SteamTransport.SendUnreliable); return; }   // too big to share a packet
            if (to.BatchN == 0) { to.Batch.Reset(); to.Batch.U8(Protocol.Batch); }
            to.Batch.VarU32((uint)w.Len);
            if (to.BatchN == 0) { to.FirstOff = to.Batch.Len; to.FirstLen = w.Len; }
            to.Batch.Bytes(w.Buf, 0, w.Len);
            to.BatchN++; BatchedMsgs++;
        }

        private void FlushPeer(Peer p)
        {
            if (p.BatchN == 0) return;
            if (p.BatchN == 1)   // a batch of one: send it as itself
            {
                _single.Reset(); _single.Bytes(p.Batch.Buf, p.FirstOff, p.FirstLen);
                T.Send(p.Conn, _single, SteamTransport.SendUnreliable);
            }
            else T.Send(p.Conn, p.Batch, SteamTransport.SendUnreliable);
            BatchesSent++;
            p.BatchN = 0; p.Batch.Reset();
        }
        private readonly NetWriter _single = new NetWriter();

        public void FlushBatches() { foreach (var p in _peers.Values) FlushPeer(p); }

        /// Ready players with a known position (from their player states): id, global position.
        internal void ReadyPeers(List<KeyValuePair<int, Vector3d>> into)
        {
            into.Clear();
            foreach (var o in _peers.Values) if (o.Ready && o.HasPos) into.Add(new KeyValuePair<int, Vector3d>(o.Id, o.Pos));
        }

        /// Car/item states by distance between the viewer and the car (the owner sends 20 Hz while it moves):
        ///   < CarNearM      every state — crashes, physical copies (12 m), collider LOD (40/50 m), close look
        ///   < CarMidM       10 Hz          < CarFarM  4 Hz          beyond: every CarBeyondInterval (stored copies)
        /// `force`: always sent (the last state when it comes to rest, a driver change) — a thinned stream must not drop
        /// the state a copy ends on. Copies interpolate over the spacing they actually receive (PoseInterpolator).
        public static double CarNearM = 150, CarMidM = 400, CarFarM = 2000;
        public static float CarMidInterval = 0.1f, CarFarInterval = 0.25f;
        /// Beyond CarFarM: a state every 5 s (plus rest states and driver changes). Nothing at all (≤ v0.57.94) left
        /// every far player's stored copy where the car was when it last came near them: arriving there showed a parked
        /// ghost of a car that was 50 km away, and their save kept it there. ~60 bytes per car per 5 s per player.
        public static float CarBeyondInterval = 5f;
        public static bool CarTiers = true;   // A/B: false = every state to everyone within CarFarM (before v0.57.89)
        public long CarStatesSent, CarStatesThinned;
        public readonly long[] CarTierSends = new long[4];
        internal void SendCarState(NetWriter w, int except, uint net, Vector3d pos, bool force)
        {
            float now = Time.realtimeSinceStartup;
            foreach (var o in _peers.Values)
            {
                if (!o.Ready || o.Id == except) continue;
                int tier = 0;
                if (o.HasPos)
                {
                    double dx = o.Pos.x - pos.x, dz = o.Pos.z - pos.z, d2 = dx * dx + dz * dz;
                    tier = d2 < CarNearM * CarNearM ? 0 : d2 < CarMidM * CarMidM ? 1 : d2 < CarFarM * CarFarM ? 2 : 3;
                }
                if (tier == 3 && !CarTiers && !force) continue;   // A/B: the old cut-off
                if (CarTiers && tier > 0 && !force)
                {
                    float iv = tier == 1 ? CarMidInterval : tier == 2 ? CarFarInterval : CarBeyondInterval;
                    // states arrive ~50 ms apart with jitter: due a little early rather than a whole state late
                    if (o.LastCarState.TryGetValue(net, out float last) && now - last < iv - 0.025f) { CarStatesThinned++; continue; }
                }
                o.LastCarState[net] = now;
                Queue(o, w);
                CarStatesSent++; CarTierSends[tier]++;
            }
        }

        public IEnumerable<string> PlayerNames()
        {
            foreach (var p in _peers.Values) if (p.Ready) yield return p.Name + " (#" + p.Id + ")";
        }

        public string StatusJson()
        {
            int ready = 0;
            foreach (var p in _peers.Values) if (p.Ready) ready++;
            return "{\"port\":" + Port + ",\"peers\":" + _peers.Count + ",\"ready\":" + ready + ",\"statesRelayed\":" + StatesRelayed +
                   ",\"posesRelayed\":" + PosesRelayed + ",\"batches\":" + BatchesSent + ",\"batchedMsgs\":" + BatchedMsgs +
                   ",\"skipped\":" + StatesSkipped + ",\"tiers\":[" + TierSends[0] + "," + TierSends[1] + "," + TierSends[2] + "]" +
                   ",\"carStatesSent\":" + CarStatesSent + ",\"carStatesThinned\":" + CarStatesThinned + ",\"carTiers\":[" + CarTierSends[0] + "," + CarTierSends[1] + "," + CarTierSends[2] + "," + CarTierSends[3] + "]" +
                   ",\"rejected\":" + Rejected + ",\"bytesIn\":" + T.BytesIn + ",\"bytesOut\":" + T.BytesOut + ",\"msgIn\":" + T.MessagesIn +
                   ",\"msgOut\":" + T.MessagesOut + ",\"sendErrors\":" + T.SendErrors + ",\"last\":" + Json.Str(T.LastEvent) + "}";
        }

        public void Dispose() => T.Dispose();
    }

    public sealed class MpClient : IDisposable
    {
        public readonly SteamTransport T;
        public readonly BotMotion Bot;   // null = the local player
        public int MyId = -1;
        public int ServerSeed;
        public string RejectReason;
        public static float ServerWorldTime = -1f;
        public static float StormLast = -1f, StormNext = -1f;
        public long StatesIn;
        private readonly NetWriter _w = new NetWriter();
        private HSteamNetConnection _conn;
        private bool _helloSent;
        private float _sendAcc;
        private ushort _seq;
        private Vector3d _botPos;
        private float _botYaw;
        private int _outfitHash;
        private float _outfitCheck;
        public long PosesIn;
        // load-test bots that drive (Mp.BotCars): the shared car and its state stream
        public byte[] CarRecord; private bool _carShared; private uint _carNet, _carEpoch; private ushort _carSeq; private Vector3d _carPrev; private double _carPrevT; private bool _carHasPrev;
        private Vector3d _gA, _gB; private bool _gSeg;   // ground under the bot's path: a sample behind, one 3 m ahead
        private bool _readySent;
        private float _loadStarted = -1f, _worldSeenAt = -1f;
        private readonly NetWriter W2 = new NetWriter();
        public bool Ready => _readySent;

        public MpClient(SteamTransport transport, BotMotion bot)
        {
            Bot = bot;
            T = transport;
            _conn = T.Connections[0];
            T.Connected = _ => SendHello();
            T.Message = OnMessage;
        }

        private void SendHello()
        {
            if (_helloSent) return;
            _helloSent = true;
            _w.Reset(); _w.U8(Protocol.Hello); _w.U16(Protocol.Version); _w.Str(Plugin.Version); _w.U64(Mp.MySteamId());
            _w.Str(Bot != null ? "bot" : (SteamTransport.SteamReady ? SteamFriends.GetPersonaName() : "player"));
            T.Send(_conn, _w, SteamTransport.SendReliable);
        }

        private readonly NetReader _sub = new NetReader();
        private void OnMessage(HSteamNetConnection c, NetReader r)
        {
            byte type = r.U8();
            if (type == Protocol.Batch)
            {
                // the server's per-tick bundle: each message as if it came alone (a message's handler may read to its end)
                while (r.Remaining > 0 && !r.Bad)
                {
                    int n = (int)r.VarU32();
                    if (r.Bad || n <= 0 || n > r.Remaining) break;
                    _sub.Load(n); System.Buffer.BlockCopy(r.Buf, r.Pos, _sub.Buf, 0, n); r.Pos += n;
                    if (_sub.Buf[0] == Protocol.Batch) continue;   // never nested
                    OnMessage(c, _sub);
                }
                return;
            }
            switch (type)
            {
                case Protocol.Welcome:
                {
                    MyId = (int)r.VarU32(); ServerSeed = r.I32(); r.U16(); int car = r.I32(), map = r.I32();
                    ServerWorldTime = !r.Bad ? r.F32() : -1f;   // -1: an old server without the field
                    StormLast = !r.Bad ? r.F32() : -1f; StormNext = !r.Bad ? r.F32() : -1f;
                    _outfitCheck = 99f;
                    if (Bot != null) { _readySent = true; W2.Reset(); W2.U8(Protocol.Ready); T.Send(_conn, W2, SteamTransport.SendReliable); break; }
                    if (map != Mp.MapType()) { RejectReason = "the host plays map type " + map + ", you " + Mp.MapType() + " — change it in the game mode settings"; break; }
                    // load the host's world: a fresh game with its seed and start car (the connection survives the scene
                    // load); the host owns the save, so this world never autosaves
                    TestSafety.InHostWorld = true;
                    RemotePlayers.Clear();
                    Entities.ClientWorldReload();
                    DataFromMenuScript.s.seed = ServerSeed;
                    DataFromMenuScript.s.startcar = (itemdatabase.CarType)car;
                    _loadStarted = Time.realtimeSinceStartup;
                    Mp.HostWorldLoad = true;
                    if (DataFromMenuScript.s.mainmenu) menuhandler.s.PressedStart(); else menuhandler.s.PressedRestart();
                    break;
                }
                case Protocol.Reject: RejectReason = r.Str(); break;
                case Protocol.PlayerJoined: { int id = (int)r.VarU32(); ulong sid = r.U64(); string name = r.Str(); if (Bot == null) RemotePlayers.Joined(id, name, sid); break; }
                case Protocol.OtherPose:
                {
                    int id = (int)r.VarU32(); var pose = PlayerLook.ReadPose(r);
                    if (pose == null) return;
                    PosesIn++;
                    if (Bot == null) RemotePlayers.Pose(id, pose);
                    break;
                }
                case Entities.Assigned when Bot != null:
                {
                    r.U32(); _carNet = r.U32(); _carEpoch = r.U32();   // our car's network id
                    break;
                }
                case Entities.Assigned: case Entities.Add: case Entities.RemoveItem: case Entities.SleepSync: case Entities.ContactImpulse: case Entities.DetachReq: case Entities.State: case Entities.Owner: case Entities.PartDetached: case Entities.LeaseGrant: case Entities.LeaseTaken: case Entities.LeaseFreed: case Entities.Resync: case Entities.Edit: case Entities.ShotgunIn: case Entities.AttachSync: case Entities.DetachSync: case PlayerCombat.PlayerDamage: case Entities.ShotFx: case Entities.ExplodeReq: case Entities.ExplosionFx: case Entities.BreakHit: case Entities.BreakFx: case Entities.AiState: case Entities.AiSound: case Entities.PoiUsable:
                    if (Bot == null) Entities.ClientReceive(type, r);
                    break;
                case Protocol.OtherOutfit:
                {
                    int id = (int)r.VarU32();
                    if (PlayerLook.ReadOutfit(r, out int sel, out var en, out var col) && Bot == null) RemotePlayers.Outfit(id, sel, en, col);
                    break;
                }
                case Protocol.PlayerLeft: { int id = (int)r.VarU32(); if (Bot == null) RemotePlayers.Left(id); break; }
                case Protocol.OtherState:
                {
                    int id = (int)r.VarU32(); ushort seq = r.U16(); var pos = Protocol.ReadPos(r); float yaw = Protocol.ReadYaw(r);
                    Protocol.ReadSeat(r, out uint seatNet, out int seatIdx);
                    if (r.Bad) return;
                    StatesIn++;
                    if (Bot == null) RemotePlayers.State(id, seq, pos, yaw, seatNet, seatIdx);
                    break;
                }
            }
        }

        /// A driving bot: share the car once, then its state with every player tick (the same message a real owner sends).
        private Vector3d OnGround(Vector3d g, Vector3d travel)
        {
            var dir = new Vector3d(travel.x, 0, travel.z);
            double len = Math.Sqrt(dir.x * dir.x + dir.z * dir.z);
            if (len < 1e-4) return _gSeg ? new Vector3d(g.x, _gA.y, g.z) : g;
            dir = new Vector3d(dir.x / len, 0, dir.z / len);
            double t = 0;
            if (_gSeg)
            {
                double ax = _gB.x - _gA.x, az = _gB.z - _gA.z;
                t = ((g.x - _gA.x) * ax + (g.z - _gA.z) * az) / Math.Max(1e-6, ax * ax + az * az);
            }
            if (!_gSeg || t >= 1.0 || t < -0.5)
            {
                if (!_gSeg || t < -0.5 || t > 2.0) { if (!GroundAt(g, out _gA)) return g; }   // start / lost the segment
                else _gA = _gB;
                if (!GroundAt(new Vector3d(g.x + dir.x * 3.0, 0, g.z + dir.z * 3.0), out _gB)) { _gB = _gA; _gB.x += dir.x * 3.0; _gB.z += dir.z * 3.0; }
                _gSeg = true;
                double ax = _gB.x - _gA.x, az = _gB.z - _gA.z;
                t = ((g.x - _gA.x) * ax + (g.z - _gA.z) * az) / Math.Max(1e-6, ax * ax + az * az);
            }
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return new Vector3d(g.x, _gA.y + (_gB.y - _gA.y) * t, g.z);
        }

        private static bool GroundAt(Vector3d g, out Vector3d onGround)
        {
            var u = mainscript.UnityPosFromGlobal(g);
            var hit = DebugBridge.GroundHit(u.x, u.z);
            onGround = hit != null ? new Vector3d(g.x, mainscript.GlobalFromUnityPos(hit.Value.point).y, g.z) : g;
            return hit != null;
        }

        private void BotCarTick(Vector3d g)
        {
            if (CarRecord == null) return;
            if (!_carShared)
            {
                _carShared = true;
                _w.Reset(); _w.U8(Entities.Share); _w.U32(0xB0700000u + (uint)MyId); _w.VarU32((uint)CarRecord.Length); _w.Bytes(CarRecord, 0, CarRecord.Length);
                T.Send(_conn, _w, SteamTransport.SendReliable);
                return;
            }
            if (_carNet == 0) return;
            // on the ground, as a player's car is: a circle at the host player's height ran through hills — near the
            // host's own cars such a copy turns physical (Entities.Physical) and was fought out of the terrain until a
            // 16.9 s frame hung the host (loadtest --radius 30, v0.57.78). Height between two ground samples on the path
            // (behind / 3 m ahead): continuous, one ray per 3 m of travel.
            if (_carHasPrev) g = OnGround(g, g - _carPrev);
            else if (GroundAt(g, out var g0)) g = g0;
            // the real time since the last state: sends fall on frame boundaries (40/60 ms apart at a 20 ms host frame),
            // a fixed 1/20 s gave the copies velocities up to ±20 % off their positions
            double tNow = Time.unscaledTimeAsDouble;
            float dt = _carHasPrev ? Mathf.Max(1e-3f, (float)(tNow - _carPrevT)) : 1f / Protocol.StateHz;
            Vector3 v = _carHasPrev ? (Vector3)(g - _carPrev) / dt : Vector3.zero;
            _carPrev = g; _carPrevT = tNow; _carHasPrev = true;
            var rot = v.sqrMagnitude > 0.01f ? Quaternion.LookRotation(v) : Quaternion.identity;   // pitched with the slope
            _w.Reset();
            Entities.WriteStateHead(_w, _carNet, _carEpoch, ++_carSeq, tNow /* the frame start: the bot's position is integrated to it */, new Vector3d(g.x, g.y + 0.6, g.z), rot, v, true);
            _w.U8((byte)(4 | (Mp.BotTorques ? 0x80 : 0)));                   // 4 wheels (+ torques), the owner's layout
            short rpm = (short)Mathf.Clamp(v.magnitude / 0.33f * 60f / (2f * Mathf.PI), 0f, 32767f);
            for (int i = 0; i < 4; i++) { _w.U8(0); _w.U8(0); _w.U16((ushort)rpm); }
            if (Mp.BotTorques) for (int i = 0; i < 4; i++) { _w.U8(0); _w.U8(8); }
            _w.U8(1 | 2); _w.U16(2200);                                      // engine running at 2200 rpm
            T.Send(_conn, _w, SteamTransport.SendUnreliable);
        }

        public void Tick()
        {
            T.Poll();
            if (MyId <= 0) return;
            if (!_readySent)
            {
                // Ready once the host's world stands here: not in the menu, our player exists, same seed, a few seconds in
                bool inWorld = DataFromMenuScript.s != null && !DataFromMenuScript.s.mainmenu && mainscript.s != null && mainscript.s.player != null
                               && Mp.Seed() == ServerSeed && Time.realtimeSinceStartup - _loadStarted > 3f;
                if (!inWorld) { _worldSeenAt = -1f; return; }
                if (_worldSeenAt < 0) _worldSeenAt = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - _worldSeenAt < 3f) return;
                TestSafety.InHostWorld = true;
                if (ServerWorldTime >= 0f) { try { Entities.ApplyWorldTime(ServerWorldTime, true); } catch { } ServerWorldTime = -1f; }   // relative shift (SetStartTime is for before the clock starts); the Ready reply corrects the load time
                if (StormLast >= 0f && StormNext >= 0f && mainscript.s.sandStormSpawn != null)
                {
                    try
                    {
                        mainscript.s.sandStormSpawn.lastTime = Time.time + StormLast;   // the same conversion the world load uses
                        mainscript.s.sandStormSpawn.nextTime = StormNext;
                    }
                    catch { }
                    StormLast = StormNext = -1f;
                }
                W2.Reset(); W2.U8(Protocol.Ready); T.Send(_conn, W2, SteamTransport.SendReliable);
                _readySent = true;
                return;
            }
            _sendAcc += Time.unscaledDeltaTime;
            if (Bot != null) _botPos = Bot.Step(Time.unscaledDeltaTime, out _botYaw);
            if (_sendAcc < 1f / Protocol.StateHz) return;
            _sendAcc -= 1f / Protocol.StateHz;
            if (_sendAcc > 1f / Protocol.StateHz) _sendAcc = 0;
            var g = Bot != null ? _botPos : Mp.LocalGlobal();
            float yaw = Bot != null ? _botYaw : Mp.LocalYaw();
            _w.Reset(); _w.U8(Protocol.State); _w.U16(++_seq); Protocol.WritePos(_w, g); Protocol.WriteYaw(_w, yaw);
            uint sNet = 0; int sIdx = 0;
            if (Bot == null) Entities.LocalSeat(out sNet, out sIdx);
            else if (Mp.BotsSeated && _carNet != 0 && Mp.BotDriverSeat >= 0) { sNet = _carNet; sIdx = Mp.BotDriverSeat; }
            Protocol.WriteSeat(_w, sNet, sIdx);
            T.Send(_conn, _w, SteamTransport.SendUnreliable);
            if (Bot != null) { BotCarTick(g); return; }
            _w.Reset(); _w.U8(Protocol.Pose);
            if (PlayerLook.WriteLocalPose(_w)) T.Send(_conn, _w, SteamTransport.SendUnreliable);
            _outfitCheck += 1f / Protocol.StateHz;
            if (_outfitCheck >= 2f && PlayerLook.LocalOutfit(out int sel, out var en, out var col))
            {
                _outfitCheck = 0;
                int h = PlayerLook.OutfitHash(sel, en, col);
                if (h != _outfitHash)
                {
                    _outfitHash = h;
                    _w.Reset(); _w.U8(Protocol.Outfit); PlayerLook.WriteOutfit(_w, sel, en, col);
                    T.Send(_conn, _w, SteamTransport.SendReliable);
                }
            }
        }

        internal void SendRaw(NetWriter w, int flags) => T.Send(_conn, w, flags);

        public string StatusJson() =>
            "{\"id\":" + MyId + ",\"ready\":" + (_readySent ? "true" : "false") + ",\"serverSeed\":" + ServerSeed + ",\"mySeed\":" + Mp.Seed() + ",\"reject\":" + Json.Str(RejectReason) +
            ",\"statesIn\":" + StatesIn + ",\"posesIn\":" + PosesIn + ",\"bytesIn\":" + T.BytesIn + ",\"bytesOut\":" + T.BytesOut + ",\"last\":" + Json.Str(T.LastEvent) +
            ",\"ping\":" + (T.Connections.Count > 0 ? T.Status(T.Connections[0]).Ping : -1) + ",\"lag\":" + Json.Str(NetLab.LagInfo) + "}";

        public void Dispose() => T.Dispose();
    }

    /// Other players, shown with the game's own character model (syncScript.playerPrefab, as the official MP spawns
    /// it: IK started, multiplayer outfit, name tag) — without the official MP's world-generation centre per player
    /// and without its dynamic rigidbody (docs §4c). Placed from global coordinates through this machine's own
    /// floating origin; limbs from PlayerLook poses, clothes from outfits. Capsule only if the prefab is missing.
    public static class RemotePlayers
    {
        private sealed class Rp
        {
            public GameObject Go; public mpplayerscript Mp; public playermodeloutfitscript Look;
            public Collider[] Cols; public CharacterController[] Ccs;   // mpplayerscript.Start() re-enables colliders 3 s after spawn — keep them dead
            public Vector3d Target; public float Yaw; public ushort Seq; public bool Has; public string Name; public ulong SteamId;
            public uint SeatNet; public int SeatIdx; public Transform SeatedOn;
            public uint CachedSeatNet; public int CachedSeatIdx; public Transform CachedCarRoot, CachedSeat;   // SeatTransformCached
        }
        /// Last outfit per player: a body re-created (its model went with a car it sat in) gets it back — the prefab's
        /// default shows BOTH characters' renderers (male and female in each other) until an outfit is applied.
        private sealed class Look { public int Sel; public bool[] En; public Color[] Col; }
        private static readonly Dictionary<int, Look> Looks = new Dictionary<int, Look>();
        public static long OutfitsReapplied;
        private static readonly Dictionary<int, Rp> All = new Dictionary<int, Rp>();
        public static int Count => All.Count;
        public static long StatesDropped, Poses, Outfits;
        public static bool Models = true;

        public static void Joined(int id, string name, ulong steamId = 0)
        {
            if (All.TryGetValue(id, out var existing))
            {
                if (name != "?") { existing.Name = name; SetName(existing); }
                if (steamId != 0) existing.SteamId = steamId;
                return;
            }
            var rp = new Rp { Name = name, SteamId = steamId };
            var prefab = Models && syncScript.s != null ? syncScript.s.playerPrefab : null;
            if (prefab != null)
            {
                rp.Go = UnityEngine.Object.Instantiate(prefab, Vector3.zero, Quaternion.Euler(0f, -90f, 0f));
                foreach (var ik in rp.Go.GetComponentsInChildren<IKscript>()) ik.FStart();
                rp.Look = rp.Go.GetComponent<playermodeloutfitscript>();
                if (rp.Look != null)
                {
                    rp.Look.isMultiplayer = true;
                    if (Looks.TryGetValue(id, out var lk)) { ApplyLook(rp, lk.Sel, lk.En, lk.Col); OutfitsReapplied++; }
                    else rp.Look.Refresh(fromMultiplayer: true);   // one character shown until theirs arrives
                }
                rp.Mp = rp.Go.GetComponent<mpplayerscript>();
                if (rp.Mp != null) { rp.Mp.steamID = steamId; rp.Mp.nameText = rp.Go.GetComponentInChildren<helptextscript>(); Bodies.Add(rp.Mp); }
                foreach (var rb in rp.Go.GetComponentsInChildren<Rigidbody>()) rb.isKinematic = true;
                // display-only: the prefab's CharacterController and colliders PUSH real physics — a seated remote
                // body inside a car depenetrated the car every frame (the rolling/freak-out bug). The prefab's own
                // mpplayerscript.Start() re-enables its colliders 3 s after spawn, so this is not a one-time strip:
                // Tick() re-disables them forever (rp.Cols/rp.Ccs).
                rp.Ccs = rp.Go.GetComponentsInChildren<CharacterController>(true);
                foreach (var cc in rp.Ccs) cc.enabled = false;
                rp.Cols = rp.Go.GetComponentsInChildren<Collider>(true);
                foreach (var col in rp.Cols) col.enabled = false;
                SetName(rp);
            }
            else
            {
                rp.Go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                UnityEngine.Object.Destroy(rp.Go.GetComponent<Collider>());
            }
            rp.Go.name = "TLDRevampRemotePlayer" + id;
            rp.Go.SetActive(false);
            All[id] = rp;
        }

        /// Our remote bodies. mpplayerscript turns its colliders back on by itself — 3 s after Start and 1 s after
        /// standing up (EnableCollidersWithDelay) — and a live collider inside a car's real body pushes the car.
        /// EnableColliders is where every such call lands: for these bodies it can only switch them off.
        internal static readonly HashSet<mpplayerscript> Bodies = new HashSet<mpplayerscript>();
        public static long CollidersBlocked;

        [HarmonyLib.HarmonyPatch(typeof(mpplayerscript), nameof(mpplayerscript.EnableColliders))]
        private static class NoRemoteColliders
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(mpplayerscript __instance, ref bool _enable)
            {
                if (_enable && Bodies.Contains(__instance)) { _enable = false; CollidersBlocked++; }
            }
        }

        private static void SetName(Rp rp)
        {
            if (rp.Mp != null && rp.Mp.nameText != null) rp.Mp.nameText.Text = new[] { rp.Name };
        }

        public static void Left(int id)
        {
            if (RemoteTrace && All.ContainsKey(id)) Plugin.Log.LogInfo($"RemoteTrace: Left(id {id}) — All.Count {All.Count}");
            if (!All.TryGetValue(id, out var rp)) return;
            if (rp.Mp != null) Bodies.Remove(rp.Mp);
            if (rp.Go != null) UnityEngine.Object.Destroy(rp.Go);
            All.Remove(id); Looks.Remove(id);
        }

        public static void State(int id, ushort seq, Vector3d global, float yaw, uint seatNet = 0, int seatIdx = 0)
        {
            if (All.TryGetValue(id, out var dead) && dead.Go == null)
            {
                // the model went with a car that was streamed out (it was sitting in it): make a new one
                if (RemoteTrace) Plugin.Log.LogInfo($"RemoteTrace: id {id} Go destroyed (seatedOn {(dead.SeatedOn != null ? "car" : "none")}, parent {(dead.Go != null && dead.Go.transform.parent != null ? dead.Go.transform.parent.name : "none")}) — recreating");
                All.Remove(id); Joined(id, dead.Name, dead.SteamId);
            }
            if (!All.TryGetValue(id, out var rp)) { Joined(id, "?"); rp = All[id]; }
            // unreliable: drop anything older than what we have (sequence wraps at 65536)
            if (rp.Has && (short)(seq - rp.Seq) <= 0) { StatesDropped++; return; }
            rp.Seq = seq; rp.Target = global; rp.Yaw = yaw; rp.SeatNet = seatNet; rp.SeatIdx = seatIdx;
            if (!rp.Has)
            {
                rp.Has = true;
                rp.Go.SetActive(true);
                rp.Go.transform.SetPositionAndRotation(mainscript.UnityPosFromGlobal(global), Quaternion.Euler(0f, yaw, 0f));
            }
        }

        public static void Pose(int id, PlayerLook.Pose pose)
        {
            if (!All.TryGetValue(id, out var rp) || rp.Mp == null || !rp.Has) return;
            PlayerLook.Apply(rp.Mp, pose);
            Poses++;
        }

        public static void Outfit(int id, int selected, bool[] enabled, Color[] colors)
        {
            Looks[id] = new Look { Sel = selected, En = enabled, Col = colors };
            if (!All.TryGetValue(id, out var rp)) { Joined(id, "?"); rp = All[id]; }
            if (rp.Look == null) return;
            ApplyLook(rp, selected, enabled, colors);
            Outfits++;
        }

        private static void ApplyLook(Rp rp, int selected, bool[] enabled, Color[] colors)
        {
            rp.Look.SetData(selected, enabled, colors);
            rp.Look.steamID = new CSteamID(rp.SteamId);
            rp.Look.Refresh(fromMultiplayer: true);
        }

        public static void Tick()
        {
            float k = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            _traceTick++;
            foreach (var rp in All.Values)
            {
                if (Mp.RemoteTrace && (_traceTick % 300) == 0)
                    Plugin.Log.LogInfo("RemoteTrace: All.Count " + All.Count + " ids[" + string.Join(",", All.Keys) + "] has[" + string.Join(",", All.Values.Select(v => v.Has.ToString())) + "]");
                if (!rp.Has || rp.Go == null) continue;
                var t = rp.Go.transform;
                // seated in a shared car: sit in this machine's copy of that seat (their global position comes from
                // their own, delayed copy of the car — following it would leave them metres behind at speed)
                if (rp.CachedSeatNet != rp.SeatNet || rp.CachedSeatIdx != rp.SeatIdx) { rp.CachedSeatNet = rp.SeatNet; rp.CachedSeatIdx = rp.SeatIdx; rp.CachedSeat = null; }
                var seat = rp.SeatNet != 0 ? Entities.SeatTransformCached(rp.SeatNet, rp.SeatIdx, ref rp.CachedCarRoot, ref rp.CachedSeat) : null;
                if ((_traceTick % 15) == 0)
                {
                    if (rp.Cols != null) foreach (var col in rp.Cols) if (col.enabled) { col.enabled = false; Plugin.Log.LogWarning("RemoteTrace: re-killed a live collider on the remote body " + rp.Go.name); }
                    if (rp.Ccs != null) foreach (var cc in rp.Ccs) if (cc.enabled) cc.enabled = false;
                }
                if (rp.SeatedOn != seat && SeatFlickerTraces < 8)
                {
                    SeatFlickerTraces++;
                    var why = rp.SeatNet == 0 ? "seatNet 0" : Json.Str(Entities.SeatWhy(rp.SeatNet, rp.SeatIdx));
                    Plugin.Log.LogInfo($"seat flicker: {rp.SeatedOn != null} -> {seat != null} {why}");
                }
                if (seat != null)
                {
                    if (rp.SeatedOn != seat) { t.SetParent(seat, false); rp.SeatedOn = seat; }
                    t.localPosition = Vector3.zero;
                    t.localRotation = Quaternion.identity;
                }
                else
                {
                    if (rp.SeatedOn != null || t.parent != null) { t.SetParent(null, true); rp.SeatedOn = null; }
                    var target = mainscript.UnityPosFromGlobal(rp.Target);
                    // a floating-origin shift moves the target by kilometres in Unity space: snap instead of sliding there
                    t.position = (t.position - target).sqrMagnitude > 100f * 100f ? target : Vector3.Lerp(t.position, target, k);
                    t.rotation = Quaternion.Slerp(t.rotation, Quaternion.Euler(0f, rp.Yaw, 0f), k);
                }
                if (rp.Mp != null) { try { rp.Mp.Upd(); } catch { } }
            }
        }

        /// Every remote player's body as a feet→head segment (THead follows their pose: crouching, prone, seated).
        /// Other players standing or walking (not seated in a car — the car is what an autopilot sees then).
        internal static List<Vector3> OnFootPositions()
        {
            var list = new List<Vector3>();
            foreach (var kv in All)
            {
                var rp = kv.Value;
                if (!rp.Has || rp.Go == null || !rp.Go.activeInHierarchy || rp.SeatNet != 0) continue;
                list.Add(rp.Go.transform.position);
            }
            return list;
        }

        /// Where a ray meets player `id`'s body: its hit boxes (the prefab's colliders — kept disabled so they can't push
        /// cars) are switched on for this one test and off again, with no physics step between, so nothing is pushed.
        /// The body part hit (its index in the prefab's colliders: the same on every machine), the point and normal.
        internal static bool BodyHit(int id, Ray ray, float maxDist, out int part, out RaycastHit hit)
        {
            part = -1; hit = default;
            if (!All.TryGetValue(id, out var rp) || rp.Cols == null || rp.Go == null || !rp.Go.activeInHierarchy) return false;
            float best = maxDist;
            for (int i = 0; i < rp.Cols.Length; i++)
            {
                var c = rp.Cols[i];
                if (c == null || c is CharacterController) continue;
                c.enabled = true;
                if (c.Raycast(ray, out var h, best)) { best = h.distance; hit = h; part = i; }
                c.enabled = false;
            }
            return part >= 0;
        }

        /// Body part `part` of player `id` (see BodyHit), or null.
        internal static Transform BodyPart(int id, int part) =>
            All.TryGetValue(id, out var rp) && rp.Cols != null && part >= 0 && part < rp.Cols.Length && rp.Cols[part] != null ? rp.Cols[part].transform : null;

        /// Remote players as targets for the game's creatures (Entities.Ai): body, head transform (what a creature's
        /// target follows), the body capsule and the Steam id.
        internal static IEnumerable<(int id, Transform body, Transform head, Vector3 feet, Vector3 top, ulong steam)> AiTargets()
        {
            foreach (var kv in All)
            {
                var rp = kv.Value;
                if (!rp.Has || rp.Go == null || !rp.Go.activeInHierarchy || rp.Mp == null || rp.Mp.THead == null) continue;
                var feet = rp.Go.transform.position; var head = rp.Mp.THead.position;
                yield return (kv.Key, rp.Go.transform, rp.Mp.THead, feet + (head - feet).normalized * 0.2f, head, rp.SteamId);
            }
        }

        internal static IEnumerable<(int id, Vector3 feet, Vector3 head)> BodyCapsules()
        {
            foreach (var kv in All)
            {
                var rp = kv.Value;
                if (!rp.Has || rp.Go == null || !rp.Go.activeInHierarchy) continue;
                var feet = rp.Go.transform.position;
                var head = rp.Mp != null && rp.Mp.THead != null ? rp.Mp.THead.position : feet + Vector3.up * 1.7f;
                yield return (kv.Key, feet + (head - feet).normalized * 0.2f, head);
            }
        }

        /// Is one of OUR remote players bound to this seat? (vanilla's inUseMulti is stale in our session —
        /// the seat-reservation messages of the old MP never flow; decide occupancy from our own bindings.)
        internal static bool AnyRemoteInSeat(seatscript st)
        {
            foreach (var rp in All.Values)
            {
                if (rp.SeatedOn == null || st == null) continue;
                if (rp.SeatedOn == st.transform || (st.sitPos != null && rp.SeatedOn == st.sitPos)) return true;
            }
            return false;
        }

        /// Remote players with the global position this machine shows them at (bridge `mp players`).
        public static string List()
        {
            var rows = new List<string>();
            foreach (var kv in All)
            {
                var rp = kv.Value;
                var shown = rp.Go != null ? mainscript.GlobalFromUnityPos(rp.Go.transform.position) : new Vector3d(0, 0, 0);
                rows.Add("{\"id\":" + kv.Key + ",\"name\":" + Json.Str(rp.Name) + ",\"has\":" + (rp.Has ? "true" : "false") +
                         ",\"seated\":" + (rp.SeatedOn != null ? "true" : "false") + ",\"seatNet\":" + rp.SeatNet + ",\"seatIdx\":" + rp.SeatIdx +
                         ",\"target\":[" + rp.Target.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + rp.Target.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + rp.Target.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "]" +
                         ",\"shown\":[" + shown.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + shown.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + shown.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "]}");
            }
            var me = Mp.LocalGlobal();
            return "{\"me\":[" + me.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + me.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + me.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "],\"players\":[" + string.Join(",", rows) + "]}";
        }

        public static long SeatFlickerTraces;
        private static int _traceTick;
        public static bool RemoteTrace
        {
            get => _remoteTrace;
            set { _remoteTrace = value; Mp.RemoteTrace = value; }
        }
        private static bool _remoteTrace;   // togglable: `set TLDRevamp.Net.Mp.RemoteTrace True` — wait, this is RemotePlayers'


        public static void Clear()
        {
            if (All.Count > 0) Plugin.Log.LogInfo("RemoteTrace: RemotePlayers.Clear() wiping " + All.Count + " entries\n" + System.Environment.StackTrace);
            foreach (var rp in All.Values) if (rp.Go != null) UnityEngine.Object.Destroy(rp.Go);
            All.Clear();
            Bodies.Clear();
            Looks.Clear();
        }
    }
}
