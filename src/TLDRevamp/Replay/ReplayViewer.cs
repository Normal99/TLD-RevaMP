using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using TLDRevamp.Cinematic;

namespace TLDRevamp.Replay
{
    public enum ReplayCamera { Free, Orbit, Eyes, Follow }

    /// Plays a recording back (issue #36). The live world stays exactly where it is: every recorded object is drawn as
    /// a copy at its recorded pose, the game's own renderers of those objects are hidden while the copy stands in for
    /// them, and recorded players are drawn with the multiplayer body. The view is the cinematic camera's, with two
    /// views of the viewer's own (through a player's eyes, and following them).
    ///
    /// Leaving puts everything back: the copies are destroyed, every hidden renderer gets its own flag back, and the
    /// camera and the player's controls are returned. Nothing is sent over the network, and the player's own
    /// character never moves.
    internal static class ReplayViewer
    {
        private sealed class Body
        {
            public GameObject Go;
            public mpplayerscript Mp;
            public Vector3 OffsetPos;   // the animation root relative to the body's root
            public Quaternion OffsetRot = Quaternion.identity;
            public bool Shown = true;
        }

        private static ReplayRecording _rec;
        private static ReplayRecorder.Take _take;
        private static readonly List<ReplayProxy> _proxies = new List<ReplayProxy>();
        private static readonly List<bool> _piecesValid = new List<bool>();
        private static readonly List<ReplayLook> _ownLooks = new List<ReplayLook>();   // looks built for this playback only
        private static readonly List<Body> _bodies = new List<Body>();
        private static readonly ReplayClock _clock = new ReplayClock();
        private static ReplayCamera _camera = ReplayCamera.Follow;
        private static int _subject;
        private static bool _openedCamera, _oldShowStatus, _oldArrowLook;
        private static string _name = "";
        private static int _matched, _fromPrefab, _missing;
        private static readonly List<MeshRenderer> _atEyes = new List<MeshRenderer>();
        private const float AtEyes = 0.1f, AtEyesSize = 0.5f;   // a head-sized piece at the eyes; a steering wheel or a dashboard sits further out
        private static Vector3 _followPos;
        private static bool _followHave;
        private static Vector3 _playerAtStart;

        internal static bool Active => _rec != null;
        internal static bool Uses(ReplayRecorder.Take take) => Active && _take == take;
        internal static bool ShowUi = true;

        // ------------------------------------------------------------------ start and end

        /// Plays the recording just made (still in memory, with its looks), or else the newest file.
        internal static string PlayLatest()
        {
            if (ReplayRecorder.Last != null) return Play(ReplayRecorder.Last.Recording, ReplayRecorder.Last, ReplayRecorder.Last.File ?? "last");
            var files = ReplayFiles.List(ReplayRecorder.Folder);
            return files.Count == 0 ? Error("there is no recording yet") : PlayFile(Path.GetFileName(files[0]));
        }

        /// `fromDisk` reads the file even when it is the recording just made (the test of a later session's playback).
        internal static string PlayFile(string name, bool fromDisk = false)
        {
            if (!ReplayFiles.SafeName(name)) return Error("not a replay file name");
            if (ReplayRecorder.Saving) return Error("the recording is still being saved");
            if (!fromDisk && ReplayRecorder.Last != null && ReplayRecorder.Last.File == name)
                return Play(ReplayRecorder.Last.Recording, ReplayRecorder.Last, name);
            string path = Path.Combine(ReplayRecorder.Folder, name);
            if (!File.Exists(path)) return Error("no such recording");
            ReplayRecording rec;
            try { rec = ReplayCodec.ReadFile(path); }
            catch (Exception e) { return Error("the recording could not be read: " + e.Message); }
            return Play(rec, null, name);
        }

        /// Plays the recording `step` places older (+1) or newer (-1) than the one playing.
        internal static string PlayNeighbour(int step)
        {
            var files = ReplayFiles.List(ReplayRecorder.Folder);
            if (files.Count == 0) return Error("there is no recording yet");
            int at = -1;
            for (int i = 0; i < files.Count; i++) if (Path.GetFileName(files[i]) == _name) at = i;
            int next = Mathf.Clamp(at < 0 ? 0 : at + step, 0, files.Count - 1);
            if (next == at) return Status();
            return PlayFile(Path.GetFileName(files[next]));
        }

