using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace TLDRevamp.Replay
{
    /// Records gameplay for playback (issue #36): every player's body and every car, part and item near a player,
    /// 20 times a second, into a ReplayRecording (ReplayCore.cs).
    ///
    /// It only reads. Nothing in the world is moved, created or sent over the network; it works the same in single
    /// player and multiplayer. While it records, an object's look (its meshes and materials) is captured the first
    /// time it is seen, so the recording just made can be played straight away with the objects exactly as they were.
    internal static class ReplayRecorder
    {
        internal sealed class Tracked
        {
            public tosaveitemscript It;
            public Transform T;
            public ReplayLook Look;
            public bool Active;
        }

        /// A finished recording with the looks captured while it ran, kept for playback in the same world.
        internal sealed class Take
        {
            public ReplayRecording Recording;
            public List<Tracked> Objects;
            public int Scene;
            public string File;
        }

        private static ReplayRecording _rec;
        private static readonly List<Tracked> _objects = new List<Tracked>();
        private static readonly Dictionary<int, int> _byInstance = new Dictionary<int, int>();   // tosaveitemscript instance id -> index
        private static readonly Dictionary<Transform, int> _byRoot = new Dictionary<Transform, int>();
        private static readonly Dictionary<int, int> _remoteIndex = new Dictionary<int, int>();   // remote player id -> player index
        private static HashSet<int> _seen = new HashSet<int>(), _seenNext = new HashSet<int>();
        private static readonly List<Vector3> _playerPositions = new List<Vector3>();
        private static double _start, _next, _nextScan;
        private static int _scene, _pieceCursor, _scans;
        private static fpscontroller _player;
        private static Task _save;
        private static volatile bool _failed;
        private static string _lastFile = "", _stopReason = "";
        private static long _ticks, _calls, _max, _gcTicks, _maxGc;
        private static float _maxAt;
        private static readonly long[] _phase = new long[4], _maxPhase = new long[4];   // players, scan, objects, pieces

        internal static Take Last;
        internal static bool Recording => _rec != null;
        internal static bool Saving => _save != null && !_save.IsCompleted;
        internal static string Folder => Path.Combine(BepInEx.Paths.BepInExRootPath, "tldrevamp-replays");

        internal static string Start()
        {
            if (Recording) return Status();
            if (ReplayViewer.Active) return Error("end the replay before recording");
            if (Saving) return Error("the last recording is still being saved");
            var p = mainscript.s != null ? mainscript.s.player : null;
            if (p == null || savedatascript.s == null) return Error("load a world first");
            if (DataFromMenuScript.s != null && DataFromMenuScript.s.mainmenu) return Error("load a world first");
            var origin = mainscript.GlobalFromUnityPos(p.transform.position);
            _rec = new ReplayRecording { Protocol = Net.Protocol.Version, OriginX = Math.Round(origin.x), OriginY = Math.Round(origin.y), OriginZ = Math.Round(origin.z) };
            _objects.Clear(); _byInstance.Clear(); _byRoot.Clear(); _remoteIndex.Clear(); _seen.Clear(); _seenNext.Clear();
            _player = p; _scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            _start = Time.realtimeSinceStartupAsDouble; _next = _start; _nextScan = _start; _scans = 0;
            _ticks = _calls = _max = _gcTicks = _maxGc = 0; _maxAt = 0; Array.Clear(_maxPhase, 0, 4); _failed = false; _stopReason = "";
            var me = new ReplayPlayer { Local = true };
            if (Net.PlayerLook.LocalOutfit(out int sel, out bool[] en, out Color[] col)) Outfit(me, sel, en, col);
            _rec.Players.Add(me);
            Tick();
            // mods record what their objects show now (MpApi replay channels): the first key of each
            foreach (var ch in TLDRevamp.Multiplayer.MpApi.Channels) ch.RaiseRecordingStarted();
            Plugin.Instance?.Overlay?.Toast("Replay recording started", 2f);
            Plugin.Log?.LogInfo("Replay recording started");
            return Status();
        }

        internal static string Stop(string reason = "stopped")
        {
            if (!Recording) return Status();
            var rec = _rec; _rec = null; _player = null;
            rec.Duration = (float)Math.Min(ReplayLimits.MaxSeconds, Time.realtimeSinceStartupAsDouble - _start);
            foreach (var p in rec.Players) foreach (var s in p.Samples) if (s.T > rec.Duration) rec.Duration = s.T;
            foreach (var o in rec.Objects) if (o.Track.Keys.Count > 0) rec.Duration = Math.Max(rec.Duration, o.Track.Keys[o.Track.Keys.Count - 1].T);
            if (Last != null && Last.Objects != null && !ReplayViewer.Uses(Last)) foreach (var o in Last.Objects) o.Look?.Dispose();
            Last = new Take { Recording = rec, Objects = new List<Tracked>(_objects), Scene = _scene };
            _objects.Clear(); _byInstance.Clear(); _byRoot.Clear(); _remoteIndex.Clear();
            _stopReason = reason;
            string folder = Folder, name = ReplayFiles.Name(DateTime.Now), path = Path.Combine(folder, name);
            Last.File = name; _lastFile = name;
            _save = Task.Run(() =>
            {
                try { Directory.CreateDirectory(folder); ReplayCodec.WriteFile(path, rec); }
                catch { _failed = true; }
            });
            Plugin.Instance?.Overlay?.Toast("Replay saved: " + ReplayClock.Clock(rec.Duration) + " (" + reason + ")", 3f);
            Plugin.Log?.LogInfo("Replay recording " + reason + ": " + rec.Duration.ToString("F1") + " s, " + rec.Objects.Count + " objects, " + rec.ObjectKeys + " keys");
            return Status();
        }

        internal static string Toggle() => Recording ? Stop() : Start();

        internal static void Tick()
        {
            if (!Recording) return;
            long begin = Stopwatch.GetTimestamp();
            int gcs = GC.CollectionCount(0);
            try
            {
                var p = mainscript.s != null ? mainscript.s.player : null;
                if (p == null || p != _player || savedatascript.s == null || UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle != _scene)
                { Stop("the world closed"); return; }
                double now = Time.realtimeSinceStartupAsDouble;
                if (now < _next) return;
                _next = Math.Max(_next + ReplayLimits.SampleStep, now - ReplayLimits.SampleStep);
                float t = (float)(now - _start);
                long a = Stopwatch.GetTimestamp();
                Players(t);
                long b = Stopwatch.GetTimestamp();
                if (now >= _nextScan) { _nextScan = now + 1.0; Scan(t); }
                long c = Stopwatch.GetTimestamp();
                Objects(t);
                long d = Stopwatch.GetTimestamp();
                Pieces(t);
                long e = Stopwatch.GetTimestamp();
                _phase[0] = b - a; _phase[1] = c - b; _phase[2] = d - c; _phase[3] = e - d;
                _rec.Duration = t;
                if (_rec.Full) Stop("the recording is full");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Replay recorder: " + e.Message);
                Stop("an error stopped it");
            }
            finally
            {
                long elapsed = Stopwatch.GetTimestamp() - begin; _ticks += elapsed; _calls++;
                // A garbage collection that lands in a sample is the whole game's, not the recorder's: counted apart.
                if (GC.CollectionCount(0) != gcs) { _gcTicks++; _maxGc = Math.Max(_maxGc, elapsed); }
                else if (elapsed > _max) { _max = elapsed; _maxAt = (float)(Time.realtimeSinceStartupAsDouble - _start); Array.Copy(_phase, _maxPhase, 4); }
                Array.Clear(_phase, 0, 4);
            }
        }

        // ------------------------------------------------------------------ players

        private static void Players(float t)
        {
            _playerPositions.Clear();
            var pl = _player;
            var me = _rec.Players[0];
            var a = pl.anim;
            var cam = pl.Cam != null ? pl.Cam.transform : null;
            float pitch = pl.Th != null ? pl.Th.localEulerAngles.x : 0f;
            me.Samples.Add(Sample(t, a, !pl.died, cam, pitch, pl.transform));
            _playerPositions.Add(pl.transform.position);
            foreach (var kv in Net.RemotePlayers.ShownBodies())
            {
                if (kv.Value == null) continue;
                if (!_remoteIndex.TryGetValue(kv.Key, out int index))
                {
                    if (_rec.Players.Count >= ReplayLimits.MaxPlayers) continue;
                    index = _rec.Players.Count; _remoteIndex[kv.Key] = index;
                    var rp = new ReplayPlayer();
                    var look = kv.Value.GetComponent<playermodeloutfitscript>();
                    if (look != null) { look.GetData(out int sel, out bool[] en, out Color[] col); Outfit(rp, sel, en, col); }
                    _rec.Players.Add(rp);
                }
                var mp = kv.Value.GetComponent<mpplayerscript>();
                var ra = mp != null ? mp.anim : null;
                float rpitch = ra != null && ra.Th != null ? ra.Th.localEulerAngles.x : 0f;
                _rec.Players[index].Samples.Add(Sample(t, ra, kv.Value.gameObject.activeInHierarchy, ra != null ? ra.Th : null, rpitch, kv.Value));
                _playerPositions.Add(kv.Value.position);
            }
        }

        private static PlayerSample Sample(float t, playeranimationscript a, bool visible, Transform eye, float pitch, Transform fallback)
        {
            var T = a != null ? (a.T != null ? a.T : a.transform) : fallback;
            var s = new PlayerSample { T = t, Visible = visible && T != null, Body = Pos(T.position), BodyRot = Rot(T.rotation), HeadPitch = pitch };
            if (eye != null) { s.Eye = Pos(eye.position); s.EyeRot = Rot(eye.rotation); }
            else { s.Eye = Pos(T.position + Vector3.up * 1.6f); s.EyeRot = s.BodyRot; }
            var inv = Quaternion.Inverse(T.rotation);
            for (int i = 0; i < ReplayLimbs.Count; i++)
            {
                var target = a != null ? Limb(a, i) : null;
                if (target == null) { s.SetLimb(i, default, ReplayRot.Identity); continue; }
                s.SetLimb(i, Vec(inv * (target.position - T.position)), Rot(inv * target.rotation));
            }
            return s;
        }

        internal static Transform Limb(playeranimationscript a, int i)
        {
            switch (i) { case 0: return a.HHTarget; case 1: return a.LHTarget; case 2: return a.RHTarget; case 3: return a.LLTarget; case 4: return a.RLTarget; default: return a.Pelvis; }
        }

        private static void Outfit(ReplayPlayer p, int sel, bool[] en, Color[] col)
        {
            p.Outfit = sel;
            if (en != null && en.Length <= ReplayLimits.MaxOutfitEntries) p.OutfitEnabled = (bool[])en.Clone();
            if (col != null && col.Length <= ReplayLimits.MaxOutfitEntries)
            {
                p.OutfitColors = new float[col.Length * 4];
                for (int i = 0; i < col.Length; i++) { p.OutfitColors[i * 4] = col[i].r; p.OutfitColors[i * 4 + 1] = col[i].g; p.OutfitColors[i * 4 + 2] = col[i].b; p.OutfitColors[i * 4 + 3] = col[i].a; }
            }
        }

        // ------------------------------------------------------------------ which objects

        /// Once a second: objects that came near a player join; tracked ones that went far stop being sampled (they
        /// stay where they were last seen). An object that appears in the world close to a player was spawned during
        /// the recording (bought, dropped from a box), so it is hidden before it was first seen; one first seen at a
        /// distance was there all along.
        private static void Scan(float t)
        {
            var items = savedatascript.s.items;
            _seenNext.Clear();
            int added = 0;
            long begin = Stopwatch.GetTimestamp();
            foreach (var it in items.Values)
            {
                if (it == null) continue;
                int id = it.GetInstanceID();
                _seenNext.Add(id);
                float d = Nearest(it.transform.position);
                if (_byInstance.TryGetValue(id, out int index))
                {
                    var tr = _objects[index];
                    bool keep = ReplayLimits.InScope(d, tr.Active);
                    if (keep && !tr.Active) tr.Active = true;
                    else if (!keep && tr.Active) { tr.Active = false; ResetPieces(index); }
                    continue;
                }
                if (!ReplayLimits.InScope(d, false) || _objects.Count >= ReplayLimits.MaxObjects) continue;
                // Capturing a look costs: a busy place (200 objects) took 16 ms in one frame. Objects join for at most
                // CaptureBudget of each sample and the scan comes back at the next one; an object that joins a few samples
                // late was there all along (before its first key, its first key holds).
                if (added > 0 && Stopwatch.GetTimestamp() - begin > CaptureBudget) { _nextScan = 0; continue; }
                bool spawned = _scans > 0 && !_seen.Contains(id) && d < 50f;
                Add(it, t, spawned);
                added++;
            }
            var swap = _seen; _seen = _seenNext; _seenNext = swap;
            _scans++;
        }

        private static readonly long CaptureBudget = Stopwatch.Frequency * 3 / 1000;   // 3 ms

        private static float Nearest(Vector3 p)
        {
            float best = float.MaxValue;
            foreach (var q in _playerPositions) { float d = (p - q).sqrMagnitude; if (d < best) best = d; }
            return Mathf.Sqrt(best);
        }

        private static void Add(tosaveitemscript it, float t, bool spawned)
        {
            int index = _objects.Count;
            var look = ReplayLook.Capture(it.transform, Nested);
            var tr = new Tracked { It = it, T = it.transform, Look = look, Active = true };
            _objects.Add(tr); _byInstance[it.GetInstanceID()] = index; _byRoot[it.transform] = index;
            var o = new ReplayObject { SaveId = it.idInSave, Prefab = it.id, PieceCount = look.Pieces.Count, LightCount = look.Lights.Count };
            _rec.Objects.Add(o);
            if (spawned && t > ReplayLimits.SampleStep)
            {
                var k = KeyOf(tr, t); k.T = Math.Max(0, t - (float)ReplayLimits.SampleStep); k.Visible = false;
                _rec.ObjectKeys += o.Track.Offer(k);
            }
        }

        /// Whether a child transform starts an object of its own (a part on a car, an item in a box): the game's own
        /// save registry decides, so a decorative child with a stray tosaveitemscript stays part of its parent's look.
        internal static bool Nested(Transform t)
        {
            var it = t.GetComponent<tosaveitemscript>();
            if (it == null || savedatascript.s == null) return false;
            return savedatascript.s.items.TryGetValue(it.idInSave, out var reg) && reg == it;
        }

        // ------------------------------------------------------------------ object keys

        private static void Objects(float t)
        {
            for (int i = 0; i < _objects.Count; i++)
            {
                var tr = _objects[i];
                if (!tr.Active) continue;
                var o = _rec.Objects[i];
                if (tr.It == null)
                {
                    // destroyed (eaten, broken, sold): hidden from here on
                    if (o.Track.Keys.Count > 0)
                    {
                        var k = o.Track.Keys[o.Track.Keys.Count - 1]; k.T = t; k.Visible = false;
                        _rec.ObjectKeys += o.Track.Offer(k);
                    }
                    tr.Active = false;
                    continue;
                }
                _rec.ObjectKeys += o.Track.Offer(KeyOf(tr, t));
            }
        }

        private static ObjKey KeyOf(Tracked tr, float t)
        {
            var k = new ObjKey { T = t, Parent = -1, Visible = tr.It.gameObject.activeInHierarchy, Lights = tr.Look.LightMask() };
            Transform parent = null;
            for (var p = tr.T.parent; p != null; p = p.parent)
                if (_byRoot.TryGetValue(p, out int pi) && _objects[pi].Active && _objects[pi].It != null) { k.Parent = pi; parent = p; break; }
            if (parent != null)
            {
                var inv = Quaternion.Inverse(parent.rotation);
                k.Pos = Vec(inv * (tr.T.position - parent.position)); k.Rot = Rot(inv * tr.T.rotation);
            }
            else { k.Pos = Pos(tr.T.position); k.Rot = Rot(tr.T.rotation); }
            return k;
        }

        // ------------------------------------------------------------------ mods' keys

        /// A mod's key (MpApi replay channels): for `about` (one of the game's saved objects, or null for none), from now
        /// on. An object not in the recording yet joins now if it is near a player; one farther away is refused.
        internal static bool RecordMod(string channel, GameObject about, byte[] data)
        {
            if (!Recording || data == null) return false;
            float t = (float)(Time.realtimeSinceStartupAsDouble - _start);
            int obj = -1;
            if (about != null)
            {
                tosaveitemscript it = null;
                for (var p = about.transform; p != null && it == null; p = p.parent)
                {
                    var s = p.GetComponent<tosaveitemscript>();
                    if (s != null && Nested(p)) it = s;
                }
                if (it == null) return false;
                if (!_byInstance.TryGetValue(it.GetInstanceID(), out obj))
                {
                    if (_objects.Count >= ReplayLimits.MaxObjects || !ReplayLimits.InScope(Nearest(it.transform.position), false)) return false;
                    obj = _objects.Count;
                    Add(it, t, false);
                }
            }
            return _rec.AddModKey(channel, obj, t, data);
        }

        // ------------------------------------------------------------------ moving pieces

        /// A bounded number of piece reads per sample, round robin over the objects near a player.
        private const int PieceBudget = 6000;

        private static void Pieces(float t)
        {
            int n = _objects.Count, reads = 0;
            for (int step = 0; step < n && reads < PieceBudget; step++)
            {
                if (_pieceCursor >= n) _pieceCursor = 0;
                int i = _pieceCursor++;
                var tr = _objects[i];
                if (!tr.Active || tr.It == null || tr.Look.Pieces.Count == 0) continue;
                if (Nearest(tr.T.position) > ReplayLimits.PieceRadius || !tr.It.gameObject.activeInHierarchy) { ResetPieces(i); continue; }
                var o = _rec.Objects[i];
                var inv = Quaternion.Inverse(tr.T.rotation); var root = tr.T.position;
                for (int p = 0; p < tr.Look.Pieces.Count; p++)
                {
                    var st = tr.Look.Pieces[p].SourceT;
                    if (st == null) continue;
                    reads++;
                    _rec.PieceKeys += o.Piece(p).Offer(new PieceKey { T = t, Pos = Vec(inv * (st.position - root)), Rot = Rot(inv * st.rotation) });
                }
            }
        }

        private static void ResetPieces(int index)
        {
            var o = _rec.Objects[index];
            foreach (var p in o.Pieces) p?.Pause();
        }

        // ------------------------------------------------------------------ coordinates

        private static ReplayVec Pos(Vector3 unity)
        {
            var g = mainscript.GlobalFromUnityPos(unity);
            return new ReplayVec((float)(g.x - _rec.OriginX), (float)(g.y - _rec.OriginY), (float)(g.z - _rec.OriginZ));
        }
        internal static ReplayVec Vec(Vector3 v) => new ReplayVec(v.x, v.y, v.z);
        internal static ReplayRot Rot(Quaternion q) => new ReplayRot(q.x, q.y, q.z, q.w);

        // ------------------------------------------------------------------ status

        private static string Error(string why) { Plugin.Instance?.Overlay?.Toast("Replay: " + why, 3f); return "{\"error\":" + Json.Str(why) + "}"; }

        internal static string Status()
        {
            var r = _rec;
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"recording\":").Append(r != null ? "true" : "false");
            sb.Append(",\"saving\":").Append(Saving ? "true" : "false").Append(",\"failed\":").Append(_failed ? "true" : "false");
            var shown = r ?? Last?.Recording;
            sb.Append(",\"seconds\":").Append(Json.Num(shown != null ? shown.Duration : 0, "F2"));
            sb.Append(",\"objects\":").Append(shown != null ? shown.Objects.Count : 0);
            int active = 0; foreach (var o in _objects) if (o.Active) active++;
            sb.Append(",\"active\":").Append(active);
            sb.Append(",\"players\":").Append(shown != null ? shown.Players.Count : 0);
            sb.Append(",\"objectKeys\":").Append(shown != null ? shown.ObjectKeys : 0).Append(",\"pieceKeys\":").Append(shown != null ? shown.PieceKeys : 0);
            sb.Append(",\"playerSamples\":").Append(shown != null ? shown.PlayerSamples : 0);
            sb.Append(",\"modChannels\":").Append(shown != null ? shown.Channels.Count : 0).Append(",\"modKeys\":").Append(shown != null ? shown.ModKeys.Count : 0)
              .Append(",\"modMB\":").Append(Json.Num(shown != null ? shown.ModBytes / 1048576.0 : 0, "F2")).Append(",\"modDropped\":").Append(shown != null ? shown.ModKeysDropped : 0);
            sb.Append(",\"approxMB\":").Append(Json.Num(shown != null ? shown.ApproxBytes / 1048576.0 : 0, "F2"));
            sb.Append(",\"selfMs\":").Append(Json.Num(_calls == 0 ? 0 : _ticks * 1000.0 / Stopwatch.Frequency / _calls, "F4"));
            sb.Append(",\"maxMs\":").Append(Json.Num(_max * 1000.0 / Stopwatch.Frequency, "F4")).Append(",\"maxAt\":").Append(Json.Num(_maxAt, "F2"))
              .Append(",\"gcSamples\":").Append(_gcTicks).Append(",\"maxGcMs\":").Append(Json.Num(_maxGc * 1000.0 / Stopwatch.Frequency, "F4"))
              .Append(",\"maxPhasesMs\":[").Append(string.Join(",", Array.ConvertAll(_maxPhase, x => Json.Num(x * 1000.0 / Stopwatch.Frequency, "F2")))).Append(']');
            sb.Append(",\"lastFile\":").Append(Json.Str(_lastFile)).Append(",\"stopReason\":").Append(Json.Str(_stopReason));
            sb.Append('}');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ banner

        private static readonly CompactOverlay Bar = new CompactOverlay();
        private static int _bannerSecond = -1;
        private static string _bannerText = "";
        internal static void Draw()
        {
            if (!Recording || ReplayViewer.Active || Event.current.type != EventType.Repaint) return;
            if (!ShowBanner) return;
            OverlayStyle.Ensure();
            int second = (int)_rec.Duration;
            if (second != _bannerSecond) { _bannerSecond = second; _bannerText = "Recording " + ReplayClock.Clock(second); }
            Bar.Measure("RevaMP · Replay", _bannerText);
            float x = Screen.width - Bar.Width - 12 * OverlayStyle.Scale, y = 12 * OverlayStyle.Scale;
            Bar.Draw(new DebugBox(x, y, Bar.Width, Bar.Height), OverlayKind.Warning);
        }

        /// Whether the small "Recording" banner shows (Settings → Revamp → Filmmaking). Off for clean footage.
        internal static bool ShowBanner = true;
    }
}
