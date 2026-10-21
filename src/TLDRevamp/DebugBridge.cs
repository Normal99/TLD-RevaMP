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

        /// The port this bridge LISTENS on. A port of 0 means "any free port": the real one is the one
        /// the listener was given, and it is reported so a client can tell which copy it is talking to.
        /// Static because the bridge's command handling is static too: there is exactly one bridge per process.
        private static int _boundPort;

        /// The port this bridge listens on, as a client sees it. -1 before the listener started.
        public static int BoundPort => _boundPort;

        public void Start()
        {
            _running = true;
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            // the listener's real port (the one the OS gave us when 0 was asked for)
            try { _boundPort = ((IPEndPoint)_listener.LocalEndpoint).Port; } catch { _boundPort = _port; }
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
        private static float _tpUntil, _tpReadySince = -1; private static bool _tpWaiting;
        private static string _tpOn = ""; private static long _tpPlaced, _tpWaited;

        internal static RaycastHit? StandHit(float x, float z, float? belowY, bool vehicles = false) =>
            Net.TpaPlacement.StandHit(x, z, belowY, vehicles);
        private static bool Room(Vector3 p, Transform playerRoot) => Net.TpaPlacement.Room(p, playerRoot);

        /// Production use too (a joiner placed next to the host, Mp): stand on the ground at a global (x, z), held until
        /// it is generated there. TpBusy while held.
        internal static void PlaceAt(double gx, double gz) { TpGround(gx, gz, null); }
        /// Next to someone standing at global height `nearGy` (a building's floor, a roof, a bridge): the floor nearest
        /// below that height, else the ground. The lowest surface with room was the wrong floor: beside the host in the
        /// spawn house, its roof (no room on the kitchen floor at that spot), and under a raised floor the ground beneath
        /// it (playtest 2026-10-18: "tpa sending him under the floor").
        internal static void PlaceNear(double gx, double gz, double nearGy) { TpGround(gx, gz, null, nearGy); }
        // Give the target's floor a short generation window before the ordinary placement fallback.
        internal static void PlaceTpa(double gx, double gz, double bodyGy, bool retry = false)
        {
            TpGround(gx, gz, null, bodyGy + .5, true, retry);
        }
        private static bool _tpStrict;
        private static float _tpReadyAt;
        internal static bool TpaWorldReady(double gx, double gy, double gz)
        {
            var u = mainscript.UnityPosFromGlobal(new Vector3d(gx, gy, gz));
            if (Time.realtimeSinceStartup < _tpReadyAt || u.sqrMagnitude >= 6250000f) return false;
            foreach (var o in TpAround)
            {
                var hit = StandHit(u.x + o.x, u.z + o.y, null);
                if (hit.HasValue && Net.TpaArrival.WorldFloor(new Net.TpaArrival.Candidate(hit.Value.point.y, true, hit.Value.collider.attachedRigidbody == null))) return true;
            }
            return false;
        }
        internal static bool TpBusy => _tpWaiting;
        internal static void CancelJoinPlacement() { _tpWaiting = _tpHeld = false; _tpCalmUntil = 0; }
        private static double? _tpNearGy;
        /// The spot, then rings of 1, 2 and 3 m around it (8 directions each).
        private static readonly Vector2[] TpAround = Ring();
        private static Vector2[] Ring()
        {
            var l = new System.Collections.Generic.List<Vector2> { Vector2.zero };
            foreach (float r in new[] { 1f, 2f, 3f })
                for (int i = 0; i < 8; i++) l.Add(new Vector2(Mathf.Cos(i * Mathf.PI / 4f), Mathf.Sin(i * Mathf.PI / 4f)) * r);
            return l.ToArray();
        }

        private static string TpGround(double gx, double gz, double? gy, double? nearGy = null, bool strict = false, bool retry = false)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"not in game\"}";
            _tpGx = gx; _tpGz = gz; _tpGy = gy; _tpNearGy = nearGy;
            _tpStrict = strict;
            if (!retry) { _tpReadySince = -1; _tpReadyAt = Time.realtimeSinceStartup + .5f; }
            _tpWaiting = false;
            if ((!strict || retry) && TryPlace(pl)) return "{\"tpground\":true,\"on\":" + Json.Str(_tpOn) + "}";
            // not generated yet: hold the player at the spot (the world streams in around it) — no hop, no fall
            var u = mainscript.UnityPosFromGlobal(new Vector3d(gx, gy ?? nearGy ?? mainscript.GlobalFromUnityPos(pl.transform.position).y, gz));
            pl.Teleport(u);
            TpSettle(pl);
            _tpWaiting = true; _tpUntil = Time.realtimeSinceStartup + 30f; _tpWaited++;
            return "{\"tpground\":\"waiting\"}";
        }

        private static bool TryPlace(fpscontroller pl)
        {
            var u = mainscript.UnityPosFromGlobal(new Vector3d(_tpGx, _tpGy ?? 0.0, _tpGz));
            if (_tpStrict)
            {
                bool ready = TpaWorldReady(_tpGx, _tpNearGy.Value - .5, _tpGz);
                if (ready && _tpReadySince < 0) _tpReadySince = Time.realtimeSinceStartup;
                float bodyY = mainscript.UnityPosFromGlobal(new Vector3d(_tpGx, _tpNearGy.Value - .5, _tpGz)).y;
                // Prefer the target's floor across the full ring before accepting any lower floor.
                var hits = new System.Collections.Generic.List<RaycastHit?>();
                var floors = new System.Collections.Generic.List<Net.TpaArrival.Candidate>();
                foreach (var o in TpAround)
                {
                    var hit = Net.TpaPlacement.StandHit(u.x + o.x, u.z + o.y, bodyY - .4f, true, false);
                    hits.Add(hit); floors.Add(new Net.TpaArrival.Candidate(hit?.point.y ?? double.NaN, hit.HasValue && Room(hit.Value.point, pl.transform.root), hit.HasValue && hit.Value.collider.attachedRigidbody == null));
                }
                int chosen = Net.TpaArrival.ChooseFloor(floors, bodyY, ready, _tpReadySince < 0 ? 0 : Time.realtimeSinceStartup - _tpReadySince);
                if (chosen >= 0)
                {
                    var hit = hits[chosen].Value;
                    pl.godModeTillGrounded = true;
                    pl.Teleport(new Vector3(hit.point.x, hit.point.y + 1.1f, hit.point.z));
                    TpSettle(pl); _tpHeld = false;
                    _tpOn = hit.collider.name; _tpPlaced++;
                    return true;
                }
                if (chosen != -2) return false;
                _tpStrict = false;
            }
            RaycastHit? h = null;
            if (_tpNearGy.HasValue)
            {
                // the floor at their level: here, else the nearest spot around with room on it (inside a house the spot
                // beside them can be a wall or a cupboard — the first try at the spawn house found no room on the kitchen
                // floor there and put the asker on the roof); a floor lower down only if there's none at their level
                // near: their body + 0.5 m; their feet are ~0.9 m under their body. The floor they stand on first (within
                // half a metre of their feet — not the bed beside them), then anything up to a step above it.
                float near = mainscript.UnityPosFromGlobal(new Vector3d(_tpGx, _tpNearGy.Value, _tpGz)).y;
                foreach (var (below, lowest) in new[] { (near - 1.1f, near - 1.9f), (near, near - 2.5f) })
                {
                    foreach (var o in TpAround)
                    {
                        var hh = StandHit(u.x + o.x, u.z + o.y, below);
                        if (hh != null && hh.Value.point.y >= lowest) { h = hh; u.x += o.x; u.z += o.y; break; }
                    }
                    if (h != null) break;
                }
                if (h == null)
                    foreach (var o in TpAround)
                    {
                        var hh = StandHit(u.x + o.x, u.z + o.y, near);
                        if (hh != null) { h = hh; u.x += o.x; u.z += o.y; break; }
                    }
            }
            if (h == null)
                foreach (var o in TpAround)
                {
                    var hh = StandHit(u.x + o.x, u.z + o.y, _tpGy.HasValue ? u.y : (float?)null);
                    if (hh != null) { h = hh; u.x += o.x; u.z += o.y; break; }
                }
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

        private static string TpaLandingCheck()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            var p = pl.transform.position;
            var floor = Net.TpaPlacement.Landing(p, pl.transform.root, out bool room);
            return "{\"floor\":" + (floor.HasValue ? "true" : "false") + ",\"gap\":" +
                (floor.HasValue ? (p.y - floor.Value.point.y).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "null") +
                ",\"room\":" + (room ? "true" : "false") + ",\"waiting\":" + (TpBusy ? "true" : "false") + "}";
        }

        private static string TpaIndoor()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            foreach (var poi in Resources.FindObjectsOfTypeAll<poiScript>())
            {
                if (poi == null || !poi.gameObject.scene.IsValid() || !poi.gameObject.activeInHierarchy ||
                    (poi.transform.position - pl.transform.position).sqrMagnitude > 90000) continue;
                for (int x = -12; x <= 12; x++) for (int z = -12; z <= 12; z++)
                {
                    var from = poi.transform.position + new Vector3(x, 10, z);
                    foreach (var h in Physics.RaycastAll(from, Vector3.down, 20, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    {
                        if (h.normal.y < .65f || h.collider.attachedRigidbody != null || h.collider.GetComponentInParent<poiScript>() != poi || !Room(h.point, pl.transform.root)) continue;
                        var body = h.point + Vector3.up * 1.1f;
                        if (!Physics.Raycast(body, Vector3.up, out var ceiling, 5, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) || ceiling.collider.GetComponentInParent<poiScript>() != poi) continue;
                        foreach (var dir in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                        {
                            if (!Physics.Raycast(body, dir, out var wall, 1.7f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) ||
                                wall.distance < 1f || wall.distance > 1.5f || Mathf.Abs(wall.normal.y) > .3f || wall.collider.attachedRigidbody != null || wall.collider.GetComponentInParent<poiScript>() != poi) continue;
                            bool sideRoom = Room(h.point + dir * 1.25f, pl.transform.root);
                            if (sideRoom) continue;
                            float yaw = Mathf.Atan2(-dir.z, dir.x) * Mathf.Rad2Deg;
                            pl.Teleport(body, new Vector3(0, yaw, 0)); TpSettle(pl);
                            return "{\"indoor\":true,\"wallM\":" + wall.distance.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ",\"sideRoom\":false,\"wallRight\":" + Vector3.Dot(pl.BodyRot.right, dir).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "}";
                        }
                    }
                }
            }
            return "{\"missing\":\"no reachable loaded building floor with ceiling, room and adjacent wall within 300 m\"}";
        }

        private static bool _tpHeld;   // tpg … hold: the next tpground settles too
        private static float _tpCalmUntil;

        /// After a long test teleport: the floating-origin shift now (same frame), physics synced, the body still and the
        /// fall-damage baseline reset for half a second, so the arrival starts clean (no fall from the old height's speed).
        /// The 2–10 million m/s launches of farworld runs 6–8 were NOT this body: copies of items attached at the spawn
        /// building were held at pre-shift coordinates (Fixes.FreeAttachPoints) and their kinematic bodies swept 201 km per
        /// physics step, carrying the player along (tpprobe trace, v0.57.97).
        internal static void TpSettle(fpscontroller pl)
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
                bool placed = TryPlace(pl);
                if (placed || Time.realtimeSinceStartup > _tpUntil) { _tpWaiting = false; Net.Tpa.TelemetryGroundFinished(placed); return; }
                // held at the spot until the ground is there
                var hold = mainscript.UnityPosFromGlobal(new Vector3d(_tpGx, _tpGy ?? _tpNearGy ?? mainscript.GlobalFromUnityPos(pl.transform.position).y, _tpGz));
                if (!_tpGy.HasValue && !_tpNearGy.HasValue) hold.y = pl.transform.position.y;
                pl.transform.position = hold; if (pl.RB != null) pl.RB.position = hold;
                TpCalm(pl); pl.godModeTillGrounded = true;
                return;
            }
            if (_tpHeld && pl != null) { TpCalm(pl); pl.godModeTillGrounded = true; return; }
            if (_tpCalmUntil <= 0f) return;
            if (Time.realtimeSinceStartup < _tpCalmUntil) { TpCalm(pl); return; }
            _tpCalmUntil = 0f;
        }

        // The read-only commands: they change nothing, so a session that denies developer access still allows
        // them (a joiner must be able to ask what the policy is, and the tests read the state through them).
        private static readonly System.Collections.Generic.HashSet<string> _readCmds = new System.Collections.Generic.HashSet<string> { "get", "gpos", "ground", "tpalanding", "errors", "ping", "status", "roadpoint", "seatraw", "items", "telemetry", "genbacklog", "devaccess", "vr", "terrainhorizon", "terrainhorizonstats",
                                                                                "deathstats", "deathinfo", "ragdolls", "itemslist", "shared" };
        private static readonly System.Collections.Generic.HashSet<string> _readMp = new System.Collections.Generic.HashSet<string> { "entities", "status", "players", "carparts", "physical", "sitestate", "farrec", "caps", "ownerlog",
                                                                                "attachedparts", "pcs", "wheelpose", "physlocks", "ipdump", "attach", "anomalies", "recsize", "mods", "saverecords", "usables", "poiu", "poius" };
        private static bool ReadOnlyCmd(string cmd, string arg)
        {
            if (_readCmds.Contains(cmd)) return true;
            if (cmd == "mp" && (arg == "liveusables" || arg == "usableprobe")) return true;
            if (cmd == "mp") { int sp = arg.IndexOf(' '); return _readMp.Contains(sp < 0 ? arg : arg.Substring(0, sp)); }
            if (cmd == "mount") return arg.StartsWith("pose") || arg.StartsWith("slots");
            // `mp ragdoll <net>` reads a body's limbs; `mp ragdoll <net> kill|grab` changes the world, so only the
            // reading form is a read-only command.
            if (cmd == "mp" && arg.StartsWith("ragdoll ")) return arg.Split(' ').Length == 2;
            // The cinematic camera's status is a pure query and safe for any peer; every other cinema command
            // changes what this machine shows, so it is a mutation and follows the host's developer policy.
            // The rule itself is in the pure core (CinematicCommandPolicy), so it can be tested offline.
            if (cmd == "cinema") return Cinematic.CinematicCommandPolicy.IsReadOnly(arg);
            if (cmd == "replay") return Replay.ReplayCommandPolicy.IsReadOnly(arg);
            // `openxr` on its own reads the state, and so does `openxr state`: both are the SAME read, so they
            // must classify the same way. Only `openxr start|stop|recenter|retry` changes the session, so it is a
            // developer command and needs the session's developer access. (Before this, `openxr state` was
            // refused when developer access was denied while the identical bare `openxr` was allowed.)
            // The XR command's read/mutation split is the policy's (VrBridgeCommandPolicy), so it is the same
            // rule the bridge itself dispatches by: `openxr` and `openxr state` read the state, and
            // `openxr start|retry|stop|recenter` change the session and need the session's developer access.
            if (cmd == "openxr") return VR.VrBridgeCommandPolicy.IsQuery(arg);
            return false;
        }

        /// A reflection path that names the host's password (or the session's): the bridge never shows its value.
        /// Where a LOCAL evidence screenshot goes: a mod-owned folder that is NOT the shared report root, so a
        /// screenshot can never be swept into a shared report by accident.
        ///
        /// The separation is the point: the telemetry deliberately holds no screenshots (docs/TELEMETRY.md),
        /// and that rule is about what is SHARED. A local, explicit capture is a different thing and is what
        /// the camera, the terrain and the watermark evidence need.
        private static string LocalShotFolder()
        {
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "tldrevamp-evidence");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// Takes a local screenshot and returns its path. The file name is built from the TIME and a sanitised
        /// name: a name typed by a player would otherwise end up in a folder listing.
        ///
        /// This is a LOCAL capture only. Nothing here is attached to telemetry or to a shared report.
        private static string CaptureLocalShot(string name)
        {
            try
            {
                string folder = LocalShotFolder();
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                // the name is sanitised: only letters, digits, dash and underscore survive
                var clean = new StringBuilder();
                foreach (char ch in (name ?? "").Trim())
                {
                    if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') clean.Append(ch);
                    if (clean.Length >= 32) break;
                }
                string file = clean.Length > 0 ? "shot-" + stamp + "-" + clean + ".png" : "shot-" + stamp + ".png";
                string path = Path.Combine(folder, file);
                UnityEngine.ScreenCapture.CaptureScreenshot(path);
                return "{\"path\":" + Json.Str(path) + ",\"name\":" + Json.Str(file) + ",\"shared\":false}";
            }
            catch (Exception e)
            {
                return "{\"error\":" + Json.Str("the screenshot could not be saved: " + e.Message) + "}";
            }
        }

        /// A reflection path that names the host's password (or the session's): the bridge never shows its value.
        private static bool SecretPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var p = path.ToLowerInvariant();
            return p.EndsWith("password") || p.Contains("hostpassword") || p.EndsWith(".password");
        }

        internal static string Execute(string line)
        {
            var parts = line.Split(new[] { ' ' }, 2);
            string cmd = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1] : "";
            // The host's own session control (the developer policy) is not a developer command: the host must be
            // able to change its own policy even when the policy denies developer access (the test restores it).
            // ONLY for the host: the exemption used to apply on every machine, so a joiner got a DIFFERENT
            // answer for `mp policy` ("not hosting") than for any other denied command (the policy error a
            // client uses to know the host denied it). The host check is now inside the exemption.
            if (!Net.SessionSettingsPolicy.BridgeAllowed(Net.Mp.Server != null, Net.DeveloperAccess.Allowed, ReadOnlyCmd(cmd, arg), cmd, arg))
            {
                Net.DeveloperAccess.DenyBridge();
                return Net.SessionSettingsPolicy.PolicyDenied;
            }
            // what a dev command changed is noted on the anomaly watch's lines (Entities.Anomaly); queries are not.
            // A command that carries the host's password never keeps its value: the anomaly watch and every report
            // that quotes it would carry the secret.
            if (!ReadOnlyCmd(cmd, arg))
            {
                var safe = Net.SessionSettingsRules.RedactCommand(line, Net.Mp.Server?.Password);
                Net.Entities.LastDevCmd = safe.Length > 80 ? safe.Substring(0, 80) : safe;
                Net.Entities.LastDevAt = Time.realtimeSinceStartup;
            }
            var p = Plugin.Instance;

            switch (cmd)
            {
                case "vr": return Fixes.VrContractBridge.Json();
                case "cinema": return Cinematic.CinematicBridge.Command(arg);
                case "replay": return Replay.ReplayBridge.Command(arg);
                // The XR session (issue #41). The tool tools/quest3vr.py sends exactly this command, so without
                // the case the tool could never start a session, sample it or recenter: the bridge answered
                // "unknown command" and the tool stopped at its first call. "openxr" on its own is a pure read
                // (the state), so it needs no developer access; "start"/"stop"/"recenter" change the session and
                // therefore go through the developer-access policy like every other mutating command.
                // The XR session, through the bridge: `openxr` reads the state, `openxr start|retry|stop|recenter`
                // acts on it. The start is explicit (nothing starts a session behind the player's back) and a
                // failed start is retried only when it is asked for.
                case "openxr": return Fixes.OpenXrBridge.Command(arg);
                // The horizon diagnostics (issue #37). The tool tools/terrainhorizon.py sends exactly this command,
                // so without the case the tool could never produce any evidence: the bridge answered "unknown
                // command" and the tool stopped. It is a pure read, so it needs no developer access.
                case "terrainhorizon": return Fixes.TerrainHorizonLab.Snapshot(arg);
                // This fix's own counters: what it drew, what it cost and how often it ran.
                case "terrainhorizonstats": return Fixes.TerrainHorizonLab.Stats();
                case "ping": return "{\"ok\":true,\"version\":\"" + Plugin.Version + "\",\"build\":\"" + Plugin.BuildHash + "\",\"commit\":\"" + Plugin.BuildCommit + "\",\"dirty\":" + (Plugin.BuildDirty ? "true" : "false") + ",\"game\":\"" + Plugin.GameBuild + "\",\"port\":" + _boundPort + ",\"dataPath\":" + Json.Str(Application.dataPath) + ",\"patchFailures\":" + Plugin.PatchFailures + ",\"failedPatches\":" + Json.Str(string.Join(",", Plugin.FailedPatches)) + "}";
                case "stats": return p.Telemetry.FrameStatsJson();
                case "scene": return p.Telemetry.SceneStatsJson();
                case "breakdown": return p.Telemetry.Breakdown.Json();
                case "telemetry": return SessionLog.Stats();
                case "counters": return CounterProbe.Run(arg);
                case "timers": return ScriptProfiler.Bridge(arg);
                case "il": return ScriptProfiler.Il(arg.Trim());
                case "telemetrytest": return SessionLog.SelfTest(arg.Length > 0 ? int.Parse(arg) : 20000);
                case "scenecount": return SceneCounts.Scan(SessionLog.St);
                case "mark": return Net.TelemetryMarks.Local(arg);
                case "markpanel": return MarkPanel.Bridge(arg);
                case "markrename": return MarkPanel.Bridge(arg, true);
                case "repro": return ReproPlayback.Command(arg);
                case "blackbox": return SessionLog.Blackbox();
                case "trace": return Net.Entities.TelemetryTrace();
                case "netcat": return Net.NetMeter.CategoryJson();
                case "items": return PhysicsLab.Items(arg);
                case "physics": return PhysicsLab.Stats();
                case "followdump": return PhysicsLab.FollowDump(arg.Length > 0 ? int.Parse(arg) : 6);
                case "render": return PhysicsLab.RenderStats();
                case "cameras": return PhysicsLab.Cameras();
                case "renderinv": return RenderLab.Inventory();
                case "skinned": return RenderLab.Skinned();
                case "rtoggle": { var tv = arg.Split(' '); return RenderLab.Toggle(tv[0], tv.Length > 1 ? tv[1] : ""); }
                case "mirrors": return RenderLab.Mirrors();
                case "particles": return RenderLab.Particles();
                case "rebaseprof": return arg == "stop" ? RebaseLab.Stop() : RebaseLab.Start();
                case "pourtest": return RebaseLab.PourTest(arg);
                case "uievents": return UiLab.Events(arg);
                case "uiclick": return UiLab.Click(arg);
                case "uihover": return UiLab.Hover(arg);
                case "revampscroll":
                {
                    // test: scroll the open Revamp tab (1 top, 0 bottom) — screenshots of its lower sections; "info V" scrolls
                    // the explanation panel instead
                    bool infoPanel = arg.StartsWith("info ", StringComparison.Ordinal);
                    if (infoPanel) arg = arg.Substring(5);
                    foreach (var sr in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.ScrollRect>())
                    {
                        var srPath = UiLab.PathOf(sr.transform);
                        if (srPath.Contains("TabRevamp") && srPath.Contains("RevampInfo") == infoPanel)
                        { sr.verticalNormalizedPosition = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture); return "{\"ok\":true}"; }
                    }
                    return "{\"error\":\"Revamp tab not open\"}";
                }
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
                case "mods": return string.IsNullOrWhiteSpace(arg) ? ModHost.Status() : Net.ModBrowser.Bridge(arg);
                case "traffic": return AutopilotBridge.Status();
                case "seatraw": return Net.Entities.SeatRaw();
                case "poisaved":
                {
                    // test: every building prefab's saveable objects (tosaveitemscript) — the lease's "taken" path turns those
                    // off on the machines that didn't spawn the building: one at the root, or carrying most of the
                    // building's meshes, would take the building with it (the missing diner, playtest 2026-10-09/10)
                    var rows = new System.Collections.Generic.List<string>();
                    var pois = itemdatabase.s.pois;
                    for (int k = 0; k < pois.Length; k++)
                    {
                        var pf = pois[k]; if (pf == null) continue;
                        int all = pf.GetComponentsInChildren<Renderer>(true).Length;
                        var big = new System.Collections.Generic.List<string>();
                        int saved = 0;
                        foreach (var ts in pf.GetComponentsInChildren<tosaveitemscript>(true))
                        {
                            saved++;
                            int r = ts.GetComponentsInChildren<Renderer>(true).Length;
                            bool root = ts.transform == pf.transform;
                            if (root || r * 5 >= all && r >= 10)
                                big.Add("[" + Json.Str(ts.name) + "," + r + "," + (root ? "true" : "false") + "," + (ts.GetComponent<pickupable>() != null ? "true" : "false") + "]");
                        }
                        rows.Add("[" + k + "," + Json.Str(pf.name) + "," + all + "," + saved + ",[" + string.Join(",", big) + "]]");
                    }
                    return "{\"pois\":[" + string.Join(",", rows) + "]}";
                }
                case "aimlab":
                {
                    var al = arg.Trim().Split(' ');
                    return al[0] == "start" ? Diagnostics.AimLab.Start(uint.Parse(al[1])) : al[0] == "stop" ? Diagnostics.AimLab.Stop() : Diagnostics.AimLab.Status();
                }
                case "guardlab":
                    // test: Fixes.WornDestroyed / FluidTypeGuard (tools/guards.py)
                    return arg.Trim() == "fluid" ? Fixes.FluidTypeGuard.Test() : arg.Trim() == "worn" ? Fixes.WornDestroyed.Test() : Fixes.WornDestroyed.Status();
                case "prefabswith":
                {
                    // test: item ids whose prefab carries a game script of that type (e.g. sprayscript)
                    var ty = typeof(mainscript).Assembly.GetType(arg.Trim());
                    if (ty == null) return "{\"error\":\"no type " + arg + "\"}";
                    var rows = new System.Collections.Generic.List<string>();
                    for (int k = 0; k < itemdatabase.s.items.Length; k++)
                        if (itemdatabase.s.items[k] != null && itemdatabase.s.items[k].GetComponentInChildren(ty, true) != null) rows.Add("[" + k + "," + Json.Str(itemdatabase.s.items[k].name) + "]");
                    return "{\"items\":[" + string.Join(",", rows) + "]}";
                }
                case "rabbits": return arg.Trim() == "spawn" ? Fixes.Rabbits.TestSpawn() : Fixes.Rabbits.Stats();
                case "devspawn":
                {
                    // the dev menu's own spawn (kaposztaleves.Spawn → mainscript.Spawn(g, color, worn, rtype, paint))
                    int idx;   // an item id, or a prefab name (e.g. Car09full_slide)
                    if (!int.TryParse(arg, out idx)) { idx = -1; for (int k = 0; k < itemdatabase.s.items.Length; k++) if (itemdatabase.s.items[k] != null && itemdatabase.s.items[k].name == arg) { idx = k; break; } }
                    if (idx < 0) return "{\"error\":" + Json.Str("no item named " + arg) + "}";
                    var g = itemdatabase.s.items[idx];
                    if (g == null || kaposztaleves.s == null) return "{\"error\":\"no item " + idx + "\"}";
                    kaposztaleves.s.Spawn(g, Color.white);
                    return "{\"spawned\":" + Json.Str(g.name) + "}";
                }
                case "itemids":
                {
                    // `itemids <part of a prefab name>`: the item ids whose prefab name contains it (devspawn / spawncarg take the id)
                    var hits = new System.Collections.Generic.List<string>();
                    for (int k = 0; k < itemdatabase.s.items.Length; k++)
                        if (itemdatabase.s.items[k] != null && itemdatabase.s.items[k].name.IndexOf(arg.Trim(), System.StringComparison.OrdinalIgnoreCase) >= 0)
                            hits.Add("[" + k + "," + Json.Str(itemdatabase.s.items[k].name) + "]");
                    return "{\"items\":[" + string.Join(",", hits) + "]}";
                }
                case "seatlook": return Net.Entities.SeatLook();
                case "seats": return Net.Entities.SeatList(uint.Parse(arg.Trim()));
                case "sitseat":
                {
                    var sp = arg.Split(' ');
                    // sitseat <idx> [redirect] [net=<id>]: a seat of the nearest car, or of shared vehicle <id>
                    uint sn = 0; foreach (var w in sp) if (w.StartsWith("net=")) sn = uint.Parse(w.Substring(4));
                    return Net.Entities.SitSeat(int.Parse(sp[0]), System.Array.IndexOf(sp, "redirect") > 0, sn);
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
                    if (ia[0] == "groupdiag") return Net.ItemSnapshot.GroupDiag(ia.Length > 1 ? float.Parse(ia[1], System.Globalization.CultureInfo.InvariantCulture) : 15f);
                    return "{\"error\":\"itemsnap test [n]\"}";
                }
                case "devaccess": return Net.DeveloperAccess.Status() + ",\"patchFailures\":" + Plugin.PatchFailures + ",\"failedPatches\":" + Json.Str(string.Join(",", Plugin.FailedPatches)) + "}";
                case "mp":
                {
                    var ma = arg.Split(' ');
                    var mic = System.Globalization.CultureInfo.InvariantCulture;
                    switch (ma[0])
                    {
                        case "host": return Net.Mp.Host(ma.Length > 1 ? ushort.Parse(ma[1]) : (ushort)27070, ma.Length > 2 ? ma[2] : "");
                        case "join": return Net.Mp.Join(ma[1], ma.Length > 2 ? ma[2] : "");
                        case "joinid": return Net.Mp.JoinFriend(ulong.Parse(ma[1]), ma.Length > 2 ? ma[2] : "");
                        case "session": return Net.Session.Status();
                        case "hands": return Net.RemotePlayers.HandsJson();
                        case "loading":
                            if (ma.Length > 1 && ma[1] == "preview") return Net.JoinLoading.PreviewFromBridge(ma);
                            return Net.JoinLoading.Probe(ma.Length > 1 ? ma[1] : "");
                        case "kick": return "{\"kicked\":" + (Net.Mp.Server != null && Net.Mp.Server.Kick(int.Parse(ma[1])) ? "true" : "false") + "}";
                        case "chat":
                            if (ma.Length > 1 && ma[1] == "say") return "{\"said\":" + (Net.Session.Say(arg.Substring(arg.IndexOf("say ", StringComparison.Ordinal) + 4)) ? "true" : "false") + "}";
                            if (ma.Length > 1 && ma[1] == "open") Net.Chat.Open();
                            if (ma.Length > 1 && ma[1] == "close") Net.Chat.Close();
                            return Net.Chat.Status();
                        case "voice":
                            if (ma.Length > 1 && ma[1] == "tone") return Net.Voice.Tone(float.Parse(ma[2], mic));
                            if (ma.Length > 1 && ma[1] == "feed") return Net.Voice.Tone(float.Parse(ma[3], mic), int.Parse(ma[2]));
                            if (ma.Length > 1 && ma[1] == "probe") return Net.Voice.Probe();
                            if (ma.Length > 1 && ma[1] == "mode") Net.Voice.Setting = (Net.Voice.Mode)Enum.Parse(typeof(Net.Voice.Mode), ma[2], true);
                            if (ma.Length > 1 && ma[1] == "mute") { if (!Net.Voice.Muted.Remove(ulong.Parse(ma[2]))) Net.Voice.Muted.Add(ulong.Parse(ma[2])); }
                            return Net.Voice.Status();
                        case "screen": return Net.MpScreen.Test(ma.Length > 1 ? arg.Substring(arg.IndexOf("screen", StringComparison.Ordinal) + 6).Trim() : "");
                        case "botcar": return Net.Mp.CaptureBotCar();
                        case "recsize": return Net.Entities.RecordSize();
                        case "carset": return Net.Entities.CarSet(uint.Parse(ma[1]), ma[2], int.Parse(ma[3]));
                        case "botattach": return Net.Mp.BotAttach(uint.Parse(ma[1]), ma[2]);
                        case "botitem": return Net.Mp.AddItemBot(ma.Skip(1).ToArray());   // ox oy oz [lag] [held]: a bot's item in the nearest car
                        case "acts": return Net.PlayerActs.Probe(ma.Length > 1 ? ma[1] : "");
                        case "copies": return Net.Entities.CopiesToggle(ma[1], ma.Length > 2 && ma[2] == "on");
                        case "bots": return Net.Mp.AddBots(int.Parse(ma[1]), ma.Length > 2 ? float.Parse(ma[2], mic) : 30f, ma.Length > 3 ? float.Parse(ma[3], mic) : 5f);
                        case "remotebots": return Net.Mp.AddRemoteBots(int.Parse(ma[1]), ulong.Parse(ma[2]), ma.Length > 3 ? float.Parse(ma[3], mic) : 30f, ma.Length > 4 ? float.Parse(ma[4], mic) : 5f, ma.Length > 5 ? float.Parse(ma[5], mic) : 0f);
                        case "dropbots": return Net.Mp.DropBots(int.Parse(ma[1]));
                        case "status": return Net.Mp.Status();
                        case "save": return Net.Mp.SaveTail();
                        case "players": return Net.RemotePlayers.List();
                        case "share": return Net.Entities.ShareCar(Net.Entities.NearestCar());
                        case "entities": return Net.Entities.Status();
                        case "mods": return Net.ModObjects.StatusJson();
                        case "plausibility": return Net.Plausibility.Stats();
                        case "ask": return Net.MpScreen.AskProbe(ma.Length > 1 ? ma[1] : "");
                        case "modtransfer": return Net.ModTransfer.Stats();
                        case "entstats": return Net.Entities.ResetStats();
                        case "jumps": return Net.Entities.JumpLog();
                        case "entdebug": return Net.Entities.Debug();
                        case "worldcar": return Net.Entities.WorldCar();
                        case "pickupnear": return Net.Entities.PickupNear(ma.Length > 1 ? float.Parse(ma[1], System.Globalization.CultureInfo.InvariantCulture) : 4f);
                        case "drop": return Net.Entities.DropHeld();
                        case "throwpoint": return Net.Entities.ThrowAtPoint(double.Parse(ma[1], System.Globalization.CultureInfo.InvariantCulture), double.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture), float.Parse(ma[3], System.Globalization.CultureInfo.InvariantCulture));
                        case "throwat": return Net.Entities.ThrowAt(uint.Parse(ma[1]), float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture));
                        case "sitpassenger": return Net.Entities.SitPassenger(uint.Parse(ma[1]));
                        case "carbreak": return Net.Entities.CarBreak(uint.Parse(ma[1]));
                        case "interp": return Net.Entities.InterpDump(uint.Parse(ma[1]));
                        case "sprayhold": return Net.Entities.SprayHold(float.Parse(ma[1], System.Globalization.CultureInfo.InvariantCulture));
                        case "sprayfx": return Net.Entities.SprayFxState(uint.Parse(ma[1]));
                        case "lookat": return Net.Entities.LookAt(uint.Parse(ma[1]));
                        case "turn": return Net.Entities.TurnItem(uint.Parse(ma[1]));
                        case "lamp": return Net.Entities.LampState(uint.Parse(ma[1]));
                        case "cardoors": return Net.Entities.CarDoors(uint.Parse(ma[1]));
                        case "busdoors": return Net.Entities.BusDoors(uint.Parse(ma[1]), ma.Length > 2 ? int.Parse(ma[2]) : -1, ma.Length > 3 ? int.Parse(ma[3]) : -1);
                        case "doorglue": return Net.Entities.GlueToDoor(uint.Parse(ma[1]), uint.Parse(ma[2]), int.Parse(ma[3]), int.Parse(ma[4]));
                        case "doorattachment": return Net.Entities.DoorAttachmentPose(uint.Parse(ma[1]));
                        case "opendoor": return Net.Entities.OpenDoor(uint.Parse(ma[1]), int.Parse(ma[2]));
                        case "headh": return Net.RemotePlayers.HeadHeights();
                        case "stimuli": return Net.RemotePlayers.Stimuli();
                        case "rabbit": return Net.Entities.Rabbit(uint.Parse(ma[1]));
                        case "rabbits": return Net.Entities.RabbitList();
                        case "rabbitdo": return Net.Entities.RabbitDo(ma);
                        case "body": return Net.Entities.BodyState(uint.Parse(ma[1]), ma);
                        case "copymotion": return Net.Entities.CopyMotion(uint.Parse(ma[1]), ma.Length > 2 && ma[2] == "reset");
                        case "stall": Net.MpClient.StallUntil = UnityEngine.Time.realtimeSinceStartup + float.Parse(ma[1], System.Globalization.CultureInfo.InvariantCulture); return "{\"stall\":" + ma[1] + ",\"held\":" + Net.MpClient.StalledMessages + "}";
                        case "fault": Net.Mp.FaultPart = int.Parse(ma[1]); Net.Mp.FaultUntil = UnityEngine.Time.realtimeSinceStartup + float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture); return "{\"fault\":" + ma[1] + ",\"faults\":" + Net.Mp.TickFaults + "}";
                        case "lag": Net.MpClient.LagS = float.Parse(ma[1], System.Globalization.CultureInfo.InvariantCulture) / 1000f; return "{\"lagMs\":" + ma[1] + "}";
                        case "lookplayer": return Net.RemotePlayers.LookAtPlayer(int.Parse(ma[1]));
                        case "bite": return Net.Entities.Bite();
                        case "food": return Net.Entities.Food(uint.Parse(ma[1]));
                        case "putincar": return Net.Entities.PutInCar(uint.Parse(ma[1]), uint.Parse(ma[2]), int.Parse(ma[3]));
                        case "relpos": return Net.Entities.RelPos(uint.Parse(ma[1]), ma.Skip(2).Select(uint.Parse).ToArray());
                        case "standon": return Net.Entities.StandOn(uint.Parse(ma[1]), ma.Length > 2 ? float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture) : 0.15f, ma.Length > 3 && ma[3] == "rear");
                        case "relplayers": return Net.Entities.RelPlayers(uint.Parse(ma[1]));
                        case "footprobe": return Net.Entities.FootProbe();
                        case "outsidebodies": return Net.Entities.OutsideBodies(uint.Parse(ma[1]));
                        case "tow": return Net.Entities.Tow(ma);
                        case "crashlease": return Net.Entities.TestCrashLease(uint.Parse(ma[1]));
                        case "paint":
                        {
                            var ci = System.Globalization.CultureInfo.InvariantCulture;
                            UnityEngine.Color? pc = ma.Length >= 5 ? new UnityEngine.Color(float.Parse(ma[2], ci), float.Parse(ma[3], ci), float.Parse(ma[4], ci)) : (UnityEngine.Color?)null;
                            return Net.Entities.PaintLab(uint.Parse(ma[1]), pc);
                        }
                        case "fluids": return Net.Entities.Fluids(uint.Parse(ma[1]));
                        case "tankexplode": return Net.Entities.TankExplode(uint.Parse(ma[1]), int.Parse(ma[2]), ma.Length > 3 ? float.Parse(ma[3], System.Globalization.CultureInfo.InvariantCulture) : 1000f);
                        case "holdnet": return Net.FillLab.HoldNet(uint.Parse(ma[1]));
                        case "walkat": return Net.FillLab.WalkAt(uint.Parse(ma[1]), float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture));
                        case "holdfill": return ma[1] == "stop" ? Net.FillLab.Stop() : ma[1] == "status" ? Net.FillLab.Status() : Net.FillLab.Start(uint.Parse(ma[1]), uint.Parse(ma[2]), int.Parse(ma[3]));
                        case "fill": return Net.Entities.Fill(uint.Parse(ma[1]), int.Parse(ma[2]), ma[3], float.Parse(ma[4], System.Globalization.CultureInfo.InvariantCulture));
                        case "pour": return ma[1] == "stop" ? Net.Entities.Pour(0, 0, 0, false) : ma[1] == "status" ? Net.Entities.PourStatus() : Net.Entities.Pour(uint.Parse(ma[1]), uint.Parse(ma[2]), int.Parse(ma[3]), true);
                        case "sitdriver": return Net.Entities.SitDriver(uint.Parse(ma[1]));
                        case "seats": return Net.Entities.SeatDump(uint.Parse(ma[1]));
                        case "lockdiag": return Net.Entities.LockDiag(uint.Parse(ma[1]));
                        case "takeout": { var cic8 = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.TakeOut(uint.Parse(ma[1]), float.Parse(ma[2], cic8), float.Parse(ma[3], cic8)); }
                        case "lights": return Net.Entities.Lights(uint.Parse(ma[1]));
                        case "switch": return Net.Entities.TurnSwitch(uint.Parse(ma[1]));
                        case "moveitem": { var cic5 = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.MoveItem(uint.Parse(ma[1]), float.Parse(ma[2], cic5), float.Parse(ma[3], cic5), ma.Length > 4 && ma[4] == "ground"); }
                        case "saverecords": return Net.Entities.SaveRecords(ma.Length > 1 ? ma[1] : "");
                        case "farstray": return Net.Entities.FarStray(uint.Parse(ma[1]), double.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture));
                        case "farstore": { var cic7 = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.FarStore(double.Parse(ma[1], cic7), double.Parse(ma[2], cic7), double.Parse(ma[3], cic7)); }
                        case "sitestate": { var cic6 = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.SiteState(double.Parse(ma[1], cic6), double.Parse(ma[2], cic6), double.Parse(ma[3], cic6)); }
                        case "itemslist": { var cic4 = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.ItemsList(double.Parse(ma[1], cic4), double.Parse(ma[2], cic4), double.Parse(ma[3], cic4)); }
                        case "itemsnear": { var cic3 = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.ItemsNear(double.Parse(ma[1], cic3), double.Parse(ma[2], cic3), double.Parse(ma[3], cic3)); }
                        case "carsnear": { var cic2 = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.CarsNear(double.Parse(ma[1], cic2), double.Parse(ma[2], cic2), double.Parse(ma[3], cic2)); }
                        case "detachpart": return Net.Entities.DetachPart(int.Parse(ma[1]));
                        case "telemetrydesync": return Net.Entities.TelemetryDesync(uint.Parse(ma[1]), int.Parse(ma[2]));
                        case "pickupnet": return Net.Entities.PickupNet(uint.Parse(ma[1]), ma.Length > 2 && ma[2] == "here");
                        case "itemcheck": return Net.Entities.ItemCheck(uint.Parse(ma[1]), ma.Length > 2 ? ma[2] : "");
                        case "wear": return Net.Entities.WearNet(uint.Parse(ma[1]));
                        case "takeoff": return Net.Entities.TakeOffNet(uint.Parse(ma[1]), ma.Length > 2 && ma[2] == "drop");
                        case "worn": return Net.Entities.WornStatus();
                        case "wearables": return Net.Entities.Wearables();
                        case "wearnear": return Net.Entities.WearNear(ma.Length > 1 ? ma[1] : "");
                        case "physlocks": { var ci = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.PhysLocks(double.Parse(ma[1], ci), double.Parse(ma[2], ci), ma.Length > 3 ? double.Parse(ma[3], ci) : 60); }
                        case "sleepall": { var ci = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.SleepAll(double.Parse(ma[1], ci), double.Parse(ma[2], ci), ma.Length > 3 ? double.Parse(ma[3], ci) : 60); }
                        case "awake": { var ci = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.Awake(double.Parse(ma[1], ci), double.Parse(ma[2], ci), ma.Length > 3 ? double.Parse(ma[3], ci) : 60); }
                        case "radio": return ma.Length > 2 && ma[1] == "advance" ? Net.Entities.RadioAdvance(int.Parse(ma[2])) : Net.Entities.RadioStatus();
                        case "physlock": { var ci = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.PhysLockStatus(double.Parse(ma[1], ci), double.Parse(ma[2], ci), float.Parse(ma[3], ci)); }
                        case "deathfling":
                        {
                            var ci = System.Globalization.CultureInfo.InvariantCulture;
                            if (ma.Length < 4) return Net.SessionRules.DeathStats();
                            var prefabs = new System.Collections.Generic.List<int>();
                            // The arguments are validated: a malformed one is an error the caller can read, not a
                            // throw from inside the bridge (the bridge has no try/catch around the command switch).
                            float vx, vy, vz;
                            if (!float.TryParse(ma[1], System.Globalization.NumberStyles.Float, ci, out vx) ||
                                !float.TryParse(ma[2], System.Globalization.NumberStyles.Float, ci, out vy) ||
                                !float.TryParse(ma[3], System.Globalization.NumberStyles.Float, ci, out vz))
                                return "{\"error\":\"deathfling vx vy vz [prefab id…]\"}";
                            for (int i = 4; i < ma.Length; i++)
                                foreach (var raw in ma[i].Split(','))
                                {
                                    if (raw.Length == 0) continue;
                                    int id;
                                    if (!int.TryParse(raw, out id)) return "{\"error\":\"not an item id: " + Json.Str(raw) + "\"}";
                                    prefabs.Add(id);
                                }
                            return Net.Entities.DeathFling(vx, vy, vz, prefabs.ToArray());
                        }
                        case "deathstats": return Net.SessionRules.DeathStats();
                        // The game's own Death, called again for a death that already happened: the duplicate path.
                        case "deathagain": return Net.Entities.DeathAgain();
                        case "deathinfo": return Net.Entities.DeathInfo();
                        case "shared": return Net.Entities.SharedList(ma.Length > 1 ? ma[1] : "");
                        case "ragdolls": return Net.Entities.RagdollPrefabs();
                        case "grips": return Net.Entities.GripPrefabs();
                        case "storms": return Net.Entities.StormTest(ma);
                        case "sounds": return Net.Entities.PlayerSoundsTest(ma);
                        case "tumble": return Net.Entities.TumbleTest(ma);
                        case "grip": return ma.Length > 1 ? Net.Entities.GripTest(ma) : Net.Entities.GripStatus();
                        case "ragdoll": return ma.Length > 2 ? Net.Entities.RagdollTest(uint.Parse(ma[1]), ma[2]) : Net.Entities.RagdollStatus(uint.Parse(ma[1]));
                        case "carsig": return Net.Entities.CarSignals(uint.Parse(ma[1]));
                        case "blood": return Net.Entities.BloodTest(uint.Parse(ma[1]), ma.Length > 2 ? ma[2] : "");
                        case "key": return Net.Entities.KeyTest(uint.Parse(ma[1]), ma.Length > 2 ? float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture) : 0f);
                        case "radiocar": return Net.Entities.RadioCar(uint.Parse(ma[1]));
                        case "seatguard": return Net.Entities.SeatGuardStats();
                        case "push": return Net.Entities.TestPush(uint.Parse(ma[1]), ma.Length > 2 ? float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture) : 3f);
                        case "itement": { var ci = System.Globalization.CultureInfo.InvariantCulture; return Net.Entities.ItemEnt(double.Parse(ma[1], ci), double.Parse(ma[2], ci), double.Parse(ma[3], ci)); }
                        case "partposes": return Net.Entities.PartPoses(uint.Parse(ma[1]));
                        case "wrench": return Net.Entities.Wrench(uint.Parse(ma[1]), int.Parse(ma[2]));
                        case "xdelete": return Net.Entities.XDelete(uint.Parse(ma[1]), ma.Length > 2 ? int.Parse(ma[2]) : -1);
                        case "falloff": return Net.Entities.FallOff(uint.Parse(ma[1]), int.Parse(ma[2]));
                        case "attach": return Net.Entities.AttachStats();
                        case "glue": return ma.Length > 2 ? Net.Entities.GlueTo(uint.Parse(ma[1]), uint.Parse(ma[2]), ma.Length > 3 ? float.Parse(ma[3], System.Globalization.CultureInfo.InvariantCulture) : 0f, ma.Length > 4 && ma[4] == "keep") : Net.Entities.Glueables();
                        case "attachpose": return Net.Entities.AttachPoseOf(uint.Parse(ma[1]));
                        case "crashstop": return Net.Entities.CrashStop(uint.Parse(ma[1]));
                        case "partin": return Net.Entities.PartIn(uint.Parse(ma[1]), uint.Parse(ma[2]));
                        case "attachto": return Net.Entities.AttachTo(uint.Parse(ma[1]), uint.Parse(ma[2]), int.Parse(ma[3]));
                        case "lookpick": return Net.Entities.LookPick(uint.Parse(ma[1]), ma.Length > 2 && ma[2] == "pick");
                        case "attachedparts": return Net.Entities.AttachedParts(uint.Parse(ma[1]));
                        case "carparts": return Net.Entities.CarParts(uint.Parse(ma[1]));
                        case "caps": return Net.Entities.Caps(uint.Parse(ma[1]));
                        case "sent": return Net.Entities.ServerEntity(uint.Parse(ma[1]));
                        case "forceheld": return Net.Entities.SetForceHeld(uint.Parse(ma[1]));
                        case "claim": return Net.Entities.ClaimTest(uint.Parse(ma[1]));
                        case "anomalies": return Net.Entities.AnomalyJson();
                        case "copystore": return Net.Entities.TestCopyStore(uint.Parse(ma[1]), uint.Parse(ma[2]));
                        case "displace": return Net.Entities.TestDisplace(uint.Parse(ma[1]), float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture));
                        case "copypartgone": return Net.Entities.TestCopyPartGone(uint.Parse(ma[1]), ma[2]);
                        case "usecap": return Net.Entities.UseCap(uint.Parse(ma[1]), int.Parse(ma[2]));
                        case "pcs": return Net.Entities.PartConds(uint.Parse(ma[1]));
                        case "wheelpose": return Net.Entities.WheelPose(uint.Parse(ma[1]));
                        case "farrec": return Net.Entities.FarRec(uint.Parse(ma[1]));
                        case "breakent": return Net.Entities.BreakEnt(uint.Parse(ma[1]), ma.Length > 2 ? float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture) : 1e6f);
                        case "shotfx": return Net.Entities.ShotFxStats();
                        case "blasts": return Net.Entities.ExplosionStats();
                        case "membergone": return Net.Entities.MemberGoneStats();
                        case "sharequota": return Net.Entities.ShareQuotaStats();
                        // `mp reports` stats; `mp report <note>`: what the report key does (asks / sends as a player would)
                        case "sharing":
                        case "reports": return Net.Reports.Stats();
                        case "report": { string rn = arg.Length > 6 ? arg.Substring(7) : ""; Net.Reports.LocalReport(rn.Length == 0 ? "bridge" : rn); return Net.Reports.Stats(); }
                        case "ai": return Net.AiProbe.List();
                        case "aiids": return Net.AiProbe.Ids();
                        case "aihead": return Net.AiProbe.Heads();
                        case "aicols": return Net.AiProbe.Cols(ma.Length > 1 ? ma[1] : "");
                        case "aivision": return Net.AiProbe.Vision();
                        case "aistats": return Net.Entities.AiStats();
                        case "aisee": return Net.Entities.AiSee();
                        case "ownerlog": return Net.Entities.OwnerLogJson(ma.Length > 1 ? uint.Parse(ma[1]) : 0u);
                        case "rpose": return Net.RemotePlayers.PoseJson();
                        case "poiusables": return Net.Entities.PoiUsableStats();
                        case "liveusables":
                            if (ma.Length > 1) Net.Entities.LiveUsableSync = ma[1] == "on";
                            return Net.Entities.LiveUsableStats();
                        case "usables": return Net.Entities.Usables(uint.Parse(ma[1]));
                        case "dragusable": return Net.Entities.DragUsable(uint.Parse(ma[1]), int.Parse(ma[2]), int.Parse(ma[3]), float.Parse(ma[4], mic), ma.Length > 5 ? float.Parse(ma[5], mic) : 60f);
                        case "usableprobe": return ma.Length > 1 ? Net.Entities.ProbeUsable(uint.Parse(ma[1]), int.Parse(ma[2]), int.Parse(ma[3]), float.Parse(ma[4], mic)) : Net.Entities.UsableProbeJson();
                        case "editcheck": return Net.Entities.EditCheckJson();
                        case "interest": return Net.Entities.InterestJson();
                        case "forgeedit": return Net.Entities.TestForgeEdit(uint.Parse(ma[1]), ma[2], ma.Length > 3 ? int.Parse(ma[3]) : 1);
                        case "forgepoi": return Net.Entities.TestForgePoi(ma);
                        case "poius": return Net.Entities.PoiList(ma.Length > 1 ? float.Parse(ma[1], mic) : 100f);
                        case "poiu": return Net.Entities.PoiGet(ma);
                        case "poidrag": return Net.Entities.PoiDrag(ma);
                        case "poiprobe": return Net.Entities.PoiProbe(ma);
                        case "poiuse": return Net.Entities.PoiUse(ma);
                        case "decals": { var ci = System.Globalization.CultureInfo.InvariantCulture; return ma.Length >= 5 ? Net.Entities.Decals(0, double.Parse(ma[1], ci), double.Parse(ma[2], ci), double.Parse(ma[3], ci), float.Parse(ma[4], ci)) : Net.Entities.Decals(uint.Parse(ma[1]), 0, 0, 0, 0); }
                        case "shootat": return Net.PlayerCombat.ShootAt(int.Parse(ma[1]), ma.Length > 2 ? float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture) : -1f);
                        case "shootpoint": { var ci = System.Globalization.CultureInfo.InvariantCulture; return Net.PlayerCombat.ShootPoint(double.Parse(ma[1], ci), double.Parse(ma[2], ci), double.Parse(ma[3], ci), ma.Length > 4 ? ma[4] : ""); }
                        case "combat": return Net.PlayerCombat.Stats();
                        case "hitcheck": return Net.HitCheck.Stats();
                        case "copycost": return Net.Entities.CopyCounts();
                        case "trysave": return JoinerSaveBlock.Try(ma[1]);
                        case "say": return "{\"said\":" + (Net.Session.Say(arg.Substring(4)) ? "true" : "false") + "}";
                        case "tpa": return Net.Tpa.Stats();
                        case "gamemode": return Net.GameMode.Stats();
                        case "friendhost": { var fh = Net.Session.FriendHosts(ulong.Parse(ma[1])); return fh == null ? "{\"hosting\":false}" : "{\"hosting\":true,\"version\":" + Json.Str(fh.Version) + ",\"mode\":" + Json.Str(fh.HasMode ? Net.GameMode.Describe(fh.Mode) : "") + "}"; }
                        case "playersave": if (ma.Length > 1 && ma[1] == "record") Net.PlayerSave.SendRecord(); return Net.PlayerSave.Stats();
                        // The host changes its own session's developer policy (the test's own host action: it needs
                        // the joiner to have developer access for the death test's bridge commands). Host only.
                        case "policy":
                        {
                            if (Net.Mp.Server == null) return "{\"error\":\"not hosting\"}";
                            if (ma.Length > 1 && ma[1].Length > 0)
                            {
                                var names = new[] { "everyone", "hostonly", "disabled" };
                                int idx = Array.IndexOf(names, ma[1].ToLowerInvariant());
                                if (idx < 0) return "{\"error\":\"policy everyone|hostonly|disabled\"}";
                                Net.Mp.Server.SetDeveloperPolicy((Net.DeveloperAccessPolicy)idx);
                            }
                            return Net.DeveloperAccess.Status();
                        }
                        case "forgehit": { var cif = System.Globalization.CultureInfo.InvariantCulture; return Net.PlayerCombat.Forge(int.Parse(ma[1]), byte.Parse(ma[2]), new Vector3d(double.Parse(ma[3], cif), double.Parse(ma[4], cif), double.Parse(ma[5], cif)), new UnityEngine.Vector3(float.Parse(ma[6], cif), float.Parse(ma[7], cif), float.Parse(ma[8], cif)), ma.Length > 10 ? uint.Parse(ma[9]) : 0u, ma.Length > 10 ? int.Parse(ma[10]) : 0); }
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
                        case "meter":
                            if (na.Length > 1 && na[1] == "reset") { Net.NetMeter.Reset(); return "{\"reset\":true}"; }
                            if (na.Length > 1 && (na[1] == "show" || na[1] == "hide")) { Net.NetMeter.Show = na[1] == "show"; return "{\"show\":" + (Net.NetMeter.Show ? "true" : "false") + "}"; }
                            if (na.Length > 1 && na[1] == "panel") return "{\"text\":" + Json.Str(Net.NetMeter.PanelText(12)) + "}";
                            return Net.NetMeter.Json();
                        case "items":
                            if (na.Length > 1 && (na[1] == "show" || na[1] == "hide")) { Net.ItemTraffic.SetVisible(na[1] == "show"); return "{\"show\":" + (Net.ItemTraffic.Show ? "true" : "false") + "}"; }
                            if (na.Length > 1 && na[1] == "labels") return ItemTrafficLabels.Json();
                            return Net.ItemTraffic.ListJson(20);
                        case "debugview": return DebugView.Command(na.Length > 1 ? na[1] : "state");
                        case "ownership": case "ghost": case "ghostall": case "links":
                            return WorldOverlays.Command(na[0], na.Length > 1 ? na[1] : "state");
                        case "compare": return ObjectPanels.Command(true, na);
                        case "events": return ObjectPanels.Command(false, na);
                        case "log": return Net.NetMeter.Log(na.Length > 1 ? int.Parse(na[1]) : 50);
                        case "me": return "{\"steamId\":" + Json.Str(Steamworks.SteamUser.GetSteamID().m_SteamID.ToString()) + "}";
                        case "stop": return Net.NetLab.Stop();
                        case "probe": return Net.NetLab.Probe(int.Parse(na[1]));
                        case "lag":
                        {
                            var lic = System.Globalization.CultureInfo.InvariantCulture;
                            return Net.NetLab.Lag(int.Parse(na[1]), na.Length > 2 ? float.Parse(na[2], lic) : 0f, na.Length > 3 ? float.Parse(na[3], lic) : 0f, na.Length > 4 ? int.Parse(na[4]) : 0);
                        }
                        case "conditions": return NetworkConditions.Command(string.Join(" ", na.Skip(1)));
                        default: return "{\"error\":\"net pair <count> <size> [loopback] | status | stats | meter [reset|show|hide|panel] | items [show|hide|labels] | log [n] | stop\"}";
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
                case "carsnear": return DriveLab.CarsNear(arg.Length > 0 ? float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) : 300f);
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
                case "shot": return CaptureLocalShot(arg);
                case "nudge": return DriveLab.Nudge(arg.Length > 0 ? float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) : 12f);
                case "fuelup": return DriveLab.FuelUp();
                case "poiretype": { var pr = arg.Split(' '); return PoiRetype(pr[0], int.Parse(pr[1])); }
                case "poihide": return PoiHide(arg.Trim());
                case "pois": { var pp = arg.Split(' '); var cip = System.Globalization.CultureInfo.InvariantCulture; return Pois(double.Parse(pp[0], cip), double.Parse(pp[1], cip), double.Parse(pp[2], cip)); }
                case "seatwhy": { var sw2 = arg.Split(' '); return Net.Entities.SeatWhy(uint.Parse(sw2[0]), int.Parse(sw2[1])); }
                case "leasetable": return Net.Entities.LeaseTable();
                case "wheelinfo": return Net.Entities.WheelInfo(parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 30f);
                case "picknearest": return Net.Entities.PickupNearest(arg.Length > 0 ? float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) : 3f);
                case "dropheld": return Net.Entities.DropHeld();
                case "pickupcar": return Net.Entities.PickupCar(uint.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
                case "deleteshare": return Net.Entities.DeleteShared(uint.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
                case "leasearm": Net.Entities.LeaseArm(arg); return "{\"armed\":" + Json.Str(arg) + "}";
                case "errors": return Diagnostics.ErrorLog.Json(arg == "reset");
                case "carprefabs":
                {
                    // every vehicle the game can spawn (an item whose prefab has a carscript): id, name, slot count,
                    // and slots outside the prefab root's own hierarchy (vehiclezoo.py)
                    var rows = new System.Collections.Generic.List<string>();
                    var items = itemdatabase.s.items;
                    for (int k = 0; k < items.Length; k++)
                    {
                        var g = items[k];
                        if (g == null || g.GetComponentInChildren<carscript>(true) == null) continue;
                        var ts = g.GetComponent<tosaveitemscript>();
                        int slots = ts != null && ts.partslotscripts != null ? ts.partslotscripts.Count : 0, outside = 0;
                        if (ts != null && ts.partslotscripts != null)
                            foreach (var sl in ts.partslotscripts) if (sl != null && !sl.transform.IsChildOf(g.transform)) outside++;
                        rows.Add("[" + k + "," + Json.Str(g.name) + "," + slots + "," + outside + "]");
                    }
                    return "{\"cars\":[" + string.Join(",", rows) + "]}";
                }
                case "slotdestroys": return "{\"count\":" + Diagnostics.SlotPartDestroyLog.Count + ",\"log\":" + Json.Str(Diagnostics.SlotPartDestroyLog.Log) + "}";
                case "slotmiss": return "{\"misses\":" + Net.ItemSnapshot.SlotMisses + ",\"log\":" + Json.Str(Net.ItemSnapshot.SlotMissLog) + "}";
                case "gpos":
                {
                    var u = mainscript.s.player.transform.position;
                    var gp = mainscript.GlobalFromUnityPos(u);
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    return "{\"ux\":" + u.x.ToString("F1", ci) + ",\"uz\":" + u.z.ToString("F1", ci) +
                           ",\"gx\":" + gp.x.ToString("F2", ci) + ",\"gy\":" + gp.y.ToString("F2", ci) + ",\"gz\":" + gp.z.ToString("F2", ci) + "}";
                }
                case "tpalanding": return TpaLandingCheck();
                case "tpaindoor": return TpaIndoor();
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
                // the terrain mode (issue #32): what is selected, what the running world uses, where it came from
                // `terrain`                   - the mode, its source and the road generator's own flags
                // `terrain select <Crossroads|MainRoadOnly>` - pick the mode for the next new game
                // `terrain host <mode>`       - take a host's mode (what Welcome does)
                case "terrain":
                {
                    if (arg.Length == 0) return WorldGen.TerrainChoice.Stats(WorldGen.TerrainHooks.CurrentMap);
                    var tparts = arg.Split(' ');
                    if (tparts[0] == "select" && tparts.Length > 1)
                    {
                        WorldGen.TerrainMode m;
                        if (!WorldGen.TerrainModes.TryParse(tparts[1], out m) || !WorldGen.TerrainModes.IsSelectable(m))
                            return "{\"error\":\"unknown or disabled mode: " + tparts[1] + "\"}";
                        WorldGen.TerrainHooks.Select(m);
                        WorldGen.TerrainNewGameUi.Refresh();
                        return WorldGen.TerrainChoice.Stats(WorldGen.TerrainHooks.CurrentMap);
                    }
                    if (tparts[0] == "host" && tparts.Length > 1)
                    {
                        WorldGen.TerrainMode m;
                        if (!WorldGen.TerrainModes.TryParse(tparts[1], out m) || !WorldGen.TerrainModes.IsSelectable(m))
                            return "{\"error\":\"unknown or disabled mode: " + tparts[1] + "\"}";
                        WorldGen.TerrainHooks.TakeHost(m);
                        return WorldGen.TerrainChoice.Stats(WorldGen.TerrainHooks.CurrentMap);
                    }
                    return "{\"error\":\"usage: terrain [select <mode>|host <mode>]\"}";
                }
                case "genbacklog":
                    if (menuhandler.s == null || menuhandler.s.currentMainMap == null || menuhandler.s.currentMainMap.poiGens == null)
                        return "{\"error\":\"no POI generators\"}";
                    SessionLog.SampleGen(); return Fixes.PoiBacklog.Stats;
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
                case "screenshot": return CaptureLocalShot("");
                case "poigens": return Fixes.PoiItemsStore.Gens();
                case "poistore": return Fixes.PoiItemsStore.Stats;
                case "unfreezeground": return Fixes.UnfreezeOnGround.Stats;
                case "attachlog": return Diagnostics.AttachLog.Stats;
                case "streamrace": return Fixes.StreamRace.Probe(arg);
                case "mount": return Net.Entities.MountLab(arg);
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
                case "spawnpoints":
                {
                    // item spawn points (newRandomStuffSpawnScript) within r m of a GLOBAL point: path, global position,
                    // chance, the prefabs it can roll and how many places — four RubberWoman spawned into one spot (fallprobe)
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    var sp = arg.Split(' ');
                    var at = new Vector3d(double.Parse(sp[0], ci), 0, double.Parse(sp[1], ci));
                    float rr = sp.Length > 2 ? float.Parse(sp[2], ci) : 3f;
                    var rows = new System.Collections.Generic.List<string>();
                    foreach (var ns in Resources.FindObjectsOfTypeAll<newRandomStuffSpawnScript>())
                    {
                        if (ns == null || !ns.gameObject.scene.IsValid()) continue;
                        var g = mainscript.GlobalFromUnityPos(ns.transform.position);
                        double dx = g.x - at.x, dz = g.z - at.z;
                        if (dx * dx + dz * dz > rr * rr) continue;
                        string spPath = ns.name;
                        for (var t = ns.transform.parent; t != null && spPath.Length < 240; t = t.parent) spPath = t.name + "/" + spPath;
                        var prefabs = new System.Collections.Generic.List<string>();
                        if (ns.items != null) foreach (var x in ns.items) prefabs.Add((x.G != null ? x.G.name : x.prefab) + "x" + (x.places != null ? x.places.Length : 0));
                        rows.Add("{\"path\":" + Json.Str(spPath) + ",\"g\":" + Json.Str(g.ToString()) + ",\"chance\":" + ns.chanceToSpawn.ToString("F2", ci) +
                                 ",\"spawned\":" + (ns.spawned ? "true" : "false") + ",\"active\":" + (ns.gameObject.activeInHierarchy ? "true" : "false") +
                                 ",\"id\":" + ns.GetInstanceID() + ",\"items\":" + Json.Str(string.Join(" ", prefabs)) + "}");
                    }
                    return "{\"count\":" + rows.Count + ",\"points\":[" + string.Join(",", rows) + "]}";
                }
                case "raydown":
                {
                    // every collider straight down from a GLOBAL point (gx gy gz, 50 m), once front faces only (the
                    // default), once with backfaces: is there a floor under the RubberWoman spawn spot (fallprobe)?
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    var q = arg.Split(' ');
                    var u = mainscript.UnityPosFromGlobal(new Vector3d(double.Parse(q[0], ci), double.Parse(q[1], ci), double.Parse(q[2], ci)));
                    double oy = mainscript.s.visszarakva.y;
                    var res = new System.Collections.Generic.List<string>();
                    bool was = Physics.queriesHitBackfaces;
                    foreach (bool bf in new[] { false, true })
                    {
                        Physics.queriesHitBackfaces = bf;
                        var hs = Physics.RaycastAll(u, Vector3.down, 50f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
                        System.Array.Sort(hs, (h1, h2) => h1.distance.CompareTo(h2.distance));
                        var rr = new System.Collections.Generic.List<string>();
                        foreach (var h in hs)
                        {
                            var mc = h.collider as MeshCollider;
                            rr.Add(Json.Str((h.point.y - oy).ToString("F2", ci) + " " + h.collider.name + " (" + h.collider.transform.root.name + ") " + h.collider.GetType().Name +
                                            (mc != null ? (mc.convex ? " convex" : " concave") : "") + (h.collider.isTrigger ? " TRIGGER" : "") + " n.y " + h.normal.y.ToString("F2", ci) +
                                            " layer " + h.collider.gameObject.layer));
                        }
                        res.Add("\"" + (bf ? "backfaces" : "front") + "\":[" + string.Join(",", rr) + "]");
                    }
                    Physics.queriesHitBackfaces = was;
                    return "{" + string.Join(",", res) + "}";
                }
                case "zeroplaces":
                {
                    // every item-spawn entry (loaded prefabs + scene) that has no places: it spawns ON the spawn point's
                    // transform — OldHouse's WifeSpawn 1-4 all sit at the house origin, so up to four RubberWoman
                    // spawn inside each other and inside the floor (fallprobe)
                    var rows = new System.Collections.Generic.List<string>();
                    var seen = new System.Collections.Generic.HashSet<string>();
                    int scanned = 0;
                    foreach (var ns in Resources.FindObjectsOfTypeAll<newRandomStuffSpawnScript>())
                    {
                        if (ns == null || ns.items == null) continue;
                        scanned++;
                        bool inScene = ns.gameObject.scene.IsValid();
                        foreach (var x in ns.items)
                        {
                            if (x == null || (x.places != null && x.places.Length > 0)) continue;
                            string key = ns.transform.root.name.Replace("(Clone)", "") + "/" + ns.name + ":" + (x.G != null ? x.G.name : x.prefab) + (x.isitem ? " item" : x.G != null ? " G" : " category");
                            if (arg == "stacked")
                            {
                                // prefabs only (each building once): local position of the spawn point in its building
                                if (inScene) continue;
                                var lp = ns.transform.root.InverseTransformPoint(ns.transform.position);
                                rows.Add("[" + Json.Str(ns.transform.root.name) + "," + Json.Str(ns.name) + "," + Json.Str(x.G != null ? x.G.name : x.prefab) + "," +
                                         lp.x.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," + lp.y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," +
                                         lp.z.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," + ns.chanceToSpawn.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "]");
                                continue;
                            }
                            if (seen.Add(key)) rows.Add(Json.Str(key + (inScene ? " [scene]" : "")));
                        }
                    }
                    return "{\"scanned\":" + scanned + ",\"count\":" + rows.Count + ",\"entries\":[" + string.Join(",", rows) + "]}";
                }
                case "ragdolls":
                {
                    // every loaded ragdoll item (tosave.ragdollPos): root global y + kinematic, and per limb (childs) y,
                    // Rigidbody kinematic / sleeping / vy, joint present — the RubberWoman that falls through a building
                    // on the laptop (fallprobe)
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    double oy = mainscript.s.visszarakva.y;
                    var rows = new System.Collections.Generic.List<string>();
                    foreach (var kvi in savedatascript.s.items)
                    {
                        var it = kvi.Value;
                        if (it == null || (arg.Length == 0 ? it.ragdollPos == null : it.name.IndexOf(arg, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                        var rb = it.GetComponent<Rigidbody>();
                        int colsOn = 0, colsAll = 0, colsTrig = 0;
                        foreach (var col in it.GetComponentsInChildren<Collider>(true)) { colsAll++; if (col.enabled && col.gameObject.activeInHierarchy) { colsOn++; if (col.isTrigger) colsTrig++; } }
                        int rbs = it.GetComponentsInChildren<Rigidbody>(true).Length;
                        string extra = ",\"cols\":[" + colsOn + "," + colsAll + "," + colsTrig + "],\"rbs\":" + rbs + ",\"parent\":" + Json.Str(it.transform.parent == null ? "-" : it.transform.parent.name) +
                                       ",\"buried\":" + (it.buried ? "true" : "false") + ",\"layer\":" + it.gameObject.layer +
                                       ",\"mode\":" + Json.Str(rb == null ? "-" : rb.collisionDetectionMode.ToString());
                        var limbs = new System.Collections.Generic.List<string>();
                        var ch = it.ragdollPos != null ? it.ragdollPos.childs : null;
                        for (int i = 0; ch != null && i < ch.Length; i++)
                        {
                            var c = ch[i];
                            if (c == null) { limbs.Add("null"); continue; }
                            var lrb = c.GetComponent<Rigidbody>();
                            limbs.Add("[" + Json.Str(c.name) + "," + (c.position.y - oy).ToString("F1", ci) + "," +
                                      (lrb == null ? "\"none\"" : (lrb.isKinematic ? "\"kin\"" : lrb.IsSleeping() ? "\"sleep\"" : "\"dyn\"")) + "," +
                                      (lrb == null ? "0" : lrb.velocity.y.ToString("F1", ci)) + "," + (c.GetComponent<Joint>() != null ? "1" : "0") + "," +
                                      Json.Str(c.parent == null ? "-" : c.parent == it.transform ? "root" : c.parent.name) + "]");
                        }
                        rows.Add("{\"id\":" + kvi.Key + ",\"name\":" + Json.Str(it.name) + ",\"pos\":" + Json.Str(mainscript.GlobalFromUnityPos(it.transform.position).ToString()) +
                                 ",\"y\":" + (it.transform.position.y - oy).ToString("F1", ci) + ",\"rb\":" +
                                 (rb == null ? "\"none\"" : (rb.isKinematic ? "\"kin\"" : rb.IsSleeping() ? "\"sleep\"" : "\"dyn\"")) +
                                 ",\"vy\":" + (rb == null ? "0" : rb.velocity.y.ToString("F1", ci)) + ",\"frozen\":" + (it.ragdollPos != null && it.ragdollPos.freezed ? "true" : "false") + extra +
                                 ",\"limbs\":[" + string.Join(",", limbs) + "]}");
                    }
                    return "{\"count\":" + rows.Count + ",\"items\":[" + string.Join(",", rows) + "]}";
                }
                // The host's password is never read out through the bridge: a diagnostic that carries it would
                // carry it into a log, a report or a test's output.
                case "get":
                    if (SecretPath(arg)) return "{\"value\":\"<set>\"}";
                    return "{\"value\":" + Reflect.Get(arg) + "}";
                case "set":
                    var kv = arg.Split(new[] { ' ' }, 2);
                    if (SecretPath(kv[0])) return "{\"value\":\"<set>\"}";
                    return "{\"value\":" + Reflect.Set(kv[0], kv[1]) + "}";
                case "serverstrip": return Net.DedicatedServer.Strip();
                case "admin":
                    // the server's admin commands (Net/Admin.cs), as its console file or an admin's chat runs them
                    return Net.Mp.Server == null ? "{\"error\":\"not hosting\"}" : "{\"out\":" + Json.Str(Net.Mp.Server.Run(arg)) + "}";
                case "modscan":
                    // the TLD Loader mod scanner (LoaderScan): one DLL, or every mod in BepInEx/TLDLoaderMods
                    return string.IsNullOrWhiteSpace(arg) ? LoaderScan.ScanAll() : LoaderScan.ScanAndWrite(arg.Trim());
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
                    return "{\"commands\":\"alive ping stats scene breakdown net items [show|hide|labels] items [filter] physics spawnpile clearpile loopprof start|stop [n] reset report <note> screenshot overlay on|off bg on|off ground x z profile start [Type.Method ...]|stop [n] " + VR.VrBridgeCommandPolicy.Syntax + " terrainhorizon [x z] terrainhorizonstats vr devaccess get <Type.member.path> set <path> <value> call <path.Method> [args] timescale <f> teleport x y z quality [n]\"}";
                default: return "{\"error\":\"unknown command, try help\"}";
            }
        }
    }
}