        private static string Play(ReplayRecording rec, ReplayRecorder.Take take, string name)
        {
            if (ReplayRecorder.Recording) return Error("stop recording first");
            var player = mainscript.s != null ? mainscript.s.player : null;
            if (player == null || savedatascript.s == null || DataFromMenuScript.s != null && DataFromMenuScript.s.mainmenu) return Error("load a world first");
            if (rec == null || rec.Duration <= 0) return Error("the recording is empty");
            if (Active) End("another recording");

            bool wasOn = CinematicCamera.Active;
            string camera = wasOn ? CinematicCamera.Status() : CinematicCamera.SetOn(CinematicMode.Free, "replay");
            if (!CinematicCamera.Active) return Error("the camera cannot open here: " + CinematicCamera.LastNotice);
            _openedCamera = !wasOn;

            try { return Begin(rec, take, name, player); }
            catch (Exception e)
            {
                // half-built: everything taken so far goes back
                Plugin.Log?.LogWarning("Replay could not start: " + e);
                if (Active) End("could not start");
                else if (_openedCamera && CinematicCamera.Active) { CinematicCamera.SetOff("replay"); _openedCamera = false; }
                return Error("the replay could not start: " + e.GetType().Name);
            }
        }

        private static string Begin(ReplayRecording rec, ReplayRecorder.Take take, string name, fpscontroller player)
        {
            _rec = rec; _name = name; _playerAtStart = player.transform.position;
            bool sameWorld = take != null && take.Scene == UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            _take = sameWorld ? take : null;
            Fixes.TinyRendererCull.Pause(true);
            BuildCopies(sameWorld ? take : null);
            BuildBodies();
            // The live stand-ins the copies replace: the player's own body and the other players' bodies go too.
            ReplayHider.HideAll(player.transform);
            foreach (var kv in Net.RemotePlayers.ShownBodies()) ReplayHider.HideAll(kv.Value);

            _oldShowStatus = CinematicCamera.ShowStatus; _oldArrowLook = CinematicCamera.ArrowLook;
            CinematicCamera.ShowStatus = false; CinematicCamera.ArrowLook = false;
            _clock.Reset(rec.Duration);
            _subject = 0; _followHave = false;
            SetCamera(ReplayCamera.Follow);
            Apply();
            Plugin.Log?.LogInfo("Replay playing " + name + ": " + rec.Objects.Count + " objects (" + _matched + " live, " + _fromPrefab + " from the item, " + _missing + " not found), " + rec.Players.Count + " players");
            return Status();
        }

        internal static string End(string reason = "ended")
        {
            if (!Active) return Status();
            foreach (var p in _proxies) p.Dispose();
            _proxies.Clear(); _piecesValid.Clear();
            foreach (var b in _bodies) { if (b.Mp != null) Net.RemotePlayers.Bodies.Remove(b.Mp); if (b.Go != null) UnityEngine.Object.Destroy(b.Go); }
            _bodies.Clear();
            foreach (var l in _ownLooks) l.Dispose();
            _ownLooks.Clear();
            int restored = ReplayHider.Restore();
            Fixes.TinyRendererCull.Pause(false);
            CinematicCamera.Override = false; CinematicCamera.CursorFree = false;
            CinematicCamera.ShowStatus = _oldShowStatus; CinematicCamera.ArrowLook = _oldArrowLook;
            if (_openedCamera && CinematicCamera.Active) CinematicCamera.SetOff("replay");
            _openedCamera = false;
            _rec = null; _take = null;
            Plugin.Log?.LogInfo("Replay " + reason + " (" + restored + " renderers given back)");
            return Status();
        }

        // ------------------------------------------------------------------ copies

        private static void BuildCopies(ReplayRecorder.Take take)
        {
            _matched = _fromPrefab = _missing = 0;
            for (int i = 0; i < _rec.Objects.Count; i++)
            {
                var o = _rec.Objects[i];
                ReplayLook look = null;
                if (take != null && i < take.Objects.Count) { look = take.Objects[i].Look; _matched++; }
                else look = Resolve(o);
                var proxy = ReplayProxy.Build(look ?? new ReplayLook(), i);
                _proxies.Add(proxy);
                _piecesValid.Add(look != null && look.Pieces.Count == o.PieceCount);
                if (look == null) continue;
                foreach (var p in look.Pieces) if (p.Source != null) ReplayHider.Hide(p.Source);
                if (!look.FromPrefab) foreach (var l in look.Lights) ReplayHider.Darken(l);
            }
        }

