using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TLDRevamp.Net
{
    /// Unity adapter for the pure join gate; idle paths return before sampling or allocating.
    public static class JoinLoading
    {
        public static bool Enabled = true;
        internal static JoinLoadingState State = new JoinLoadingState();
        internal static bool Visible => State.Visible;
        private static readonly HashSet<uint> Expected = new HashSet<uint>();
        internal static readonly JoinLoadingMetrics Metrics = new JoinLoadingMetrics();
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
        private static readonly GUIContent Title = new GUIContent("Joining the host's world"), Host = new GUIContent(), Detail = new GUIContent(), Question = new GUIContent(), Stuck = new GUIContent("No progress for 60 s. You can wait, cancel or continue anyway.");
        private static readonly GUIContent ReceivedTitle = new GUIContent("Received"), ReceivedHint = new GUIContent("Most recent initial objects"), Counters = new GUIContent(), Footer = new GUIContent(), Percent = new GUIContent();
        private static readonly GUIContent Credit = new GUIContent("Multiplayer · RevaMP");
        private static readonly GUIContent Check = new GUIContent("\u2713");
        private static readonly GUIContent[] Stages = Contents(7), StageDetails = Contents(7), Timers = Contents(7), Feed = Contents(JoinLoadingMetrics.FeedCapacity);
        private static readonly float[] StageY = new float[7], StageDetailHeight = new float[7];
        private static double _nextText;
        private static float _progress;
        private static float _questionHeight, _titleHeight, _hostHeight, _detailHeight, _stuckHeight, _stagesHeight, _receivedHeight, _footerHeight, _columnWidth, _rowWidth, _countersHeight;
        private static Vector2 _questionScroll, _dialogScroll;
        private static Texture2D _backdrop;
        private static GUIStyle _titleStyle;
        private static bool _twoColumns;
        private static int _feedRows;
        private static GUIContent[] Contents(int count)
        {
            var rows = new GUIContent[count];
            for (int i = 0; i < count; i++) rows[i] = new GUIContent();
            return rows;
        }
        internal static void Begin()
        {
            JoinLoadingNative.Close();
            State = new JoinLoadingState(); Expected.Clear(); _nextText = 0; _progress = 0;
            Metrics.Clear(); _lastBytes = 0; _bytesAt = Time.realtimeSinceStartupAsDouble;
            _detailsAt = 0; _dialogScroll = _questionScroll = Vector2.zero;
            _route = "unknown"; _host = "Host"; _ping = -1; _mod = null; _modBytes = _modTotal = 0;
            _version = "Waiting for the host's result"; _world = "Waiting for world settings"; _placement = "waiting for join position";
            _manifest = _ended = _scene = _geometry = _placed = false;
            _count = 0; _pending = -1; _sampleAt = 0; _floorSince = -1;
            _cursor = Cursor.lockState; _cursorVisible = Cursor.visible; _paused = AudioListener.pause;
            State.Begin(Enabled, Time.realtimeSinceStartupAsDouble, AudioListener.volume, SetVolume, Completed);
            if (Visible)
            {
                AudioListener.pause = true; BlockMenuInput();
                try { JoinLoadingNative.Begin(); }
                catch (Exception e) { JoinLoadingNative.Close(); Plugin.Log.LogWarning("Join loading presentation: " + e.Message); }
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
                JoinLoadingNative.Close(); Release();
            }
        }
        private static void RestoreControls()
        {
            AudioListener.pause = _paused; Cursor.lockState = _cursor; Cursor.visible = _cursorVisible;
            if (_events != null) _events.enabled = _eventsEnabled;
            _events = null;
            JoinLoadingNative.Close();
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
            try { JoinLoadingNative.Update(Progress()); }
            catch (Exception e) { JoinLoadingNative.Fallback(); Plugin.Log.LogWarning("Join loading presentation: " + e.Message); }
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
                var hit = DebugBridge.StandHit(pl.transform.position.x, pl.transform.position.z, pl.transform.position.y + .1f);
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
            _world = c.MyId <= 0 ? "Waiting for world settings" : _scene ? "Local scene loaded from host settings" : "Loading local scene from host seed and world settings";
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
            b.Append("],\"muted\":").Append(State.Muted ? "true" : "false").Append(",\"audioVolume\":").Append(Json.Num(AudioListener.volume))
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
                .Append(",\"loadingScreen\":").Append(Json.Str(JoinLoadingNative.Used ? "native" : "fallback"))
                .Append(",\"progressPercent\":").Append(Json.Num(Progress() * 100)).Append('}').ToString();
        }
        private static string Reason() => !Visible ? State.Reason : !_scene ? JoinLoadingState.Names[State.Stage] : Mp.Client != null && !Mp.Client.SpotPlaced ? (DebugBridge.TpBusy ? "Waiting for ground at the join position" : "Waiting for the host's join position") : !_geometry ? "Waiting for terrain and roads" : !_manifest ? "Waiting for the host's initial object list" : !_ended ? "Waiting for the host to finish initial object sync" : _count != Expected.Count ? "Waiting for shared objects to be placed" : "Waiting for settled ground";
        // Stage-based progress: no known total transfer size or linear generation estimate exists.
        private static float Progress() => _progress = JoinPresentation.Forward(_progress, (State.Stage + (State.Stage == 4 && State.Total > 0 ? (float)State.Count / State.Total : 0)) / 6);
        private static string YesNo(bool value) => value ? "yes" : "no";
        private static void RebuildText(float width, float height, double now)
        {
            float s = JoinLoadingSkin.Scale, gap = JoinLoadingSkin.Gap;
            _nextText = now + .25;
            _twoColumns = JoinDialogLayout.TwoColumns(width, height + Screen.height * .32f, s);
            _columnWidth = _twoColumns ? (width - 40 * s) * .59f : width;
            _rowWidth = _columnWidth - 24 * s;
            if (_titleStyle == null) _titleStyle = new GUIStyle(JoinLoadingSkin.Large) { wordWrap = true };
            _titleStyle.fontSize = JoinLoadingSkin.Large.fontSize;
            Host.text = _host;
            var g = Metrics.Geometry;
            StageDetails[0].text = $"{_host} · {_route}" + (_ping >= 0 ? $" · {_ping:N0} ms" : " · ping unknown");
            StageDetails[1].text = _version + (_mod != null ? $"\n{(MpScreen.PendingQuestion != null ? "Awaiting approval" : "Downloading")} {_mod}: {_modBytes:N0} / {_modTotal:N0} B" : "");
            StageDetails[2].text = $"{_world}\nFrom host: {Metrics.BytesReceived:N0} B · {Metrics.Rate / 1000:F1} KB/s · scene loaded: {YesNo(_scene)}";
            StageDetails[3].text = _pending < 0 ? "Waiting for the scene and join position" : $"Terrain tiles {g.TerrainDone:N0} / {g.TerrainNeeded:N0} · road chunks {g.RoadsDone:N0} / {g.RoadsNeeded:N0}";
            StageDetails[4].text = !_manifest ? "Waiting for the initial object manifest" : $"Applied {State.Count:N0} / {Expected.Count:N0} · received {Metrics.Received:N0} / {Expected.Count:N0}\n" + (State.Objects ? "Initial object sync complete" : _ended ? "Host finished sending; waiting for object application" : "Waiting for initial records and end marker");
            StageDetails[5].text = _placement;
            StageDetails[6].text = "";
            _stagesHeight = 0;
            for (int i = 0; i < Stages.Length; i++)
            {
                StageY[i] = _stagesHeight;
                Stages[i].text = JoinLoadingState.Names[i];
                float nameHeight = JoinLoadingSkin.Height(Stages[i], Math.Max(1, _rowWidth - 68 * s), JoinLoadingSkin.Wrap);
                double ms = i < State.Stage ? State.Ms[i] : i == State.Stage ? (now - State.StageAt) * 1000 : 0;
                Timers[i].text = i <= State.Stage ? $"{ms / 1000:F1} s" : "";
                bool show = i <= State.Stage && StageDetails[i].text.Length > 0;
                StageDetailHeight[i] = show ? JoinLoadingSkin.Height(StageDetails[i], _rowWidth, JoinLoadingSkin.Wrap) : 0;
                _stagesHeight += Math.Max(nameHeight, JoinLoadingSkin.Line) + (show ? StageDetailHeight[i] + gap / 2 : 0) + gap * 1.5f;
            }
            string expected = _manifest ? Expected.Count.ToString("N0") : "unknown";
            Counters.text = $"{Metrics.Received:N0} objects received · {State.Count:N0} applied\nExpected: {expected}\n\n{Metrics.Vehicles:N0} vehicles\n{Metrics.ItemsParts:N0} items / parts\n{Metrics.Other:N0} other";
            float receivedWidth = _twoColumns ? width - _columnWidth - 40 * s : width;
            _countersHeight = JoinLoadingSkin.Height(Counters, receivedWidth, JoinLoadingSkin.Wrap);
            _receivedHeight = JoinLoadingSkin.Title.lineHeight + gap + _countersHeight + 3 * gap + JoinLoadingSkin.Line + gap;
            _feedRows = Math.Max(1, Metrics.ShownCount);
            for (int i = 0; i < Feed.Length; i++)
            {
                Feed[i].text = OverlayStyle.Truncate(i < Metrics.ShownCount ? Metrics.Names[i] : i == 0 ? "Waiting for shared objects" : "", receivedWidth, JoinLoadingSkin.Body);
                if (i < _feedRows) _receivedHeight += JoinLoadingSkin.Line + gap / 2;
            }
            Detail.text = Reason();
            Footer.text = $"{Metrics.ElapsedMs / 1000:F1} s elapsed · {Metrics.BytesReceived / 1000000d:F2} MB received · {Metrics.Rate / 1000:F1} KB/s · {Progress() * 100:F0}%";
            Percent.text = $"{Progress() * 100:F0}% · stage progress";
            _titleHeight = JoinLoadingSkin.Height(Title, width, _titleStyle);
            _hostHeight = JoinLoadingSkin.Height(Host, width, JoinLoadingSkin.Wrap);
            _detailHeight = JoinLoadingSkin.Height(Detail, width, JoinLoadingSkin.Wrap);
            _footerHeight = JoinLoadingSkin.Height(Footer, width, JoinLoadingSkin.Wrap);
            _stuckHeight = JoinLoadingSkin.Height(Stuck, width, JoinLoadingSkin.Wrap);
            Question.text = MpScreen.PendingQuestion;
            _questionHeight = Question.text == null ? 0 : JoinLoadingSkin.Height(Question, Math.Max(1, width - 24 * s), JoinLoadingSkin.Wrap);
        }
        private static void Background()
        {
            if (_backdrop == null)
            {
                _backdrop = new Texture2D(1, 64) { wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < 64; y++) _backdrop.SetPixel(0, y, Color.Lerp(new Color(.09f, .08f, .065f, 1), new Color(.16f, .145f, .12f, 1), y / 63f));
                _backdrop.Apply(false, true);
            }
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _backdrop);
        }
        private static void DrawReceived(float x, float y, float width)
        {
            float gap = JoinLoadingSkin.Gap;
            JoinLoadingSkin.Label(new Rect(x, y, width, JoinLoadingSkin.Title.lineHeight), ReceivedTitle, OverlayKind.Text, JoinLoadingSkin.Title);
            y += JoinLoadingSkin.Title.lineHeight + gap;
            JoinLoadingSkin.Label(new Rect(x, y, width, _countersHeight), Counters, OverlayKind.Text, JoinLoadingSkin.Wrap);
            y += _countersHeight + 3 * gap;
            JoinLoadingSkin.Label(new Rect(x, y, width, JoinLoadingSkin.Line), ReceivedHint, OverlayKind.Muted, JoinLoadingSkin.Small);
            y += JoinLoadingSkin.Line + gap;
            for (int i = 0; i < _feedRows; i++)
            {
                JoinLoadingSkin.Label(new Rect(x, y, width, JoinLoadingSkin.Line), Feed[i], i < Metrics.ShownCount ? OverlayKind.Text : OverlayKind.Muted);
                y += JoinLoadingSkin.Line + gap / 2;
            }
        }
        internal static void Draw()
        {
            if (!Visible) return;
            JoinLoadingSkin.Ensure();
            int depth = GUI.depth; var colour = GUI.color; var matrix = GUI.matrix;
            GUI.depth = -10000; GUI.color = Color.white; GUI.matrix = Matrix4x4.identity;
            try { DrawScreen(); }
            finally { GUI.depth = depth; GUI.color = colour; GUI.matrix = matrix; }
            if (Event.current.isMouse) Event.current.Use();
        }
        private static void DrawScreen()
        {
            float s = JoinLoadingSkin.Scale, gap = JoinLoadingSkin.Gap;
            var placement = JoinPresentation.Paper(Screen.width, Screen.height);
            float width = placement.W, viewportHeight = placement.H;
            // Reserve a scrollbar gutter so cached wrapping remains valid when content grows.
            float w = Math.Max(1, width - 2 * JoinLoadingSkin.Padding - 20 * s);
            double now = Time.realtimeSinceStartupAsDouble;
            // Even continuous resizing must not rebuild strings or measure text every frame.
            if (now >= _nextText)
                RebuildText(w, viewportHeight, now);
            bool stuck = State.Stuck(now), question = Question.text != null;
            float stagesY = _titleHeight + gap + _hostHeight + 3 * gap;
            float columnsHeight = _twoColumns ? Math.Max(_stagesHeight, _receivedHeight) : _stagesHeight + _receivedHeight + 2 * gap;
            float progressY = stagesY + columnsHeight + 2 * gap;
            float detailY = progressY + 6 * s + gap + JoinLoadingSkin.Line + gap;
            float footerY = detailY + _detailHeight + gap;
            float creditY = footerY + _footerHeight + gap;
            float extraY = creditY + JoinLoadingSkin.Line + 2 * gap;
            float questionView = Mathf.Min(140 * s, _questionHeight);
            float questionButtonsY = extraY + questionView + gap;
            float buttonsY = question ? questionButtonsY + JoinLoadingSkin.ButtonHeight + gap : extraY + (stuck ? _stuckHeight + gap : 0);
            float bodyHeight = buttonsY + JoinLoadingSkin.ButtonHeight + gap;
            float naturalHeight = JoinLoadingSkin.Header + bodyHeight + JoinLoadingSkin.Padding;
            float height = Math.Min(naturalHeight, viewportHeight);
            var frame = new Rect(placement.X, placement.Y, width, height);
            if (!JoinLoadingNative.Active)
            {
                if (Event.current.type == EventType.Repaint) Background();
                JoinLoadingSkin.Fallback(Progress());
            }
            var content = JoinLoadingSkin.Panel(frame);
            bool scroll = naturalHeight > height;
            if (scroll) _dialogScroll = GUI.BeginScrollView(content, _dialogScroll, new Rect(0, 0, w, bodyHeight));
            else GUI.BeginGroup(content);
            JoinLoadingSkin.Label(new Rect(0, 0, w, _titleHeight), Title, OverlayKind.Text, _titleStyle);
            JoinLoadingSkin.Label(new Rect(0, _titleHeight + gap, w, _hostHeight), Host, OverlayKind.Muted, JoinLoadingSkin.Wrap);
            for (int i = 0; i < Stages.Length; i++)
            {
                bool done = i < State.Stage || State.Ready, current = i == State.Stage && !State.Ready;
                var kind = done ? OverlayKind.Ok : current ? OverlayKind.Accent : OverlayKind.Muted;
                float y = stagesY + StageY[i];
                float nameHeight = (i + 1 < Stages.Length ? StageY[i + 1] : _stagesHeight) - StageY[i] - gap * 1.5f - (StageDetailHeight[i] > 0 ? StageDetailHeight[i] + gap / 2 : 0);
                if (done) JoinLoadingSkin.Label(new Rect(0, y, 20 * s, JoinLoadingSkin.Line), Check, OverlayKind.Ok);
                else if (Event.current.type == EventType.Repaint)
                {
                    for (int dot = 0; dot < (current ? 3 : 1); dot++)
                        JoinLoadingSkin.StatusDot(new Rect(dot * 5 * s, y + JoinLoadingSkin.Line / 2, 3 * s, 3 * s), current && dot == (int)(now * 4) % 3 ? OverlayKind.Accent : OverlayKind.Muted);
                }
                JoinLoadingSkin.Label(new Rect(24 * s, y, Math.Max(1, _rowWidth - 68 * s), nameHeight), Stages[i], kind, JoinLoadingSkin.Wrap);
                JoinLoadingSkin.Label(new Rect(_columnWidth - 68 * s, y, 68 * s, JoinLoadingSkin.Line), Timers[i], kind, JoinLoadingSkin.Numeric);
                if (StageDetailHeight[i] > 0) JoinLoadingSkin.Label(new Rect(24 * s, y + nameHeight + gap / 2, _rowWidth, StageDetailHeight[i]), StageDetails[i], OverlayKind.Muted, JoinLoadingSkin.Wrap);
            }
            float receivedX = _twoColumns ? _columnWidth + 40 * s : 0;
            DrawReceived(receivedX, _twoColumns ? stagesY : stagesY + _stagesHeight + 2 * gap, w - receivedX);
            if (Event.current.type == EventType.Repaint)
            {
                if (_twoColumns) JoinLoadingSkin.Fill(new Rect(_columnWidth + 20 * s, stagesY, s, columnsHeight), JoinLoadingSkin.Muted);
                if (!JoinLoadingNative.Active) JoinLoadingSkin.Progress(new Rect(0, progressY, w, 4 * s), Progress(), OverlayKind.Accent);
            }
            // The game's own bar shows progress; the panel's bar is for the fallback only.
            if (!JoinLoadingNative.Active) JoinLoadingSkin.Label(new Rect(0, progressY + 6 * s + gap, w, JoinLoadingSkin.Line), Percent, OverlayKind.Accent, JoinLoadingSkin.Numeric);
            JoinLoadingSkin.Label(new Rect(0, detailY, w, _detailHeight), Detail, OverlayKind.Muted, JoinLoadingSkin.Wrap);
            JoinLoadingSkin.Label(new Rect(0, footerY, w, _footerHeight), Footer, OverlayKind.Text, JoinLoadingSkin.Wrap);
            JoinLoadingSkin.Label(new Rect(0, creditY, w, JoinLoadingSkin.Line), Credit, OverlayKind.Muted, JoinLoadingSkin.Small);
            float buttonWidth = Math.Max(1, Math.Min(290 * s, (w - gap) / 2));
            if (question)
            {
                _questionScroll = GUI.BeginScrollView(new Rect(0, extraY, w, questionView), _questionScroll, new Rect(0, 0, w - 24 * s, _questionHeight));
                JoinLoadingSkin.Label(new Rect(0, 0, w - 24 * s, _questionHeight), Question, OverlayKind.Text, JoinLoadingSkin.Wrap); GUI.EndScrollView();
                if (JoinLoadingSkin.Click(new Rect(0, questionButtonsY, buttonWidth, JoinLoadingSkin.ButtonHeight), "Download mods", true)) MpScreen.AskProbe("yes");
                if (JoinLoadingSkin.Click(new Rect(buttonWidth + gap, questionButtonsY, buttonWidth, JoinLoadingSkin.ButtonHeight), "Decline")) MpScreen.AskProbe("no");
            }
            if (stuck)
            {
                if (!question) JoinLoadingSkin.Label(new Rect(0, extraY, w, _stuckHeight), Stuck, OverlayKind.Warning, JoinLoadingSkin.Wrap);
                if (JoinLoadingSkin.Click(new Rect(buttonWidth + gap, buttonsY, buttonWidth, JoinLoadingSkin.ButtonHeight), "Continue anyway", true)) Probe("continue");
            }
            if (JoinLoadingSkin.Click(new Rect(0, buttonsY, buttonWidth, JoinLoadingSkin.ButtonHeight), "Cancel (Esc)")) Cancel();
            if (scroll) GUI.EndScrollView(); else GUI.EndGroup();
        }
    }

    [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Move))]
    internal static class JoinLoadingMovement
    {
        [HarmonyPrefix]
        private static bool Prefix() => !JoinLoading.Visible;
    }
}
