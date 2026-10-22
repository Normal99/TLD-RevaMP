using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TLDRevamp.Net
{
    /// Unity adapter for the pure join gate; idle paths return before sampling or allocating.
    public static partial class JoinLoading
    {
        public static bool Enabled = true;
        internal static JoinLoadingState State = new JoinLoadingState();
        internal static bool Visible => State.Visible;
        private static readonly HashSet<uint> Expected = new HashSet<uint>();
        internal static readonly JoinLoadingMetrics Metrics = new JoinLoadingMetrics();
        private static readonly JoinLoadingDisclosure Disclosure = new JoinLoadingDisclosure();
        private static bool _manifest, _ended, _scene, _geometry, _placed;
        private static int _count, _pending = -1;
        private static double _sampleAt, _floorSince = -1;
        private static long _lastBytes;
        private static double _bytesAt, _detailsAt;
        private static string _route = "unknown", _host = "Host", _version = "Waiting for the host's result", _world = "Waiting for world settings", _placement = "waiting for join position";
        private static string _mod;
        private static uint _modBytes, _modTotal;
        private static int _ping = -1;
        private static Vector3d _floor;
        private static Rigidbody _held;
        private static bool _wasKinematic;
        private static CursorLockMode _cursor;
        private static bool _cursorVisible, _paused;
        private static EventSystem _events;
        private static bool _eventsEnabled;
        private static double _nextText;
        private static float _progress;
        internal static bool Showing => Visible || JoinLoadingPreview.Active;
        internal static void Begin()
        {
            JoinLoadingPreview.Off();
            State = new JoinLoadingState(); Expected.Clear(); _nextText = 0; _progress = 0;
            Metrics.Clear(); _lastBytes = 0; _bytesAt = Time.realtimeSinceStartupAsDouble;
            _detailsAt = 0; _outerScroll = Vector2.zero; Disclosure.Reset();
            _route = "unknown"; _host = "Host"; _ping = -1; _mod = null; _modBytes = _modTotal = 0;
            _version = "Waiting for the host's result"; _world = "Waiting for world settings"; _placement = "waiting for join position";
            _manifest = _ended = _scene = _geometry = _placed = false;
            _count = 0; _pending = -1; _sampleAt = 0; _floorSince = -1;
            _cursor = Cursor.lockState; _cursorVisible = Cursor.visible; _paused = AudioListener.pause;
            State.Begin(Enabled, Time.realtimeSinceStartupAsDouble, AudioListener.volume, SetVolume, Completed);
            if (Visible)
            {
                AudioListener.pause = true; BlockMenuInput();
            }
        }
        private static void SetVolume(float value) => AudioListener.volume = value;
        internal static bool HasReceipt(uint net) => Metrics.Contains(net);
        internal static bool WantsReceipt(uint net) => Visible && Expected.Contains(net) && !Metrics.Contains(net);
        internal static void Receipt(uint net, int prefab, JoinObjectKind kind, bool recent = true)
        {
            if (Visible && Expected.Contains(net)) Metrics.Record(net, prefab, kind, recent);
        }
        private static string ReceiptName(JoinReceipt row)
        {
            string name = row.Prefab < 0 ? "" : TelemetryContent.ItemName(row.Prefab);
            return string.IsNullOrEmpty(name) || name == TelemetryPrivacy.Removed ? (row.Kind == JoinObjectKind.ItemPart ? "Item / part" : "Shared object") : name;
        }
        private static void Completed(int stage, double ms) => SessionLog.LoadingStage(stage, ms);
        internal static void End(string reason)
        {
            bool restore = State.Muted;
            if (Visible) Metrics.ElapsedMs = (Time.realtimeSinceStartupAsDouble - State.Started) * 1000;
            try { State.Close(reason); }
            finally
            {
                if (restore) { RestoreControls(); DebugBridge.CancelJoinPlacement(); }
                Release();
            }
        }
        private static void RestoreControls()
        {
            AudioListener.pause = _paused; Cursor.lockState = _cursor; Cursor.visible = _cursorVisible;
            if (_events != null) _events.enabled = _eventsEnabled;
            _events = null;
        }
        private static void BlockMenuInput()
        {
            var current = EventSystem.current;
            if (current == null || current == _events) return;
            if (_events != null) _events.enabled = _eventsEnabled;
            _events = current; _eventsEnabled = current.enabled; current.enabled = false;
        }
        private static void Release()
        {
            if (_held != null) _held.isKinematic = _wasKinematic;
            _held = null;
        }
        internal static void Manifest(NetReader r)
        {
            if (!Visible) return;
            byte kind = r.U8();
            if (kind == 1 && !r.Bad && r.Remaining == 0) { _ended = true; return; }
            if (kind != 0) return;
            uint n = r.VarU32();
            if (r.Bad || n > r.Remaining / 4 || n * 4 != r.Remaining) return;
            Expected.Clear();
            for (uint i = 0; i < n; i++) Expected.Add(r.U32());
            Metrics.Reserve(Expected.Count);
            _manifest = true; State.Progress(Time.realtimeSinceStartupAsDouble);
        }
        internal static void Tick()
        {
            if (!Visible) return;
            try { TickLoading(); }
            catch (Exception e)
            {
                End("error"); Session.Leave();
                Plugin.Log.LogError("Join loading: " + e);
                Session.Show(1, "", "Joining failed while loading. See the log for details.");
            }
        }
        private static void TickLoading()
        {
            if (!Visible) return;
            var c = Mp.Client;
            if (c == null || c.Lost || c.RejectReason != null)
            {
                string reason = c?.RejectReason ?? "Join ended";
                End(reason);
                if (c != null) { Session.EndReason = reason; Session.Leave(); Session.Show(1, "", reason); }
                return;
            }
            AudioListener.volume = 0;
            AudioListener.pause = true; Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            BlockMenuInput();
            double now = Time.realtimeSinceStartupAsDouble;
            if (Input.GetKeyDown(KeyCode.Escape)) { Cancel(); return; }
            if (now < _sampleAt) return;
            _sampleAt = now + .2;
            _scene = c.WorldLoaded;
            int pending = -1;
            _geometry = _scene && c.SpotPlaced && Geometry(out pending);
            if (!_scene || !c.SpotPlaced) { pending = -1; Metrics.Geometry = default; }
            if (pending != _pending) { _pending = pending; State.Progress(now); }
            var pl = mainscript.s != null ? mainscript.s.player : null;
            bool floor = false;
            if (_geometry && pl != null && !DebugBridge.TpBusy)
            {
                var at = Mp.LocalGlobal();
                // standing on a floor, whatever is around them: with the room check a joiner was placed again 1 m over
                // for anything beside them — their own things given back (PlayerSave), a rabbit — and a teleport lets go
                // of what the hands hold (tools/rejoin.py 2026-10-22: back 1 m off, the lamp no longer in the hand)
                var hit = DebugBridge.FloorHit(pl.transform.position.x, pl.transform.position.z, pl.transform.position.y + .1f);
                bool supported = hit.HasValue && hit.Value.collider.attachedRigidbody == null &&
                    Math.Abs(pl.transform.position.y - hit.Value.point.y - 1.1) < .5;
                floor = supported && DebugBridge.TpaWorldReady(at.x, at.y, at.z);
                if (!floor || (_floor - at).magnitude > .05) _floorSince = -1;
                _floor = at;
                if (floor && _floorSince < 0) _floorSince = now;
                // The game's 30 s placement wait can expire before slow generation finishes. Stay held and
                // retry the real floor placement; the game's start position also sometimes needs settling.
                if (!supported) DebugBridge.PlaceAt(at.x, at.z);
            }
            else _floorSince = -1;
            _placed = floor && _floorSince >= 0 && now - _floorSince >= .5;
            _count = Entities.JoinApplied(Expected);
            State.Observe(now, c.ConnectedToHost, c.MyId > 0, _scene, _geometry,
                _manifest && _ended && _count == Expected.Count && Entities.TpaInboxEmpty, _placed, _count, Expected.Count);
            if (!Visible) { RestoreControls(); Release(); }
            // Presentation failures must not cancel the join or change the gate's control/audio lease.
            try { SampleDetails(c, now); }
            catch (Exception e) { Plugin.Log.LogWarning("Join loading details: " + e.Message); }
        }
        internal static bool GeometryReady => _geometry;
        private static void SampleDetails(MpClient c, double now)
        {
            if (now < _detailsAt && Visible) return;
            _detailsAt = now + .25;
            Metrics.ElapsedMs = (now - State.Started) * 1000;
            long bytes = c.T.BytesIn;
            Metrics.BytesReceived = bytes; // This transport belongs to this join; includes handshake and mod bytes.
            Metrics.Rate = now > _bytesAt ? Math.Max(0, bytes - _lastBytes) / (now - _bytesAt) : 0;
            _lastBytes = bytes; _bytesAt = now;
            _host = c.HostName ?? (Session.LastJoinSteamId != 0 ? Session.Persona(new Steamworks.CSteamID(Session.LastJoinSteamId)) : "Host");
            _version = c.MyId > 0 ? "RevaMP version accepted; mods matched" : c.LoadingMod != null ? "RevaMP version accepted; required mods offered" : "Waiting for the host's result";
            _mod = c.LoadingMod; _modBytes = c.LoadingModBytes; _modTotal = c.LoadingModTotal;
            _world = c.MyId <= 0 ? "Waiting for world settings" : _scene ? "Scene loaded" : "Loading the scene from the host's seed";
            _placement = !c.SpotPlaced ? (DebugBridge.TpBusy ? "waiting for ground" : "waiting for join position") : _floorSince >= 0 ? (_placed ? "ground stable" : "settling") : "waiting for ground";
            if (c.ConnectedToHost && c.T.Connections.Count > 0)
            {
                _ping = c.T.Status(c.T.Connections[0]).Ping;
                try
                {
                    string route = SteamCompat.Route(c.T.Connections[0]);
                    _route = !route.Contains("\"ok\":true") ? "unknown" : route.Contains("\"relayed\":true") ? "relay" : "direct";
                }
                catch { _route = "unknown"; }
            }
            Metrics.PublishFeed(ReceiptName);
        }
        private static bool Geometry(out int pending)
        {
            pending = 0;
            var counts = new JoinGeometryCounts(); Metrics.Geometry = counts;
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.terrainGen == null || map.roadGens == null) return false;
            foreach (var g in map.terrainGen)
            {
                if (g == null || g.coordsNeeded == null || g.terrains == null) continue;
                foreach (var key in g.coordsNeeded)
                {
                    counts.Terrain(g.terrains.TryGetValue(key, out var tile) && tile != null && tile.mf != null && tile.mf.sharedMesh != null &&
                        (!g.hasCollider || (tile.mc != null && tile.mc.sharedMesh != null)));
                }
            }
            foreach (var g in map.roadGens)
                if (g != null) foreach (var key in g.chunksNeeded) counts.Road(g.ChunkDone(key));
            Metrics.Geometry = counts; pending = counts.Pending;
            return counts.Ready;
        }
        internal static void FixedTick()
        {
            if (!Visible) return;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || pl.RB == null || !pl.loadedmove) return;
            if (_held != pl.RB) { Release(); _held = pl.RB; _wasKinematic = _held.isKinematic; }
            if (!_held.isKinematic) { _held.velocity = Vector3.zero; _held.angularVelocity = Vector3.zero; }
            _held.isKinematic = true; pl.godModeTillGrounded = true;
        }
        private static void Cancel() { End("cancelled"); Session.Leave(); }
        public static string Probe(string action = "")
        {
            if (action == "cancel") Cancel();
            if (action == "continue" && Visible) { State.Continue(Time.realtimeSinceStartupAsDouble); if (!Visible) { RestoreControls(); Release(); } }
            var b = new StringBuilder("{\"visible\":").Append(Visible ? "true" : "false").Append(",\"stage\":").Append(State.Stage).Append(",\"stages\":[");
            for (int i = 0; i < 7; i++)
            {
                if (i > 0) b.Append(',');
                double ms = State.Ms[i];
                if (Visible && i == State.Stage) ms = (Time.realtimeSinceStartupAsDouble - State.StageAt) * 1000;
                b.Append("{\"name\":").Append(Json.Str(JoinLoadingState.Names[i])).Append(",\"state\":").Append(Json.Str(i < State.Stage || State.Ready ? "done" : i == State.Stage ? "loading" : "pending"))
                    .Append(",\"count\":").Append(i == 4 ? State.Count : 0).Append(",\"total\":").Append(i == 4 ? State.Total : 0).Append(",\"ms\":").Append(Json.Num(ms)).Append('}');
            }
            b.Append("],\"expandedSteps\":[");
            bool comma = false;
            for (int i = 0; i < 7; i++)
                if (Disclosure.Expanded(i)) { if (comma) b.Append(','); b.Append(i); comma = true; }
            b.Append("],\"receivedExpanded\":").Append(Disclosure.ReceivedExpanded ? "true" : "false")
                .Append(",\"muted\":").Append(State.Muted ? "true" : "false").Append(",\"audioVolume\":").Append(Json.Num(AudioListener.volume))
                .Append(",\"ready\":").Append(State.Ready ? "true" : "false").Append(",\"reason\":").Append(Json.Str(Reason()))
                .Append(",\"scene\":").Append(_scene ? "true" : "false").Append(",\"geometry\":").Append(_geometry ? "true" : "false")
                .Append(",\"ground\":").Append(_placed ? "true" : "false").Append(",\"initialSync\":").Append(_manifest && _ended && _count == Expected.Count ? "true" : "false");
            Metrics.AppendProbe(b);
            return b.Append(",\"hostName\":").Append(Json.Str(_host)).Append(",\"route\":").Append(Json.Str(_route)).Append(",\"pingMs\":").Append(_ping)
                .Append(",\"versionCheck\":").Append(Json.Str(_version)).Append(",\"worldActivity\":").Append(Json.Str(_world))
                .Append(",\"placementStatus\":").Append(Json.Str(_placement)).Append(",\"loadingMod\":").Append(Json.Str(_mod))
                .Append(",\"loadingModBytes\":").Append(_modBytes).Append(",\"loadingModTotal\":").Append(_modTotal)
                .Append(",\"manifestReceived\":").Append(_manifest ? "true" : "false").Append(",\"initialRecordsEnded\":").Append(_ended ? "true" : "false")
                .Append(",\"geometrySampled\":").Append(_pending >= 0 ? "true" : "false")
                .Append(",\"expectedObjects\":").Append(Expected.Count)
                .Append(",\"loadingScreen\":").Append(Json.Str("revamp"))
                .Append(",\"progressPercent\":").Append(Json.Num(Progress() * 100)).Append('}').ToString();
        }
        private static string Reason() => !Visible ? State.Reason : !_scene ? JoinLoadingState.Names[State.Stage] : Mp.Client != null && !Mp.Client.SpotPlaced ? (DebugBridge.TpBusy ? "Waiting for ground at the join position" : "Waiting for the host's join position") : !_geometry ? "Waiting for terrain and roads" : !_manifest ? "Waiting for the host's initial object list" : !_ended ? "Waiting for the host to finish initial object sync" : _count != Expected.Count ? "Waiting for shared objects to be placed" : "Waiting for settled ground";
        // Stage-based progress: no known total transfer size or linear generation estimate exists.
        private static float Progress() => _progress = JoinPresentation.Forward(_progress, (State.Stage + (State.Stage == 4 && State.Total > 0 ? (float)State.Count / State.Total : 0)) / 6);
        private static string YesNo(bool value) => value ? "yes" : "no";
    }

    [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Move))]
    internal static class JoinLoadingMovement
    {
        [HarmonyPrefix]
        private static bool Prefix() => !JoinLoading.Visible;
    }
}