        /// A recording from an earlier session: the same object in the same save if it is here (same save id and
        /// item), otherwise the item's own default look.
        private static ReplayLook Resolve(ReplayObject o)
        {
            if (savedatascript.s.items.TryGetValue(o.SaveId, out var it) && it != null && it.id == o.Prefab)
            {
                var look = ReplayLook.Capture(it.transform, ReplayRecorder.Nested);
                _ownLooks.Add(look); _matched++;
                return look;
            }
            var db = itemdatabase.s;
            if (db != null && db.items != null && o.Prefab >= 0 && o.Prefab < db.items.Length && db.items[o.Prefab] != null)
            {
                var root = db.items[o.Prefab].transform;
                var look = ReplayLook.Capture(root, t => t.GetComponent<tosaveitemscript>() != null, fromPrefab: true);
                _ownLooks.Add(look); _fromPrefab++;
                return look;
            }
            _missing++;
            return null;
        }

        private static void BuildBodies()
        {
            var prefab = syncScript.s != null ? syncScript.s.playerPrefab : null;
            for (int i = 0; i < _rec.Players.Count; i++)
            {
                var rp = _rec.Players[i];
                var b = new Body();
                if (prefab != null)
                {
                    b.Go = UnityEngine.Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
                    b.Go.hideFlags = HideFlags.HideAndDontSave;
                    foreach (var ik in b.Go.GetComponentsInChildren<IKscript>()) ik.FStart();
                    foreach (var rb in b.Go.GetComponentsInChildren<Rigidbody>(true)) rb.isKinematic = true;
                    foreach (var cc in b.Go.GetComponentsInChildren<CharacterController>(true)) cc.enabled = false;
                    foreach (var col in b.Go.GetComponentsInChildren<Collider>(true)) col.enabled = false;
                    b.Mp = b.Go.GetComponent<mpplayerscript>();
                    if (b.Mp != null)
                    {
                        Net.RemotePlayers.Bodies.Add(b.Mp);   // keeps the prefab from turning its colliders back on
                        var tag = b.Go.GetComponentInChildren<helptextscript>();
                        if (tag != null) tag.Text = new[] { "" };
                        Quiet(b.Mp);
                    }
                    var outfit = b.Go.GetComponent<playermodeloutfitscript>();
                    if (outfit != null)
                    {
                        outfit.isMultiplayer = true;
                        // SetData only stores the numbers: Refresh is what shows the one character and its clothes. Without
                        // it the prefab drew both characters, male and female, in each other.
                        try { if (rp.Outfit >= 0 && rp.OutfitEnabled.Length > 0) outfit.SetData(rp.Outfit, rp.OutfitEnabled, Colors(rp.OutfitColors)); }
                        catch (Exception e) { Plugin.Log?.LogWarning("Replay: player " + i + "'s outfit does not fit this game's characters: " + e.Message); }
                        outfit.Refresh(fromMultiplayer: true);
                    }
                    var a = b.Mp != null ? b.Mp.anim : null;
                    var T = a != null && a.T != null ? a.T : (a != null ? a.transform : b.Go.transform);
                    b.OffsetRot = Quaternion.Inverse(b.Go.transform.rotation) * T.rotation;
                    b.OffsetPos = Quaternion.Inverse(b.Go.transform.rotation) * (T.position - b.Go.transform.position);
                }
                else
                {
                    b.Go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    b.Go.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.Object.Destroy(b.Go.GetComponent<Collider>());
                }
                b.Go.name = "TLDRevampReplayPlayer" + i;
                _bodies.Add(b);
            }
        }

        /// What live multiplayer sets on every pose and every frame, and a replay body never got: the prefab's four menu
        /// cards (pause, multiplayer, dev menu, tray) are all on, flat in front of the face (a dark card over the head),
        /// until mainscript.SetMenus turns them off; and the mouth sphere keeps its prefab size (a red ball at the
        /// head) until mpplayerscript.Upd sizes it to the closed mouth. The recording has no menus and no voice, so
        /// the cards go off and the mouth stays closed.
        private static void Quiet(mpplayerscript mp)
        {
            if (mp.GMenus != null && mainscript.s != null) mainscript.s.SetMenus(0, mp.GMenus);
            if (mp.lipT != null && mp.lipC != null)
            {
                mp.lipCurrent = mp.lipTarget = 0f;
                mp.lipT.SetPositionAndRotation(mp.lipC.position, mp.lipC.rotation);
                mp.lipT.localScale = mp.lipC.localScale;
            }
        }

        private static Color[] Colors(float[] c)
        {
            var colors = new Color[c.Length / 4];
            for (int i = 0; i < colors.Length; i++) colors[i] = new Color(c[i * 4], c[i * 4 + 1], c[i * 4 + 2], c[i * 4 + 3]);
            return colors;
        }

