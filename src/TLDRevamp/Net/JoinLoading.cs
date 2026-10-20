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
        private static bool _manifest, _ended, _scene, _geometry, _placed;
        private static int _count, _pending = -1;
        private static double _sampleAt, _floorSince = -1;
        private static Vector3d _floor;
        private static Rigidbody _held;
        private static bool _wasKinematic;
        private static CursorLockMode _cursor;
        private static bool _cursorVisible, _paused;
        private static EventSystem _events;
        private static bool _eventsEnabled;
        private static GUIStyle _style, _title;
        private static Vector2 _questionScroll;
        internal static void Begin()
        {
            State = new JoinLoadingState(); Expected.Clear();
            _manifest = _ended = _scene = _geometry = _placed = false;
            _count = 0; _pending = -1; _sampleAt = 0; _floorSince = -1;
            _cursor = Cursor.lockState; _cursorVisible = Cursor.visible; _paused = AudioListener.pause;
            State.Begin(Enabled, Time.realtimeSinceStartupAsDouble, AudioListener.volume, SetVolume, Completed);
            if (Visible) { AudioListener.pause = true; BlockMenuInput(); }
        }
        private static void SetVolume(float value) => AudioListener.volume = value;
        private static void Completed(int stage, double ms) => SessionLog.LoadingStage(stage, ms);
        internal static void End(string reason)
        {
            bool restore = State.Muted;
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
            if (!_scene || !c.SpotPlaced) pending = -1;
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
        }
        internal static bool GeometryReady => _geometry;
        private static bool Geometry(out int pending)
        {
            pending = 0;
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.terrainGen == null || map.roadGens == null) return false;
            bool terrain = false;
            foreach (var g in map.terrainGen)
            {
                if (g == null || g.coordsNeeded == null || g.terrains == null) continue;
                foreach (var key in g.coordsNeeded)
                {
                    terrain = true;
                    if (!g.terrains.TryGetValue(key, out var tile) || tile == null || tile.mf == null || tile.mf.sharedMesh == null ||
                        (g.hasCollider && (tile.mc == null || tile.mc.sharedMesh == null))) pending++;
                }
            }
            foreach (var g in map.roadGens)
                if (g != null) foreach (var key in g.chunksNeeded) if (!g.ChunkDone(key)) pending++;
            return terrain && pending == 0;
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
            return b.Append("],\"muted\":").Append(State.Muted ? "true" : "false").Append(",\"audioVolume\":").Append(Json.Num(AudioListener.volume))
                .Append(",\"ready\":").Append(State.Ready ? "true" : "false").Append(",\"reason\":").Append(Json.Str(Reason()))
                .Append(",\"scene\":").Append(_scene ? "true" : "false").Append(",\"geometry\":").Append(_geometry ? "true" : "false")
                .Append(",\"ground\":").Append(_placed ? "true" : "false").Append(",\"initialSync\":").Append(_manifest && _ended && _count == Expected.Count ? "true" : "false").Append('}').ToString();
        }
        private static string Reason() => !Visible ? State.Reason : !_scene ? JoinLoadingState.Names[State.Stage] : Mp.Client != null && !Mp.Client.SpotPlaced ? (DebugBridge.TpBusy ? "Waiting for ground at the join position" : "Waiting for the host's join position") : !_geometry ? "Waiting for terrain and roads" : !_manifest ? "Waiting for the host's initial object list" : !_ended ? "Waiting for the host to finish initial object sync" : _count != Expected.Count ? "Waiting for shared objects to be placed" : "Waiting for settled ground";
        internal static void Draw()
        {
            if (!Visible) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 20, richText = false, wordWrap = true };
                _title = new GUIStyle(_style) { fontSize = 30 };
            }
            int depth = GUI.depth; GUI.depth = -10000;
            GUI.color = new Color(.055f, .065f, .055f, 1);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture); GUI.color = Color.white;
            float scale = Mathf.Min(1, Screen.height / 720f, Screen.width / 800f);
            var matrix = GUI.matrix; GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * scale);
            float x = (Screen.width / scale - 720) / 2, y = (Screen.height / scale - 620) / 2;
            GUI.Label(new Rect(x, y, 720, 45), "Joining the host's world", _title);
            string host = Mp.Client?.HostName;
            if (host == null && Session.LastJoinSteamId != 0) host = Session.Persona(new Steamworks.CSteamID(Session.LastJoinSteamId));
            GUI.Label(new Rect(x, y + 48, 720, 35), host ?? "Host", _style);
            for (int i = 0; i < 7; i++)
            {
                string marker = i < State.Stage ? "✓" : i == State.Stage ? "|/-\\"[(int)(Time.realtimeSinceStartup * 5) % 4].ToString() : "·";
                string text = marker + "  " + JoinLoadingState.Names[i];
                if (i == 4 && _manifest) text += $": {State.Count:N0} of {State.Total:N0}";
                if (i == 1 && Mp.Client?.LoadingMod != null) text += $": {Mp.Client.LoadingMod} ({Mp.Client.LoadingModBytes:N0} of {Mp.Client.LoadingModTotal:N0} bytes)";
                GUI.Label(new Rect(x, y + 100 + i * 36, 720, 36), text, _style);
            }
            float progress = State.Stage + (State.Stage == 4 && State.Total > 0 ? (float)State.Count / State.Total : 0);
            GUI.Label(new Rect(x, y + 360, 720, 35), $"{progress * 100 / 6:F0}% — {Reason()}", _style);
            string question = MpScreen.PendingQuestion;
            if (question != null)
            {
                float height = Mathf.Max(90, _style.CalcHeight(new GUIContent(question), 695));
                _questionScroll = GUI.BeginScrollView(new Rect(x, y + 400, 720, 90), _questionScroll, new Rect(0, 0, 695, height));
                GUI.Label(new Rect(0, 0, 695, height), question, _style); GUI.EndScrollView();
                if (GUI.Button(new Rect(x, y + 495, 180, 38), "Download mods")) MpScreen.AskProbe("yes");
                if (GUI.Button(new Rect(x + 200, y + 495, 180, 38), "Decline")) MpScreen.AskProbe("no");
            }
            if (State.Stuck(Time.realtimeSinceStartupAsDouble))
            {
                if (question == null) GUI.Label(new Rect(x, y + 400, 720, 80), "No progress for 60 seconds. " + Reason() + ". You can wait, cancel, or continue anyway.", _style);
                if (GUI.Button(new Rect(x + 220, y + 560, 200, 40), "Continue anyway")) Probe("continue");
            }
            if (GUI.Button(new Rect(x, y + 560, 200, 40), "Cancel (Esc)")) Cancel();
            GUI.matrix = matrix; GUI.depth = depth;
            if (Event.current.isMouse) Event.current.Use();
        }
    }

    [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Move))]
    internal static class JoinLoadingMovement
    {
        [HarmonyPrefix]
        private static bool Prefix() => !JoinLoading.Visible;
    }
}
