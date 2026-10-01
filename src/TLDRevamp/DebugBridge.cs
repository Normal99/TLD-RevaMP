using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace TLDRevamp
{
    /// Line-based TCP command server on 127.0.0.1 only. Each line is one command; the reply is one JSON line.
    /// Commands run on Unity's main thread (queued, executed in Update).
    public partial class DebugBridge
    {
        private readonly int _port;
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private readonly ConcurrentQueue<Job> _jobs = new ConcurrentQueue<Job>();

        private class Job
        {
            public string Cmd;
            public string Result;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim();
        }

        public DebugBridge(int port) => _port = port;

        public void Start()
        {
            _running = true;
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "TLDRevampBridge" };
            _thread.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _listener?.Stop(); } catch { }
            // Unblock any client thread waiting on the main thread so nothing is left hanging at quit
            while (_jobs.TryDequeue(out var job)) { job.Result = "{\"error\":\"shutting down\"}"; job.Done.Set(); }
        }

        private void AcceptLoop()
        {
            try
            {
                while (_running)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch { return; } // listener stopped
                    new Thread(() => Serve(client)) { IsBackground = true, Name = "TLDRevampBridgeClient" }.Start();
                }
            }
            catch (Exception e) { SafeLog("bridge accept loop error: " + e.Message); }
        }

        private static void SafeLog(string msg)
        {
            try { Plugin.Log.LogWarning(msg); } catch { }
        }

        private void Serve(TcpClient client)
        {
            // Must never throw: an unhandled exception on a background thread crashed the game once
            // (Mono died while formatting its stack trace, 2026-09-24).
            try { ServeLines(client); }
            catch (IOException) { } // client went away
            catch (ObjectDisposedException) { }
            catch (Exception e) { SafeLog("bridge client error: " + e.GetType().Name + ": " + e.Message); }
        }

        private void ServeLines(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
            {
                string line;
                while (_running && (line = reader.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    // A web page can make the browser send text to 127.0.0.1 (a form or fetch POST): its request line and
                    // headers arrive before the body, so dropping the connection there runs nothing it carries.
                    if (LooksLikeHttp(line)) { SafeLog("bridge: refused an HTTP request"); return; }
                    if (line.Trim() == "alive")
                    {
                        // Answered off the main thread: tells us whether Update is ticking at all
                        writer.WriteLine("{\"bridge\":true,\"updates\":" + Runner.Updates + "}");
                        continue;
                    }
                    var job = new Job { Cmd = line.Trim() };
                    _jobs.Enqueue(job);
                    // Main thread may be stalled on a loading screen; don't hang forever
                    writer.WriteLine(job.Done.Wait(30000) ? job.Result : "{\"error\":\"timeout\"}");
                }
            }
        }

        private static readonly System.Text.RegularExpressions.Regex HttpLine = new System.Text.RegularExpressions.Regex(
            @"^\s*(GET|POST|PUT|HEAD|OPTIONS|DELETE|PATCH|CONNECT|TRACE|PRI)\s+\S+\s+HTTP/|\bHTTP/[0-9]|^[A-Za-z][A-Za-z0-9-]*:\s",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        internal static bool LooksLikeHttp(string line) => HttpLine.IsMatch(line);

        public static double LastPumpMs, MaxPumpMs, TotalPumpMs;

        public void PumpMainThread()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            PumpJobs();
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            LastPumpMs = ms;
            TotalPumpMs += ms;
            if (ms > MaxPumpMs) MaxPumpMs = ms;
        }

        private void PumpJobs()
        {
            while (_jobs.TryDequeue(out var job))
            {
                try { job.Result = Execute(job.Cmd); }
                catch (Exception e) { job.Result = "{\"error\":" + Json.Str(e.ToString()) + "}"; }
                job.Done.Set();
            }
        }

        private static string Ground(float x, float z)
        {
            var h = GroundHit(x, z);
            if (h == null) return "{\"hit\":false}";
            return "{\"hit\":true,\"y\":" + h.Value.point.y.ToString("F2") + ",\"collider\":" + Json.Str(h.Value.collider.name) +
                   ",\"type\":" + Json.Str(h.Value.collider.GetType().Name) + "}";
        }

        /// The first STATIC collider under (x, z) that isn't the player (Unity coordinates); null if nothing is generated there.
        internal static RaycastHit? GroundHit(float x, float z)
        {
            Transform playerRoot = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.transform.root : null;
            var hits = Physics.RaycastAll(new Vector3(x, 5000f, z), Vector3.down, 10000f, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (playerRoot != null && h.collider.transform.root == playerRoot) continue; // the player, held items
                if (h.collider.attachedRigidbody != null) continue;                           // cars, loose items
                return h;
            }
            return null;
        }

        // ---------------------------------------------------------------- tpground
        // It used to take the TOPMOST static surface (roofs: 'teto'; something high by the mansion gate) and, where the
        // ground wasn't generated yet, threw the player 120 m up to fall onto it (16 of 37 printed replies were 'hop';
        // aimp runs 5/6 lost the laptop to that fall before the test began). Now: the lowest surface a player can stand
        // on (room above it) at or above the terrain — a road over terrain, a floor over the ground under a house, never
        // the roof; with a height given, the floor nearest below it. Ground not generated yet: the player is HELD at the
        // spot (kinematic, no fall) and set down once the ground is there (FixedTick), up to 30 s.
        private static double _tpGx, _tpGz; private static double? _tpGy;
        private static float _tpUntil; private static bool _tpWaiting;
        private static string _tpOn = ""; private static long _tpPlaced, _tpWaited;

        /// The surface to stand on at (x, z) — see above. Null while nothing is generated there.
        internal static RaycastHit? StandHit(float x, float z, float? belowY)
        {
            Transform playerRoot = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.transform.root : null;
            var hits = Physics.RaycastAll(new Vector3(x, 5000f, z), Vector3.down, 10000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var list = new System.Collections.Generic.List<RaycastHit>();
            float terrainY = float.NegativeInfinity;
            foreach (var h in hits)
            {
                if (playerRoot != null && h.collider.transform.root == playerRoot) continue;   // the player, held items
                if (h.collider.attachedRigidbody != null) continue;                            // cars, loose items
                if (h.collider.GetType().Name == "TerrainCollider") terrainY = Mathf.Max(terrainY, h.point.y);
                list.Add(h);
            }
            if (list.Count == 0) return null;
            if (belowY.HasValue)
            {
                list.Sort((a, b) => b.point.y.CompareTo(a.point.y));   // highest first: the nearest below the height
                foreach (var h in list) if (h.point.y <= belowY.Value && Room(h.point, playerRoot)) return h;
                return null;
            }
            list.Sort((a, b) => a.point.y.CompareTo(b.point.y));       // lowest first
            foreach (var h in list) if (h.point.y >= terrainY - 0.3f && Room(h.point, playerRoot)) return h;
            return null;
        }

        private static readonly Collider[] RoomBuf = new Collider[16];
        private static bool Room(Vector3 p, Transform playerRoot)
        {
            int n = Physics.OverlapCapsuleNonAlloc(p + Vector3.up * 0.45f, p + Vector3.up * 1.7f, 0.3f, RoomBuf, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = RoomBuf[i];
                if (playerRoot != null && c.transform.root == playerRoot) continue;
                if (c.attachedRigidbody != null) continue;   // a loose item isn't a ceiling
                return false;
            }
            return true;
        }

        private static string TpGround(double gx, double gz, double? gy)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"not in game\"}";
            _tpGx = gx; _tpGz = gz; _tpGy = gy;
            _tpWaiting = false;
            if (TryPlace(pl)) return "{\"tpground\":true,\"on\":" + Json.Str(_tpOn) + "}";
            // not generated yet: hold the player at the spot (the world streams in around it) — no hop, no fall
            var u = mainscript.UnityPosFromGlobal(new Vector3d(gx, gy ?? mainscript.GlobalFromUnityPos(pl.transform.position).y, gz));
            pl.Teleport(u);
            TpSettle(pl);
            _tpWaiting = true; _tpUntil = Time.realtimeSinceStartup + 30f; _tpWaited++;
            return "{\"tpground\":\"waiting\"}";
        }

        private static bool TryPlace(fpscontroller pl)
        {
            var u = mainscript.UnityPosFromGlobal(new Vector3d(_tpGx, _tpGy ?? 0.0, _tpGz));
            var h = StandHit(u.x, u.z, _tpGy.HasValue ? u.y : (float?)null);
            if (h == null) return false;
            pl.godModeTillGrounded = true;
            pl.Teleport(new Vector3(u.x, h.Value.point.y + 1.1f, u.z));
            TpSettle(pl);
            _tpHeld = false;
            _tpOn = h.Value.collider.name; _tpPlaced++;
            return true;
        }

        private static string TpStatus() =>
            "{\"waiting\":" + (_tpWaiting ? "true" : "false") + ",\"on\":" + Json.Str(_tpOn) + ",\"placed\":" + _tpPlaced + ",\"waited\":" + _tpWaited + "}";

        private static bool _tpHeld;   // tpg … hold: the next tpground settles too
        private static float _tpCalmUntil;

        /// After a long test teleport: the floating-origin shift now (same frame), physics synced, the body still and the
        /// fall-damage baseline reset for half a second, so the arrival starts clean (no fall from the old height's speed).
        /// The 2–10 million m/s launches of farworld runs 6–8 were NOT this body: copies of items attached at the spawn
        /// building were held at pre-shift coordinates (Fixes.FreeAttachPoints) and their kinematic bodies swept 201 km per
        /// physics step, carrying the player along (tpprobe trace, v0.57.97).
        private static void TpSettle(fpscontroller pl)
        {
            if (pl.RB != null) pl.RB.position = pl.transform.position;
            if (mainscript.s.player.transform.position.sqrMagnitude > 6250000f) mainscript.s.VisszaRakas();
            if (pl.RB != null) pl.RB.position = pl.transform.position;
            Physics.SyncTransforms();
            if (TpStoreLeft) StoreLeftBehind();
            _tpCalmUntil = Time.realtimeSinceStartup + 0.5f;
            TpCalm(pl);
        }

        /// A teleport leaves in one frame what a driver leaves over minutes: the game stores items beyond itemRemoveDist
        /// once a second (itemPlaceRemoveScript.DoUpd), and in that second the ground under them can already be gone —
        /// farworld run 14: the far player's car (all 33 items) was stored 1.21 m low, half a second of falling through
        /// the unloaded site. Driving away, the game stores them at 300 m, long before the ground goes. Here the game's
        /// own pass runs at once, with the generation centres where the player now is.
        public static bool TpStoreLeft = true;
        public static long TpStores;

        private static void StoreLeftBehind()
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.genArounds == null || itemPlaceRemoveScript.s == null || savedatascript.s == null) return;
            foreach (var ga in map.genArounds) if (ga != null && ga.t != null) ga.upos = ga.t.position;
            itemPlaceRemoveScript.s.RemoveStuff();
            TpStores++;
        }

        private static void TpCalm(fpscontroller pl)
        {
            if (pl == null) return;
            if (pl.RB != null) { pl.RB.velocity = Vector3.zero; pl.RB.angularVelocity = Vector3.zero; }
            pl.lastVelocity = 0f; pl.currentVelocity = 0f;
        }

        /// Plugin.FixedUpdate: keep the body calm for a moment after a settled teleport.
        public static void FixedTick()
        {
            if (mainscript.s == null) return;
            var pl = mainscript.s.player;
            // held (tpg … hold) until tpground/tprelease — not only the first half second: waiting for the target's
            // ground can take longer, and a 120 m fall with the game's fall damage killed the host there (farworld
            // runs 11/16: its ragdoll became an item of the site under test)
            if (_tpWaiting && pl != null)
            {
                if (TryPlace(pl) || Time.realtimeSinceStartup > _tpUntil) { _tpWaiting = false; return; }
                // held at the spot until the ground is there
                var hold = mainscript.UnityPosFromGlobal(new Vector3d(_tpGx, _tpGy ?? mainscript.GlobalFromUnityPos(pl.transform.position).y, _tpGz));
                if (!_tpGy.HasValue) hold.y = pl.transform.position.y;
                pl.transform.position = hold; if (pl.RB != null) pl.RB.position = hold;
                TpCalm(pl); pl.godModeTillGrounded = true;
                return;
            }
            if (_tpHeld && pl != null) { TpCalm(pl); pl.godModeTillGrounded = true; return; }
            if (_tpCalmUntil <= 0f) return;
            if (Time.realtimeSinceStartup < _tpCalmUntil) { TpCalm(pl); return; }
            _tpCalmUntil = 0f;
        }

        internal static string Execute(string line)
        {
            var parts = line.Split(new[] { ' ' }, 2);
            string cmd = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1] : "";
            var p = Plugin.Instance;

            switch (cmd)
            {
                case "ping": return "{\"ok\":true,\"version\":\"" + Plugin.Version + "\",\"build\":\"" + Plugin.BuildHash + "\"}";
                case "stats": return p.Telemetry.FrameStatsJson();
                case "scene": return p.Telemetry.SceneStatsJson();
                case "breakdown": return p.Telemetry.Breakdown.Json();
                case "items": return PhysicsLab.Items(arg);
                case "physics": return PhysicsLab.Stats();
                case "followdump": return PhysicsLab.FollowDump(arg.Length > 0 ? int.Parse(arg) : 6);
                case "render": return PhysicsLab.RenderStats();
                case "cameras": return PhysicsLab.Cameras();
                case "renderinv": return RenderLab.Inventory();
                case "rtoggle": { var tv = arg.Split(' '); return RenderLab.Toggle(tv[0], tv.Length > 1 ? tv[1] : ""); }
                case "mirrors": return RenderLab.Mirrors();
                case "particles": return RenderLab.Particles();
                case "rebaseprof": return arg == "stop" ? RebaseLab.Stop() : RebaseLab.Start();
                case "pourtest": return RebaseLab.PourTest(arg);
                case "uievents": return UiLab.Events(arg);
                case "uiclick": return UiLab.Click(arg);
                case "settingsinst": return UiLab.SettingsInstances();
                case "loadsave": return TestSafety.LoadSave(arg);
                case "worldhash":
                {
                    var wa = arg.Split(' ');
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    return WorldHash.Hash(double.Parse(wa[0], ic), double.Parse(wa[1], ic), double.Parse(wa[2], ic), wa.Length > 3 && wa[3] == "detail");
                }
                case "gensettings": return WorldHash.GenSettings();
                case "heightdbg": { var ha = arg.Split(' '); var hic = System.Globalization.CultureInfo.InvariantCulture; return WorldHash.HeightDbg(double.Parse(ha[0], hic), double.Parse(ha[1], hic)); }
                case "stale": return WorldHash.Stale(arg == "detail");
                case "mods": return ModHost.Status();
                case "traffic": return AutopilotBridge.Status();
                case "seatraw": return Net.Entities.SeatRaw();
                case "devspawn":
                {
                    // the dev menu's own spawn (kaposztaleves.Spawn → mainscript.Spawn(g, color, worn, rtype, paint))
                    int idx = int.Parse(arg);
                    var g = itemdatabase.s.items[idx];
                    if (g == null || kaposztaleves.s == null) return "{\"error\":\"no item " + idx + "\"}";
                    kaposztaleves.s.Spawn(g, Color.white);
                    return "{\"spawned\":" + Json.Str(g.name) + "}";
                }
                case "seatlook": return Net.Entities.SeatLook();
                case "sitseat":
                {
                    var sp = arg.Split(' ');
                    return Net.Entities.SitSeat(int.Parse(sp[0]), sp.Length > 1 && sp[1] == "redirect");
                }
                case "streamlab":
                {
                    var sa = arg.Split(' ');
                    if (sa[0] == "start") return Net.StreamLab.Start(sa.Length > 1 ? float.Parse(sa[1], System.Globalization.CultureInfo.InvariantCulture) : 0.5f);
                    return Net.StreamLab.Status();
                }
                case "vehlab":
                {
                    var va = arg.Split(' ');
                    switch (va[0])
                    {
                        case "start": return Net.VehicleLab.Start(va.Length > 1 ? float.Parse(va[1], System.Globalization.CultureInfo.InvariantCulture) : 6f);
                        case "status": return Net.VehicleLab.Status();
                        case "stop": return Net.VehicleLab.Stop();
                        default: return "{\"error\":\"vehlab start [offset] | status | stop\"}";
                    }
                }
                case "itemsnap":
                {
                    var ia = arg.Split(' ');
                    if (ia[0] == "test") return Net.ItemSnapshot.Test(ia.Length > 1 ? int.Parse(ia[1]) : 10);
                    if (ia[0] == "group") return Net.ItemSnapshot.TestGroup();
                    return "{\"error\":\"itemsnap test [n]\"}";
                }
                case "mp":
                {
                    var ma = arg.Split(' ');
                    var mic = System.Globalization.CultureInfo.InvariantCulture;
                    switch (ma[0])
                    {
                        case "host": return Net.Mp.Host(ma.Length > 1 ? ushort.Parse(ma[1]) : (ushort)27070);
                        case "join": return Net.Mp.Join(ma[1]);
                        case "joinid": return Net.Mp.JoinFriend(ulong.Parse(ma[1]));
                        case "botcar": return Net.Mp.CaptureBotCar();
                        case "copies": return Net.Entities.CopiesToggle(ma[1], ma.Length > 2 && ma[2] == "on");
                        case "bots": return Net.Mp.AddBots(int.Parse(ma[1]), ma.Length > 2 ? float.Parse(ma[2], mic) : 30f, ma.Length > 3 ? float.Parse(ma[3], mic) : 5f);
                        case "status": return Net.Mp.Status();
                        case "save": return Net.Mp.SaveTail();
                        case "players": return Net.RemotePlayers.List();
                        case "share": return Net.Entities.ShareCar(Net.Entities.NearestCar());
                        case "entities": return Net.Entities.Status();
                        case "entstats": return Net.Entities.ResetStats();
                        case "jumps": return Net.Entities.JumpLog();
                        case "entdebug": return Net.Entities.Debug();
                        case "worldcar": return Net.Entities.WorldCar();
                        case "pickupnear": return Net.Entities.PickupNear(ma.Length > 1 ? float.Parse(ma[1], System.Globalization.CultureInfo.InvariantCulture) : 4f);
                        case "drop": return Net.Entities.DropHeld();
                        case "sitpassenger": return Net.Entities.SitPassenger(uint.Parse(ma[1]));
                        case "carbreak": return Net.Entities.CarBreak(uint.Parse(ma[1]));
                        case "fluids": return Net.Entities.Fluids(uint.Parse(ma[1]));
                        case "holdnet": return Net.FillLab.HoldNet(uint.Parse(ma[1]));
                        case "walkat": return Net.FillLab.WalkAt(uint.Parse(ma[1]), float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture));
                        case "holdfill": return ma[1] == "stop" ? Net.FillLab.Stop() : ma[1] == "status" ? Net.FillLab.Status() : Net.FillLab.Start(uint.Parse(ma[1]), uint.Parse(ma[2]), int.Parse(ma[3]));
                        case "fill": return Net.Entities.Fill(uint.Parse(ma[1]), int.Parse(ma[2]), ma[3], float.Parse(ma[4], System.Globalization.CultureInfo.InvariantCulture));
                        case "pour": return ma[1] == "stop" ? Net.Entities.Pour(0, 0, 0, false) : ma[1] == "status" ? Net.Entities.PourStatus() : Net.Entities.Pour(uint.Parse(ma[1]), uint.Parse(ma[2]), int.Parse(ma[3]), true);
                        case "sitdriver": return Net.Entities.SitDriver(uint.Parse(ma[1]));
                        case "seats": return Net.Entities.SeatDump(uint.Parse(ma[1]));
                        case "moveitem": { var cic5 = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.MoveItem(uint.Parse(ma[1]), float.Parse(ma[2], cic5), float.Parse(ma[3], cic5)); }
                        case "farstore": { var cic7 = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.FarStore(double.Parse(ma[1], cic7), double.Parse(ma[2], cic7), double.Parse(ma[3], cic7)); }
                        case "sitestate": { var cic6 = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.SiteState(double.Parse(ma[1], cic6), double.Parse(ma[2], cic6), double.Parse(ma[3], cic6)); }
                        case "itemslist": { var cic4 = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.ItemsList(double.Parse(ma[1], cic4), double.Parse(ma[2], cic4), double.Parse(ma[3], cic4)); }
                        case "itemsnear": { var cic3 = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.ItemsNear(double.Parse(ma[1], cic3), double.Parse(ma[2], cic3), double.Parse(ma[3], cic3)); }
                        case "carsnear": { var cic2 = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.CarsNear(double.Parse(ma[1], cic2), double.Parse(ma[2], cic2), double.Parse(ma[3], cic2)); }
                        case "detachpart": return Net.Entities.DetachPart(int.Parse(ma[1]));
                        case "pickupnet": return Net.Entities.PickupNet(uint.Parse(ma[1]));
                        case "wrench": return Net.Entities.Wrench(uint.Parse(ma[1]), int.Parse(ma[2]));
                        case "falloff": return Net.Entities.FallOff(uint.Parse(ma[1]), int.Parse(ma[2]));
                        case "attach": return Net.Entities.AttachStats();
                        case "crashstop": return Net.Entities.CrashStop(uint.Parse(ma[1]));
                        case "partin": return Net.Entities.PartIn(uint.Parse(ma[1]), uint.Parse(ma[2]));
                        case "attachto": return Net.Entities.AttachTo(uint.Parse(ma[1]), uint.Parse(ma[2]), int.Parse(ma[3]));
                        case "lookpick": return Net.Entities.LookPick(uint.Parse(ma[1]), ma.Length > 2 && ma[2] == "pick");
                        case "attachedparts": return Net.Entities.AttachedParts(uint.Parse(ma[1]));
                        case "shotfx": return Net.Entities.ShotFxStats();
                        case "blasts": return Net.Entities.ExplosionStats();
                        case "ai": return Net.AiProbe.List();
                        case "aiids": return Net.AiProbe.Ids();
                        case "aicols": return Net.AiProbe.Cols(ma.Length > 1 ? ma[1] : "");
                        case "aivision": return Net.AiProbe.Vision();
                        case "aistats": return Net.Entities.AiStats();
                        case "aisee": return Net.Entities.AiSee();
                        case "poiusables": return Net.Entities.PoiUsableStats();
                        case "poius": return Net.Entities.PoiList(ma.Length > 1 ? float.Parse(ma[1], mic) : 100f);
                        case "poiu": return Net.Entities.PoiGet(ma);
                        case "poiuse": return Net.Entities.PoiUse(ma);
                        case "decals": { var ci = System.Globalization.CultureInfo.InvariantCulture; return ma.Length >= 5 ? Net.Entities.Decals(0, double.Parse(ma[1], ci), double.Parse(ma[2], ci), double.Parse(ma[3], ci), float.Parse(ma[4], ci)) : Net.Entities.Decals(uint.Parse(ma[1]), 0, 0, 0, 0); }
                        case "shootat": return Net.PlayerCombat.ShootAt(int.Parse(ma[1]));
                        case "shootpoint": { var ci = System.Globalization.CultureInfo.InvariantCulture; return Net.PlayerCombat.ShootPoint(double.Parse(ma[1], ci), double.Parse(ma[2], ci), double.Parse(ma[3], ci), ma.Length > 4 ? ma[4] : ""); }
                        case "combat": return Net.PlayerCombat.Stats();
                        case "physical": return Net.Entities.PhysicalStatus();
                        case "contacts": return Net.Entities.Contacts(ma.Length > 1 ? ma[1] : "");
                        case "crashauth": return Net.Entities.CrashAuthorityStatus();
                        case "entstate": return Net.Entities.EntState(uint.Parse(ma[1]));
                        case "resyncdiff": return Net.Entities.ResyncDiff(uint.Parse(ma[1]));
                        case "entitems": return Net.Entities.EntItems(uint.Parse(ma[1]));
                        case "entpoke": return Net.Entities.EntPoke(uint.Parse(ma[1]), ma[2], ma.Length > 4 ? int.Parse(ma[3]) : 0, float.Parse(ma[ma.Length - 1], System.Globalization.CultureInfo.InvariantCulture));
                        case "stop": return Net.Mp.Stop();
                        default: return "{\"error\":\"mp host [port] | join <ip:port> | bots <n> [radius] [speed] | status | stop\"}";
                    }
                }
                case "net":
                {
                    var na = arg.Split(' ');
                    switch (na[0])
                    {
                        case "pair": return Net.NetLab.Pair(na.Length > 1 ? int.Parse(na[1]) : 1000, na.Length > 2 ? int.Parse(na[2]) : 64, na.Length > 3 && na[3] == "loopback");
                        case "status": return Net.NetLab.Status();
                        case "relay": return Net.NetLab.Relay(na.Length > 1 ? na[1] : "off");
                        case "pops": return Net.SteamCompat.Pops();
                        case "route": return Net.Mp.Route();
                        case "stats": return Net.Mp.NetStats();
                        case "me": return "{\"steamId\":" + Json.Str(Steamworks.SteamUser.GetSteamID().m_SteamID.ToString()) + "}";
                        case "stop": return Net.NetLab.Stop();
                        case "probe": return Net.NetLab.Probe(int.Parse(na[1]));
                        case "lag":
                        {
                            var lic = System.Globalization.CultureInfo.InvariantCulture;
                            return Net.NetLab.Lag(int.Parse(na[1]), na.Length > 2 ? float.Parse(na[2], lic) : 0f, na.Length > 3 ? float.Parse(na[3], lic) : 0f, na.Length > 4 ? int.Parse(na[4]) : 0);
                        }
                        default: return "{\"error\":\"net pair <count> <size> [loopback] | status | stop\"}";
                    }
                }
                case "fakeplayers":
                {
                    var fa = arg.Split(' ');
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    return MpLab.Set(int.Parse(fa[0]), fa.Length > 1 ? float.Parse(fa[1], ic) : 25f, fa.Length > 2 ? float.Parse(fa[2], ic) : 5000f);
                }
                case "uidump": { var ua = arg.Split(' '); return UiLab.Dump(ua[0], ua.Length > 1 ? int.Parse(ua[1]) : 3); }
                case "matshare": return RenderLab.MaterialSharing(arg);
                case "instinfo": return RenderLab.InstancingInfo();
                case "instancing": return RenderLab.InstancingOn();
                case "visroots": return RenderLab.VisibleByRoot(arg.Length > 0 ? int.Parse(arg) : 25);
                case "rendertime": return arg == "stop" ? RenderLab.TimingStop() : RenderLab.TimingStart();
                case "camera":
                {
                    var camArgs = arg.Split(' ');
                    return PhysicsLab.SetCamera(camArgs[0], camArgs[1] == "on");
                }
                case "drivein":
                {
                    if (arg.Length == 0) return DriveLab.GetIn();
                    var dv = arg.Split(' ');
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    return DriveLab.GetIn(new Vector3(float.Parse(dv[0], ic), float.Parse(dv[1], ic), float.Parse(dv[2], ic)));
                }
                case "driveout": return DriveLab.GetOut();
                case "roadpoint": return DriveLab.RoadPoint();
                case "brakecar": return DriveLab.Brake();
                case "handbrake": {
                    var pl = mainscript.s != null ? mainscript.s.player : null;
                    var car = pl != null ? pl.Car : null;
                    if (car == null) return "{\"error\":\"not in a car\"}";
                    bool on = arg.Trim().ToLower() != "off";
                    car.BhandBrake = on;
                    return "{\"handbrake\":" + (on ? "true" : "false") + "}";
                }
                case "shot": UnityEngine.ScreenCapture.CaptureScreenshot("mpshot_" + arg + ".png"); return "{\"shot\":\"" + arg + "\"}";
                case "nudge": return DriveLab.Nudge(arg.Length > 0 ? float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) : 12f);
                case "fuelup": return DriveLab.FuelUp();
                case "pois": { var pp = arg.Split(' '); var cip = System.Globalization.CultureInfo.InvariantCulture; return Pois(double.Parse(pp[0], cip), double.Parse(pp[1], cip), double.Parse(pp[2], cip)); }
                case "seatwhy": { var sw2 = arg.Split(' '); return Net.Entities.SeatWhy(uint.Parse(sw2[0]), int.Parse(sw2[1])); }
                case "leasetable": return Net.Entities.LeaseTable();
                case "picknearest": return Net.Entities.PickupNearest(arg.Length > 0 ? float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) : 3f);
                case "dropheld": return Net.Entities.DropHeld();
                case "pickupcar": return Net.Entities.PickupCar(uint.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
                case "deleteshare": return Net.Entities.DeleteShared(uint.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
                case "leasearm": Net.Entities.LeaseArm(arg); return "{\"armed\":" + Json.Str(arg) + "}";
                case "gpos":
                {
                    var u = mainscript.s.player.transform.position;
                    var gp = mainscript.GlobalFromUnityPos(u);
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    return "{\"ux\":" + u.x.ToString("F1", ci) + ",\"uz\":" + u.z.ToString("F1", ci) +
                           ",\"gx\":" + gp.x.ToString("F2", ci) + ",\"gy\":" + gp.y.ToString("F2", ci) + ",\"gz\":" + gp.z.ToString("F2", ci) + "}";
                }
                case "tprelease":
                    _tpHeld = false; _tpCalmUntil = 0f;
                    return "{\"released\":true}";
                case "tpground":
                {
                    // `tpground gx gz [gy]`: stand ON the ground at a GLOBAL position (TpGround below); `tpground status`
                    var tp = arg.Split(' ');
                    if (tp[0] == "status") return TpStatus();
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    return TpGround(double.Parse(tp[0], ci), double.Parse(tp[1], ci), tp.Length > 2 ? double.Parse(tp[2], ci) : (double?)null);
                }
                case "spawncarg":
                {
                    // a test car ON the ground at a GLOBAL position (each machine converts with its own floating origin)
                    var sg = arg.Split(' ');
                    var uvc = mainscript.UnityPosFromGlobal(new Vector3d(double.Parse(sg[2], System.Globalization.CultureInfo.InvariantCulture), 0, double.Parse(sg[3], System.Globalization.CultureInfo.InvariantCulture)));
                    var ghc = StandHit(uvc.x, uvc.z, null);   // the road or ground, not a roof
                    if (ghc == null) return "{\"error\":\"no ground generated there\"}";
                    return PhysicsLab.SpawnCar(int.Parse(sg[0]), int.Parse(sg[1]), new Vector3(uvc.x, ghc.Value.point.y + 0.6f, uvc.z), sg.Length > 4 ? float.Parse(sg[4], System.Globalization.CultureInfo.InvariantCulture) : 0f);
                }
                case "tpg":
                {
                    // teleport to a GLOBAL position (floating-origin safe between machines): convert, hop high, ground
                    var gp = arg.Split(' ');
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    var gv = new Vector3d(double.Parse(gp[0], ci), 0, double.Parse(gp[1], ci));
                    var uv = mainscript.UnityPosFromGlobal(gv);
                    var pl = mainscript.s.player;
                    // y: 120 m above the player's CURRENT height — after an origin shift (the y part too) heights at two
                    // places 200 km apart differ by hundreds of metres in Unity space, so this can be under the target's
                    // terrain; pushed out of it, the body flew off at 2,040,716 m/s (farworld run 6: landings 129–1,836 km
                    // off, a fall-damage death). `tpg gx gz hold`: settled (TpSettle) now and again at `tpground`.
                    pl.Teleport(new Vector3(uv.x, pl.transform.position.y + 120, uv.z));
                    pl.godModeTillGrounded = true;
                    if (gp.Length > 2 && gp[2] == "hold") { _tpHeld = true; TpSettle(pl); }
                    return "{\"tpg\":true,\"ux\":" + uv.x.ToString("F1", ci) + ",\"uz\":" + uv.z.ToString("F1", ci) + "}";
                }
                case "worldgen": return arg == "reset" ? WorldGenLab.Reset() : WorldGenLab.Snapshot();
                case "alignstats": return WorldGenLab.AlignStats();
                case "randbench": return WorldGenLab.RandBench(arg.Length > 0 ? int.Parse(arg) : 20000);
                case "allocprobe": return WorldGenLab.AllocProbe();
                case "alloc": return WorldGenLab.AllocStats(arg == "reset");
                case "noisecheck": return Fixes.FastNoise.Check(arg.Length > 0 ? int.Parse(arg) : 1000000);
                case "randcheck": return Fixes.FastRandom.Check(arg.Length > 0 ? int.Parse(arg) : 1000000);
                case "carface":
                {
                    var fv = arg.Split(' ');
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    return DriveLab.FaceCar(new Vector3(float.Parse(fv[0], ic), float.Parse(fv[1], ic), float.Parse(fv[2], ic)), float.Parse(fv[3], ic));
                }
                case "near": return DriveLab.Near(arg.Length > 0 ? float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) : 15f);
                case "autodrive":
                    if (arg.StartsWith("on")) return DriveLab.Start(arg.Length > 3 ? float.Parse(arg.Substring(3), System.Globalization.CultureInfo.InvariantCulture) : 20f);
                    if (arg == "off") return DriveLab.Stop();
                    return DriveLab.Stats();
                case "sleepcars": return PhysicsLab.SleepCars();
                case "carsleep": return PhysicsLab.CarSleepState();
                case "wheelbench": return PhysicsLab.WheelBench(arg.Length > 0 ? int.Parse(arg) : 100);
                case "awake": return PhysicsLab.Awake(arg.Length > 0 ? int.Parse(arg) : 25);
                case "clearpile": return PhysicsLab.ClearPile();
                case "crash":
                {
                    // collision tests: `crash rec start [radius]`, `crash rec stop`, `crash launch <m/s> [gx gz]`
                    var cra = arg.Split(' '); var cic = System.Globalization.CultureInfo.InvariantCulture;
                    if (cra[0] == "rec") return cra.Length > 1 && cra[1] == "stop" ? CrashLab.Stop(cra.Length > 2 ? float.Parse(cra[2], cic) : -1f) : CrashLab.Start(cra.Length > 2 ? float.Parse(cra[2], cic) : 80f);
                    if (cra[0] == "cars") return CrashLab.CarsNear(cra.Length > 1 ? float.Parse(cra[1], cic) : 40f);
                    if (cra[0] == "launch")
                    {
                        bool hold = cra[cra.Length - 1] == "hold"; int nargs = hold ? cra.Length - 1 : cra.Length;
                        return CrashLab.Launch(float.Parse(cra[1], cic), nargs > 3 ? new Vector3d(double.Parse(cra[2], cic), 0, double.Parse(cra[3], cic)) : (Vector3d?)null, hold);
                    }
                    return "{\"error\":\"crash rec start|stop, crash launch <m/s> [gx gz]\"}";
                }
                case "spawncar": { var sa = arg.Split(' '); return PhysicsLab.SpawnCar(int.Parse(sa[0]), int.Parse(sa[1]), new Vector3(float.Parse(sa[2], System.Globalization.CultureInfo.InvariantCulture), float.Parse(sa[3], System.Globalization.CultureInfo.InvariantCulture), float.Parse(sa[4], System.Globalization.CultureInfo.InvariantCulture)), sa.Length > 5 ? float.Parse(sa[5], System.Globalization.CultureInfo.InvariantCulture) : 0f); }
                case "spawnpile":
                {
                    // spawnpile <ids,comma> <count> <x> <y> <z> <cols> <spacing> <height> <seed>
                    var a = arg.Split(' ');
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    return PhysicsLab.SpawnPile(a[0], int.Parse(a[1]),
                        new Vector3(float.Parse(a[2], ci), float.Parse(a[3], ci), float.Parse(a[4], ci)),
                        int.Parse(a[5]), float.Parse(a[6], ci), float.Parse(a[7], ci), int.Parse(a[8]));
                }
                case "reset": p.Telemetry.Reset(); return "{\"ok\":true}";
                case "report": return "{\"dir\":" + Json.Str(p.Feedback.Capture(arg)) + "}";
                case "screenshot":
                    string path = Path.Combine(FeedbackReporter.Root, "shot-" + DateTime.Now.ToString("HHmmss") + ".png");
                    Directory.CreateDirectory(FeedbackReporter.Root);
                    ScreenCapture.CaptureScreenshot(path);
                    return "{\"path\":" + Json.Str(path) + "}";
                case "poigens": return Fixes.PoiItemsStore.Gens();
                case "poistore": return Fixes.PoiItemsStore.Stats;
                case "unfreezeground": return Fixes.UnfreezeOnGround.Stats;
                case "attachlog": return Diagnostics.AttachLog.Stats;
                case "attachpoints":
                {
                    // attached-without-parenting items (attachablescript.Update puts them on `point` every frame): where the
                    // point hangs and whether the floating-origin shift moves it
                    var rows = new System.Collections.Generic.List<string>();
                    foreach (var at in UnityEngine.Object.FindObjectsOfType<attachablescript>())
                    {
                        if (at == null || !at.attached || at.parentAtAttach) continue;
                        var pt = at.point;
                        string chain = "";
                        for (var t = pt; t != null && chain.Length < 200; t = t.parent)
                            chain += t.name + (t.GetComponent<visszarako>() != null ? "[vr" + (t.GetComponent<visszarako>().dont ? " dont" : "") + (t.GetComponent<visszarako>().moveWhenParented ? " mwp" : "") + "]" : "") + "/";
                        rows.Add("[" + Json.Str(at.name) + "," + Json.Str(chain) + "," + Json.Str(mainscript.GlobalFromUnityPos(at.transform.position).ToString()) + "," + Json.Str(pt != null ? mainscript.GlobalFromUnityPos(pt.position).ToString() : "-") + "]");
                    }
                    return "{\"count\":" + rows.Count + ",\"items\":[" + string.Join(",", rows) + "]}";
                }
                case "whereis":
                {
                    // every loaded item whose name contains `arg`: id, global position, what it hangs on (parent root,
                    // attach point root and slot) — the loose car parts of farworld B
                    var rows = new System.Collections.Generic.List<string>();
                    foreach (var kvi in savedatascript.s.items)
                    {
                        var it = kvi.Value;
                        if (it == null || it.name.IndexOf(arg, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var at = it.attachable;
                        string onPt = "-";
                        if (at != null && at.point != null)
                        {
                            var pr = at.point.GetComponentInParent<tosaveitemscript>();
                            onPt = at.point.root.name + (pr != null ? " item #" + pr.idInSave + " " + pr.name : "");
                        }
                        var par = it.transform.parent != null ? it.transform.parent.GetComponentInParent<tosaveitemscript>() : null;
                        rows.Add("[" + kvi.Key + "," + Json.Str(it.name) + "," + Json.Str(mainscript.GlobalFromUnityPos(it.transform.position).ToString()) + "," +
                                 Json.Str("attached " + (at != null && at.attached) + " slot " + (at != null && at.slot != null ? at.slot.name : "-") + " on " + onPt +
                                          " parent " + (par != null ? "#" + par.idInSave + " " + par.name : "-")) + "]");
                    }
                    return "{\"count\":" + rows.Count + ",\"items\":[" + string.Join(",", rows) + "]}";
                }
                case "get": return "{\"value\":" + Reflect.Get(arg) + "}";
                case "set":
                    var kv = arg.Split(new[] { ' ' }, 2);
                    return "{\"value\":" + Reflect.Set(kv[0], kv[1]) + "}";
                case "groundg":
                {
                    // ground height at a GLOBAL point, in global y — plus this machine's floating origin (machines must agree)
                    var gg = arg.Split(' ');
                    var ci2 = System.Globalization.CultureInfo.InvariantCulture;
                    var ug = mainscript.UnityPosFromGlobal(new Vector3d(double.Parse(gg[0], ci2), 0, double.Parse(gg[1], ci2)));
                    var hit = GroundHit(ug.x, ug.z);
                    var o = mainscript.s.visszarakva;
                    return "{\"origin\":[" + o.x.ToString("F3", ci2) + "," + o.y.ToString("F3", ci2) + "," + o.z.ToString("F3", ci2) + "],\"gy\":" +
                           (hit != null ? (hit.Value.point.y - o.y).ToString("F3", ci2) : "null") + ",\"on\":" + Json.Str(hit != null ? hit.Value.collider.name : "") + "}";
                }
                case "ground":
                    // Terrain height at (x,z): ray from far above, first STATIC collider that isn't the player.
                    // {"hit":false} if the ground there isn't generated yet.
                    var gz = arg.Split(' ');
                    return Ground(float.Parse(gz[0]), float.Parse(gz[1]));
                case "loopprof":
                    if (arg == "start") return PlayerLoopProfiler.Start();
                    if (arg.StartsWith("stop")) return PlayerLoopProfiler.Stop(arg.Length > 5 ? int.Parse(arg.Substring(5)) : 40);
                    return "{\"error\":\"loopprof start|stop [n]\"}";
                case "profile":
                    if (arg == "start") return ScriptProfiler.Start(null);
                    if (arg.StartsWith("start ")) return ScriptProfiler.Start(arg.Substring(6).Split(' '));
                    if (arg.StartsWith("stop")) return ScriptProfiler.Stop(arg.Length > 5 ? int.Parse(arg.Substring(5)) : 25);
                    return "{\"error\":\"ground x z profile start [Type.Method ...]|stop [n]\"}";
                case "call":
                    var ca = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    return "{\"value\":" + Reflect.Call(ca[0], ca.Skip(1).ToArray()) + "}";
                case "bg":
                    // Keep simulating/rendering when the window is unfocused (needed for unattended benchmarks)
                    if (arg.Length > 0) Application.runInBackground = arg == "on";
                    return "{\"runInBackground\":" + (Application.runInBackground ? "true" : "false") + ",\"focused\":" + (Application.isFocused ? "true" : "false") + "}";
                case "overlay": p.Overlay.Visible = arg != "off"; return "{\"ok\":true}";
                case "timescale": Time.timeScale = float.Parse(arg); return "{\"ok\":true}";
                case "banner":
                {
                    if (arg == "off") return Net.Banner.Show(0f, "");
                    int sp = arg.IndexOf(' ');
                    return Net.Banner.Show(float.Parse(arg.Substring(0, sp), System.Globalization.CultureInfo.InvariantCulture), arg.Substring(sp + 1));
                }
                case "carmove":
                {
                    // nearest car to the player: put it at x y z facing yaw, at rest (scenario staging)
                    var cm = arg.Split(' ');
                    var cic = System.Globalization.CultureInfo.InvariantCulture;
                    var car = Net.Entities.NearestCar();
                    if (car == null) return "{\"error\":\"no car\"}";
                    var crb = car.GetComponent<Rigidbody>();
                    var cpos = new Vector3(float.Parse(cm[0], cic), float.Parse(cm[1], cic), float.Parse(cm[2], cic));
                    float roll = cm.Length > 4 ? float.Parse(cm[4], cic) : 0f;   // e.g. 180 = upside down (crash tests)
                    car.transform.SetPositionAndRotation(cpos, Quaternion.Euler(0f, float.Parse(cm[3], cic), roll));
                    float cspeed = cm.Length > 5 ? float.Parse(cm[5], cic) : 0f;   // m/s along the car's forward (crash tests)
                    if (crb != null && !crb.isKinematic) { crb.velocity = car.transform.forward * cspeed; crb.angularVelocity = Vector3.zero; }
                    return "{\"ok\":true}";
                }
                case "teleportyaw":
                {
                    var ty = arg.Split(' ');
                    var tic = System.Globalization.CultureInfo.InvariantCulture;
                    if (mainscript.s == null || mainscript.s.player == null) return "{\"error\":\"not in game\"}";
                    mainscript.s.player.Teleport(new Vector3(float.Parse(ty[0], tic), float.Parse(ty[1], tic), float.Parse(ty[2], tic)), new Vector3(0f, float.Parse(ty[3], tic), 0f));
                    mainscript.s.player.FxRot = 0f;
                    return "{\"ok\":true}";
                }
                case "teleport":
                    var xyz = arg.Split(' ');
                    if (mainscript.s == null || mainscript.s.player == null) return "{\"error\":\"not in game\"}";
                    mainscript.s.player.Teleport(new Vector3(float.Parse(xyz[0]), float.Parse(xyz[1]), float.Parse(xyz[2])));
                    return "{\"ok\":true}";
                case "quality":
                    if (arg.Length > 0) QualitySettings.SetQualityLevel(int.Parse(arg), true);
                    return "{\"level\":" + QualitySettings.GetQualityLevel() + ",\"shadowDistance\":" + QualitySettings.shadowDistance + ",\"vSync\":" + QualitySettings.vSyncCount + ",\"targetFps\":" + Application.targetFrameRate + "}";
                case "help":
                    return "{\"commands\":\"alive ping stats scene breakdown items [filter] physics spawnpile clearpile loopprof start|stop [n] reset report <note> screenshot overlay on|off bg on|off ground x z profile start [Type.Method ...]|stop [n] get <Type.member.path> set <path> <value> call <path.Method> [args] timescale <f> teleport x y z quality [n]\"}";
                default: return "{\"error\":\"unknown command, try help\"}";
            }
        }
    }
}