        // ------------------------------------------------------------------ per frame

        internal static void Tick()
        {
            if (!Active) return;
            try
            {
                if (mainscript.s == null || mainscript.s.player == null || !CinematicCamera.Active) { End("the camera closed"); return; }
                Keys();
                if (!Active) return;
                _clock.Advance(Time.unscaledDeltaTime);
                Apply();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Replay viewer: " + e);
                End("an error stopped it");
            }
        }

        private static void Apply()
        {
            float t = (float)_clock.Time;
            var origin = new Vector3d(_rec.OriginX, _rec.OriginY, _rec.OriginZ);
            foreach (var r in _atEyes) if (r != null) r.enabled = true;
            _atEyes.Clear();
            for (int i = 0; i < _proxies.Count; i++)
            {
                var proxy = _proxies[i];
                var o = _rec.Objects[i];
                if (!o.Track.Sample(t, out var k)) { proxy.Show(false); continue; }
                if (k.Parent != proxy.Parent)
                {
                    var parent = k.Parent >= 0 ? _proxies[k.Parent].Root.transform : null;
                    // a parent can never hang under its own child (keys from two moments disagreeing): world instead
                    if (parent != null && parent.IsChildOf(proxy.Root.transform)) { parent = null; k.Parent = -1; }
                    proxy.Root.transform.SetParent(parent, false);
                    proxy.Parent = k.Parent;
                }
                if (k.Parent >= 0) { var rt = proxy.Root.transform; rt.localPosition = V(k.Pos); rt.localRotation = Q(k.Rot); }
                else proxy.Root.transform.SetPositionAndRotation(World(origin, k.Pos), Q(k.Rot));
                proxy.Show(k.Visible);
                if (k.Visible) proxy.SetLights(o.LightCount == proxy.Lights.Length ? k.Lights : 0);
                if (!k.Visible || !_piecesValid[i]) continue;
                for (int p = 0; p < o.Pieces.Length && p < proxy.Pieces.Length; p++)
                {
                    var track = o.Pieces[p];
                    if (track == null || !track.Sample(t, out var pk)) continue;
                    var pt = proxy.Pieces[p]; pt.localPosition = V(pk.Pos); pt.localRotation = Q(pk.Rot);
                }
            }
            if (_camera == ReplayCamera.Eyes && SubjectSample(t, out var es)) LeaveOutAtEyes(World(origin, es.Eye));
            for (int i = 0; i < _bodies.Count; i++)
            {
                var b = _bodies[i];
                bool on = _rec.Players[i].Sample(t, out var s) && !(i == _subject && _camera == ReplayCamera.Eyes);
                if (b.Shown != on) { b.Shown = on; b.Go.SetActive(on); }
                if (on) Pose(b, s, origin);
            }
            Camera(t, origin);
        }

        /// Through the eyes, what the player wore on the head (a mask, a hat; the game draws it on a layer its own
        /// camera leaves out) would fill the screen: the small pieces sitting on the eyes are left out of that view,
        /// for this frame only.
        private static void LeaveOutAtEyes(Vector3 eye)
        {
            foreach (var proxy in _proxies)
            {
                if (!proxy.Shown || proxy.Root == null || (proxy.Root.transform.position - eye).magnitude > proxy.Reach + AtEyes) continue;
                for (int p = 0; p < proxy.Pieces.Length; p++)
                {
                    var r = proxy.Renderers[p];
                    float radius = proxy.Radii[p];
                    if (r == null || !r.enabled || radius > AtEyesSize) continue;
                    if ((proxy.Pieces[p].TransformPoint(proxy.Centers[p]) - eye).magnitude < radius + AtEyes) { r.enabled = false; _atEyes.Add(r); }
                }
            }
        }

        private static void Pose(Body b, PlayerSample s, Vector3d origin)
        {
            Vector3 at = World(origin, s.Body); Quaternion rot = Q(s.BodyRot);
            var rootRot = rot * Quaternion.Inverse(b.OffsetRot);
            b.Go.transform.SetPositionAndRotation(at - rootRot * b.OffsetPos, rootRot);
            var a = b.Mp != null ? b.Mp.anim : null;
            if (a == null) return;
            a.gotUpd = false;   // the game's own pose easing stays out of it: the targets are placed here
            for (int i = 0; i < ReplayLimbs.Count; i++)
            {
                var target = ReplayRecorder.Limb(a, i);
                if (target != null) target.SetPositionAndRotation(at + rot * V(s.Limb(i)), rot * Q(s.LimbRot(i)));
            }
            if (a.Th != null) { a.Th.position = a.HHTarget != null ? a.HHTarget.position : at; a.Th.localRotation = Quaternion.Euler(s.HeadPitch, 0f, 0f); }
        }

        // ------------------------------------------------------------------ camera

        private static void SetCamera(ReplayCamera mode)
        {
            _camera = mode;
            CinematicCamera.Override = mode == ReplayCamera.Eyes || mode == ReplayCamera.Follow;
            CinematicCamera.SwitchMode(mode == ReplayCamera.Orbit ? CinematicMode.Orbit : CinematicMode.Free);
            if (mode == ReplayCamera.Orbit) CinematicCamera.State.Radius = CinematicLimits.ClampRadius(6f);
            _followHave = false;
        }

        private static bool SubjectSample(float t, out PlayerSample s)
        {
            s = default;
            return _subject >= 0 && _subject < _rec.Players.Count && _rec.Players[_subject].Sample(t, out s);
        }

        private static void Camera(float t, Vector3d origin)
        {
            if (!SubjectSample(t, out var s)) return;   // the watched player is not there right now: the view holds
            Vector3 body = World(origin, s.Body);
            switch (_camera)
            {
                case ReplayCamera.Eyes:
                    CinematicCamera.OverridePosition = World(origin, s.Eye);
                    CinematicCamera.OverrideRotation = Q(s.EyeRot);
                    break;
                case ReplayCamera.Follow:
                {
                    var yaw = Quaternion.Euler(0f, Q(s.BodyRot).eulerAngles.y, 0f);
                    Vector3 want = body + yaw * new Vector3(0f, 2.2f, -5f);
                    float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f) * Mathf.Max(0.25f, _clock.Speed);
                    _followPos = _followHave && (_followPos - want).sqrMagnitude < 400f ? Vector3.Lerp(_followPos, want, 1f - Mathf.Exp(-dt * 4f)) : want;
                    _followHave = true;
                    var look = body + Vector3.up * 1.2f - _followPos;
                    CinematicCamera.OverridePosition = _followPos;
                    if (look.sqrMagnitude > 1e-4f) CinematicCamera.OverrideRotation = Quaternion.LookRotation(look.normalized, Vector3.up);
                    break;
                }
                case ReplayCamera.Orbit:
                    CinematicCamera.FollowTarget(body + Vector3.up * 1.2f);
                    break;
            }
        }

        // ------------------------------------------------------------------ keys

        private static void Keys()
        {
            CinematicCamera.CursorFree = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (Net.Chat.Typing || MarkPanel.Opened) return;
            if (Input.GetKeyDown(KeyCode.Space)) _clock.Toggle();
            if (Input.GetKeyDown(KeyCode.LeftArrow)) _clock.Jump(-ReplayLimits.JumpSeconds);
            if (Input.GetKeyDown(KeyCode.RightArrow)) _clock.Jump(ReplayLimits.JumpSeconds);
            if (Input.GetKeyDown(KeyCode.UpArrow)) _clock.Faster();
            if (Input.GetKeyDown(KeyCode.DownArrow)) _clock.Slower();
            if (Input.GetKeyDown(KeyCode.Home)) _clock.Seek(0);
            if (Input.GetKeyDown(KeyCode.Alpha1)) SetCamera(ReplayCamera.Free);
            if (Input.GetKeyDown(KeyCode.Alpha2)) SetCamera(ReplayCamera.Orbit);
            if (Input.GetKeyDown(KeyCode.Alpha3)) SetCamera(ReplayCamera.Eyes);
            if (Input.GetKeyDown(KeyCode.Alpha4)) SetCamera(ReplayCamera.Follow);
            if (Input.GetKeyDown(KeyCode.Tab)) NextSubject();
            if (Input.GetKeyDown(KeyCode.H)) ShowUi = !ShowUi;
            if (Input.GetKeyDown(KeyCode.PageDown)) { PlayNeighbour(1); return; }
            if (Input.GetKeyDown(KeyCode.PageUp)) { PlayNeighbour(-1); return; }
            if (Input.GetKeyDown(KeyCode.Backspace)) End("closed");
        }

        private static void NextSubject()
        {
            int n = _rec.Players.Count;
            for (int step = 1; step <= n; step++)
            {
                int i = (_subject + step) % n;
                if (_rec.Players[i].Sample((float)_clock.Time, out _)) { _subject = i; _followHave = false; return; }
            }
        }

        // ------------------------------------------------------------------ the bridge's commands

        internal static string Pause() { if (Active) _clock.Playing = false; return Status(); }
        internal static string Resume() { if (Active) _clock.Playing = true; return Status(); }
        internal static string Toggle() { if (Active) _clock.Toggle(); return Status(); }
        internal static string Seek(double t) { if (!Active) return Error("no replay is playing"); _clock.Seek(t); Apply(); return Status(); }
        internal static string Jump(double s) { if (!Active) return Error("no replay is playing"); _clock.Jump(s); Apply(); return Status(); }
        internal static string Speed(float x)
        {
            if (!Active) return Error("no replay is playing");
            return _clock.SetSpeed(x) ? Status() : Error("speed is one of 0.1, 0.25, 0.5, 1, 2, 4");
        }
        internal static string Camera(string mode)
        {
            if (!Active) return Error("no replay is playing");
            switch (mode)
            {
                case "free": SetCamera(ReplayCamera.Free); break;
                case "orbit": SetCamera(ReplayCamera.Orbit); break;
                case "eyes": SetCamera(ReplayCamera.Eyes); break;
                case "follow": SetCamera(ReplayCamera.Follow); break;
                default: return Error("camera is free, orbit, eyes or follow");
            }
            Apply();
            return Status();
        }
        internal static string Subject(int i)
        {
            if (!Active) return Error("no replay is playing");
            if (i < 0 || i >= _rec.Players.Count) return Error("no such player");
            _subject = i; _followHave = false; Apply(); return Status();
        }

        /// Debugging the look of a view: the copied pieces within `range` of the camera, nearest first (object, item,
        /// mesh and material names, layer, size). Read-only.
        internal static string Near(float range)
        {
            if (!Active) return Error("no replay is playing");
            Vector3 eye = CinematicCamera.Override ? CinematicCamera.OverridePosition : mainscript.UnityPosFromGlobal(CinematicCamera.GlobalPosition());
            var found = new List<KeyValuePair<float, string>>();
            int playerModel = 0;   // pieces drawn with the game's player-model shader (a player's own model)
            for (int i = 0; i < _proxies.Count; i++)
            {
                var proxy = _proxies[i];
                if (proxy.Root == null || !proxy.Root.activeInHierarchy) continue;
                foreach (var t in proxy.Pieces)
                {
                    var mr = t.GetComponent<MeshRenderer>(); var mf = t.GetComponent<MeshFilter>();
                    if (mr == null || !mr.enabled) continue;
                    float d = (mr.bounds.center - eye).magnitude;
                    if (d > range) continue;
                    var m = mr.sharedMaterial;
                    if (m != null && m.shader != null && m.shader.name.IndexOf("playermodel", StringComparison.OrdinalIgnoreCase) >= 0) playerModel++;
                    found.Add(new KeyValuePair<float, string>(d, "{\"d\":" + Json.Num(d, "F2") + ",\"object\":" + i + ",\"item\":" + _rec.Objects[i].Prefab +
                        ",\"mesh\":" + Json.Str(mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "") + ",\"material\":" + Json.Str(m != null ? m.name : "") +
                        ",\"shader\":" + Json.Str(m != null && m.shader != null ? m.shader.name : "") + ",\"layer\":" + t.gameObject.layer +
                        ",\"shadows\":" + (int)mr.shadowCastingMode + ",\"size\":" + Json.Num(mr.bounds.size.magnitude, "F2") + "}"));
                }
            }
            found.Sort((a, b) => a.Key.CompareTo(b.Key));
            var sb = new System.Text.StringBuilder("{\"near\":[");
            for (int i = 0; i < found.Count && i < 16; i++) { if (i > 0) sb.Append(','); sb.Append(found[i].Value); }
            return sb.Append("],\"count\":").Append(found.Count).Append(",\"playerModel\":").Append(playerModel).Append('}').ToString();
        }

        internal static string Status()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"playing\":").Append(Active && _clock.Playing ? "true" : "false");
            sb.Append(",\"active\":").Append(Active ? "true" : "false");
            if (Active)
            {
                sb.Append(",\"name\":").Append(Json.Str(_name));
                sb.Append(",\"time\":").Append(Json.Num(_clock.Time, "F3")).Append(",\"duration\":").Append(Json.Num(_clock.Duration, "F3"));
                sb.Append(",\"speed\":").Append(Json.Num(_clock.Speed, "F2"));
                sb.Append(",\"camera\":").Append(Json.Str(_camera.ToString().ToLowerInvariant())).Append(",\"subject\":").Append(_subject);
                int shown = 0; foreach (var p in _proxies) if (p.Root != null && p.Root.activeInHierarchy) shown++;
                int pieces = 0; foreach (var p in _proxies) pieces += p.Pieces.Length;
                int bodies = 0; foreach (var b in _bodies) if (b.Shown) bodies++;
                sb.Append(",\"copies\":").Append(_proxies.Count).Append(",\"copiesShown\":").Append(shown).Append(",\"atEyes\":").Append(_atEyes.Count).Append(",\"pieces\":").Append(pieces);
                sb.Append(",\"bodiesShown\":").Append(bodies).Append(",\"players\":").Append(_rec.Players.Count);
                sb.Append(",\"live\":").Append(_matched).Append(",\"fromItem\":").Append(_fromPrefab).Append(",\"notFound\":").Append(_missing);
                sb.Append(",\"inMemory\":").Append(_take != null ? "true" : "false");
                // the proof that the player's own character did not move while the replay played
                var pl = mainscript.s != null ? mainscript.s.player : null;
                sb.Append(",\"playerMoved\":").Append(Json.Num(pl != null ? (pl.transform.position - _playerAtStart).magnitude : -1, "F3"));
                if (SubjectSample((float)_clock.Time, out var s))
                {
                    var g = new Vector3d(_rec.OriginX + s.Body.X, _rec.OriginY + s.Body.Y, _rec.OriginZ + s.Body.Z);
                    sb.Append(",\"subjectAt\":[").Append(Json.Num(g.x, "F2")).Append(',').Append(Json.Num(g.y, "F2")).Append(',').Append(Json.Num(g.z, "F2")).Append(']');
                }
            }
            sb.Append(",\"darkLights\":").Append(ReplayHider.DarkCount);
            sb.Append(",\"hidden\":").Append(ReplayHider.Count).Append(",\"stillHidden\":").Append(ReplayHider.StillHidden());
            sb.Append(",\"cullPaused\":").Append(Fixes.TinyRendererCull.Paused ? "true" : "false");
            sb.Append(",\"inputBlocked\":").Append(ReplayInputBlock.Frames);
            sb.Append('}');
            return sb.ToString();
        }

        private static string Error(string why) { Plugin.Instance?.Overlay?.Toast("Replay: " + why, 3f); return "{\"error\":" + Json.Str(why) + "}"; }

        // ------------------------------------------------------------------ helpers

        private static Vector3 World(Vector3d origin, ReplayVec v) => mainscript.UnityPosFromGlobal(new Vector3d(origin.x + v.X, origin.y + v.Y, origin.z + v.Z));
        private static Vector3 V(ReplayVec v) => new Vector3(v.X, v.Y, v.Z);
        private static Quaternion Q(ReplayRot r) => new Quaternion(r.X, r.Y, r.Z, r.W);

        // ------------------------------------------------------------------ timeline

        private static readonly GUIContent _line = new GUIContent(), _help = new GUIContent();
        private static int _lineSecond = -1; private static float _lineSpeed; private static bool _linePlaying; private static ReplayCamera _lineCamera; private static int _lineSubject = -1;
        private static Rect _bar;

        internal static void Draw()
        {
            if (!Active || !ShowUi) return;
            var e = Event.current;
            OverlayStyle.Ensure();
            float s = OverlayStyle.Scale, w = Mathf.Min(900f * s, Screen.width - 32f * s), h = 64f * s;
            var r = new Rect((Screen.width - w) / 2f, Screen.height - h - 16f * s, w, h);
            _bar = new Rect(r.x + OverlayStyle.Padding, r.y + h - 18f * s, w - 2 * OverlayStyle.Padding, 8f * s);
            if (CinematicCamera.CursorFree && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0)
            {
                var hit = new Rect(_bar.x, _bar.y - 8f * s, _bar.width, _bar.height + 16f * s);
                if (hit.Contains(e.mousePosition)) { Seek((e.mousePosition.x - _bar.x) / Mathf.Max(1f, _bar.width) * _clock.Duration); e.Use(); }
            }
            if (e.type != EventType.Repaint) return;
            int second = (int)_clock.Time;
            if (second != _lineSecond || _lineSpeed != _clock.Speed || _linePlaying != _clock.Playing || _lineCamera != _camera || _lineSubject != _subject)
            {
                _lineSecond = second; _lineSpeed = _clock.Speed; _linePlaying = _clock.Playing; _lineCamera = _camera; _lineSubject = _subject;
                string who = _subject == 0 && _rec.Players.Count > 0 && _rec.Players[0].Local ? "you" : "player " + (_subject + 1);
                _line.text = "Replay  " + (_clock.Playing ? "playing" : "paused") + "   " + ReplayClock.Clock(_clock.Time) + " / " + ReplayClock.Clock(_clock.Duration) +
                             "   " + _clock.Speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×   " + _camera.ToString().ToLowerInvariant() + " view   watching " + who;
                _help.text = "Space pause · ← → 5 s · ↑ ↓ speed · 1 free 2 orbit 3 eyes 4 follow · Tab player · Alt+click seek · H hide · PgUp/PgDn replays · Backspace exit";
            }
            OverlayStyle.Back(r);
            OverlayStyle.Label(new Rect(r.x + OverlayStyle.Padding, r.y + 6f * s, w - 2 * OverlayStyle.Padding, 20f * s), _line, OverlayKind.Accent);
            OverlayStyle.Label(new Rect(r.x + OverlayStyle.Padding, r.y + 26f * s, w - 2 * OverlayStyle.Padding, 18f * s), _help, OverlayKind.Muted, OverlayStyle.Small);
            OverlayStyle.Progress(_bar, _clock.Duration > 0 ? (float)(_clock.Time / _clock.Duration) : 0f, OverlayKind.Accent);
        }
    }

    /// Takes every input from the player while a replay plays: the keys the replay and its camera use (W A S D, Space,
    /// the arrows, the number keys) must not also walk, jump, switch the inventory or drive the car under the player.
    /// The movement axes go to the camera instead. Runs right after the game's input script fills its fields, so the
    /// player, the car and everything else read zeroes this frame. The brake, the handbrake and the clutch are left as
    /// they are: a car the player sits in must stay held, not roll away while the replay plays.
    [HarmonyPatch(typeof(inputscript), "Update")]
    internal static class ReplayInputBlock
    {
        internal static long Frames;
        private static readonly string[] Names =
        {
            "lookX", "lookY", "forward", "backward", "left", "right", "scroll", "scrollOutside", "lmb", "rmb", "mmb", "target", "map", "run", "duck",
            "jump", "lean", "push", "use", "pickup", "mount", "glue", "cam", "burp", "piss", "reload", "sleep", "zoom", "i1", "i2", "i3", "i4",
            "ignition", "hum", "smoke", "horn", "lights", "point", "removesunglasses", "hidehudDown", "steer", "throttle",
            "handbrakeBDown", "grDown", "gnDown", "g1Down", "g2Down", "g3Down", "g4Down", "g5Down", "g6Down", "upshiftDown", "downshiftDown",
            "keyup", "keydown", "keyleft", "keyright",
        };
        private static AccessTools.FieldRef<inputscript, bool>[] _bools;
        private static AccessTools.FieldRef<inputscript, float>[] _floats;

        /// The fields, found once by name (a name this game version does not have is skipped) and written through
        /// field references, so blocking costs no allocation per frame.
        private static void Find()
        {
            if (_bools != null) return;
            var bools = new List<AccessTools.FieldRef<inputscript, bool>>(); var floats = new List<AccessTools.FieldRef<inputscript, float>>();
            var seen = new HashSet<string>();
            foreach (var n in Names)
                foreach (var suffix in new[] { "", "Up", "Down" })
                {
                    var f = AccessTools.Field(typeof(inputscript), n + suffix);
                    if (f == null || f.IsStatic || !seen.Add(f.Name)) continue;
                    if (f.FieldType == typeof(bool)) bools.Add(AccessTools.FieldRefAccess<inputscript, bool>(f.Name));
                    else if (f.FieldType == typeof(float)) floats.Add(AccessTools.FieldRefAccess<inputscript, float>(f.Name));
                }
            _floats = floats.ToArray(); _bools = bools.ToArray();
        }

        [HarmonyPostfix]
        private static void Postfix(inputscript __instance)
        {
            if (!ReplayViewer.Active || __instance == null) return;
            Find();
            CinematicInput.Forward = __instance.forward; CinematicInput.Backward = __instance.backward;
            CinematicInput.Left = __instance.left; CinematicInput.Right = __instance.right;
            CinematicInput.SpeedFactor = CinematicInput.SpeedFactorFor(__instance.run, __instance.duck);
            foreach (var f in _bools) f(__instance) = false;
            foreach (var f in _floats) f(__instance) = 0f;
            Frames++;
        }
    }
}
