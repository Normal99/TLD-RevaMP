using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;

namespace HeadUnit
{
    /// Plays one unit's video on this machine and keeps it at the shared position (UnitState.Position on the session
    /// clock): a small difference is closed by playing 5 % faster or slower, a big one by seeking. The sound comes
    /// from the unit in the car (3D), at the car's shared video volume. It plays whichever tab a player looks at.
    ///
    /// Each video gets a VideoPlayer of its own, thrown away for the next one: under Proton, preparing a stopped player
    /// again crashed the game in the media framework (playtest 2026-10-15: NAV, back to YouTube, PLAY → crash in
    /// rtworkq). The picture goes into a texture the video's own shape; the screen fits it without stretching.
    ///
    /// The player runs on real time, as the shared position does: on game time it stood still in the pause menu
    /// (time scale 0) while the position went on, and the seeks to catch up, four a second, crashed the media
    /// framework the same way (playtest 2026-10-15, crash while driving after opening the menu). Seeks are also
    /// spaced out and never stacked.
    ///
    /// A player is never destroyed while the game runs: under Proton, destroying a VideoPlayer (playing or not, at
    /// once or seconds later) crashed the game in rtworkq when the next one started, two runs in three (huprev.py,
    /// 2026-10-15; with old players only paused, none in eight switches). A player done with is paused, silenced and
    /// kept in this unit's pool, and the next video reuses one from there with a new url (never a stopped one: see
    /// above). So a unit has as many players as it ever had videos loading at once, two or three. New players are
    /// also spaced out (MakeGapS): a burst of skips makes only the last one.
    public sealed class Video
    {
        public VideoPlayer Player { get; private set; }
        public AudioSource Audio { get; private set; }
        public RenderTexture Texture { get; private set; }
        public string VideoId { get; private set; }
        public string Problem;           // why nothing plays (shown on the screen)
        public bool Ready => Player != null && Player.isPrepared && Texture != null && !_placeholder.Contains(Player);
        /// Width over height of the picture (16:9 until the video says).
        public float Aspect = 16f / 9f;
        public static float SeekOverS = 1.0f, NudgeOverS = 0.08f, RetryS = 30f, SeekGapS = 3f;
        public static int MaxTextureWidth = 1920;
        public static bool Trace = false;   // makes, drops and seeks in the log
        public static float MakeGapS = 1.5f;
        public static bool SendEnded = true;   // A/B

        /// Players done with: paused, silent, kept for the next video. Picture textures go a second after their player.
        private readonly List<VideoPlayer> _pool = new List<VideoPlayer>();
        private readonly Dictionary<VideoPlayer, AudioSource> _audioOf = new Dictionary<VideoPlayer, AudioSource>();
        private readonly Dictionary<VideoPlayer, string> _idOf = new Dictionary<VideoPlayer, string>();
        private readonly HashSet<VideoPlayer> _failed = new HashSet<VideoPlayer>();
        private readonly HashSet<VideoPlayer> _placeholder = new HashSet<VideoPlayer>();
        public const string NoCodecs = "this machine can't decode YouTube's video (Steam's Proton has no H.264): the head unit converts it with ffmpeg, which + LINK offers to download";

        /// Valve's Proton has no H.264 decoder: the media framework plays a 4 s stand-in clip instead of any video
        /// (the laptop, Proton 11.0, 2026-10-15: length 4.0, 120 frames at 30/s, for 213 s and 216 s videos; GE-Proton
        /// plays them). Told apart by the file's own length (its mp4 header) against the player's.
        private static bool StandIn(VideoPlayer p)
        {
            try
            {
                if (p.length <= 0) return false;
                string path = p.url.StartsWith("file:///") ? p.url.Substring(8) : p.url;
                double file = Mp4Seconds(path);
                return file > 0 && file > p.length + 2.0;
            }
            catch { return false; }
        }

        /// The length an mp4 file says it has (moov/mvhd), or -1.
        public static double Mp4Seconds(string path)
        {
            using (var f = System.IO.File.OpenRead(path))
            {
                var buf = new byte[16];
                long pos = 0, end = f.Length;
                // top level: find moov (yt-dlp's files have it first; a file with it at the end is read to there)
                while (pos + 8 <= end)
                {
                    f.Position = pos; if (f.Read(buf, 0, 16) < 8) return -1;
                    long size = BE(buf, 0, 4); string type = System.Text.Encoding.ASCII.GetString(buf, 4, 4);
                    int head = 8;
                    if (size == 1) { size = BE(buf, 8, 8); head = 16; }
                    else if (size == 0) size = end - pos;
                    if (size < head) return -1;
                    if (type == "moov")
                    {
                        long c = pos + head, cend = pos + size;
                        while (c + 8 <= cend)
                        {
                            f.Position = c; if (f.Read(buf, 0, 8) < 8) return -1;
                            long cs = BE(buf, 0, 4); string ct = System.Text.Encoding.ASCII.GetString(buf, 4, 4);
                            if (cs < 8) return -1;
                            if (ct == "mvhd")
                            {
                                var m = new byte[32]; f.Position = c + 8; if (f.Read(m, 0, 32) < 32) return -1;
                                bool v1 = m[0] == 1;
                                long scale = v1 ? BE(m, 20, 4) : BE(m, 12, 4);
                                long dur = v1 ? BE(m, 24, 8) : BE(m, 16, 4);
                                return scale > 0 ? dur / (double)scale : -1;
                            }
                            c += cs;
                        }
                        return -1;
                    }
                    pos += size;
                }
            }
            return -1;
        }

        private static long BE(byte[] b, int at, int n) { long v = 0; for (int i = 0; i < n; i++) v = (v << 8) | b[at + i]; return v; }

        private void CheckStandIn(VideoPlayer p)
        {
            if (!StandIn(p)) return;
            if (_placeholder.Add(p)) Plugin.Log.LogWarning($"video {VideoId}: the player gives a {p.length:F1} s stand-in clip: no video decoder");
        }
        private readonly Dictionary<VideoPlayer, float> _droppedAt = new Dictionary<VideoPlayer, float>();
        /// About 20 MB each (huprev.py: 3.89 → 4.01 GB over six videos). Past PoolKeep the oldest, idle IdleDestroyS,
        /// is destroyed after all: under Proton the next video may then crash the game (huprev.py, a kept player
        /// destroyed after 12 s idle: the next one crashed), so only after a long session (100 videos, ~2 GB).
        public static int PoolKeep = 100; public static float IdleDestroyS = 60f;
        public static long Destroyed;

        /// Kept players beyond PoolKeep, idle for IdleDestroyS, destroyed one at a time.
        private void Prune()
        {
            if (_pool.Count <= PoolKeep) return;
            float now = Time.realtimeSinceStartup;
            var k = _pool[0];
            float at = 0f;
            if (k != null && _droppedAt.TryGetValue(k, out at) && now - at < IdleDestroyS) return;
            _pool.RemoveAt(0);
            if (k == null) return;
            if (Trace) Plugin.Log.LogInfo($"video destroy kept {(_idOf.TryGetValue(k, out var kid) ? kid : "?")} idle {now - at:F0} s");
            if (_audioOf.TryGetValue(k, out var a) && a != null) Object.Destroy(a);
            _audioOf.Remove(k); _idOf.Remove(k); _droppedAt.Remove(k); _failed.Remove(k);
            Object.Destroy(k);
            Destroyed++;
        }
        private static readonly List<(RenderTexture T, float At)> OldTextures = new List<(RenderTexture, float)>();
        public int PoolSize => _pool.Count;
        /// The videos open in kept players (their files are in use).
        public IEnumerable<string> PooledIds() { foreach (var p in _pool) if (p != null && _idOf.TryGetValue(p, out var id)) yield return id; }

        /// Once a frame (Runner): old picture textures released.
        public static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = OldTextures.Count - 1; i >= 0; i--)
            {
                if (now - OldTextures[i].At < 1f) continue;
                var t = OldTextures[i].T;
                if (t != null) { if (RenderTexture.active == t) RenderTexture.active = null; t.Release(); Object.Destroy(t); }
                OldTextures.RemoveAt(i);
            }
        }
        private float _lastMake = -100f;
        private string _makeId, _makeUrl;

        private readonly Transform _unit;
        /// The players and their sound live here, not on the unit: a unit destroyed (taken out and thrown away, a world
        /// unloaded) would destroy them with it, the crash above. It follows the unit while it plays; it outlives it.
        private readonly GameObject _host;
        private string _wantId;
        private float _nextCheck, _failedAt, _lastSeek = -100f;
        private bool _seeking;
        private bool _endedSent;

        public Video(GameObject unit)
        {
            _unit = unit.transform;
            _host = new GameObject("HeadUnitVideo");
            Object.DontDestroyOnLoad(_host);
            _host.transform.position = _unit.position;
        }

        /// Nothing playing, the player thrown away (the queue emptied, the unit removed).
        public void Stop()
        {
            Drop();
            VideoId = null; _wantId = null; Problem = null;
        }

        /// Paused where it is (the player walked away): picks up at the shared position when back.
        public void Hold() { if (Player != null && Player.isPlaying) Player.Pause(); }

        private void Drop()
        {
            _makeId = null; _makeUrl = null;
            if (Trace && Player != null) Plugin.Log.LogInfo($"video drop {VideoId} prepared {Player.isPrepared} playing {Player.isPlaying} pool {_pool.Count + 1}");
            if (Player != null)
            {
                if (Player.isPlaying) Player.Pause();
                Player.targetTexture = null;
                _pool.Add(Player); _droppedAt[Player] = Time.realtimeSinceStartup;
            }
            if (Audio != null) Audio.mute = true;
            if (Texture != null) OldTextures.Add((Texture, Time.realtimeSinceStartup));
            Player = null; Audio = null; Texture = null;
        }

        /// The kept player that already has this video open, or a new one. A kept player is never given another url:
        /// that crashed like destroying one (pooled players reused for the next song, huprev.py 2026-10-15).
        private VideoPlayer Take(string id, out bool reused)
        {
            reused = false;
            for (int i = 0; i < _pool.Count; i++)
            {
                var k = _pool[i];
                if (k == null) { _pool.RemoveAt(i--); continue; }
                if (!_idOf.TryGetValue(k, out var kid) || kid != id || _failed.Contains(k) || _placeholder.Contains(k)) continue;
                _pool.RemoveAt(i);
                reused = true;
                return k;
            }
            var p = _host.AddComponent<VideoPlayer>();
            p.playOnAwake = false;
            p.renderMode = VideoRenderMode.RenderTexture;
            p.source = VideoSource.Url;
            p.waitForFirstFrame = true;
            p.skipOnDrop = true;
            p.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
            p.seekCompleted += _ => { if (p == Player) _seeking = false; };
            var a = _host.AddComponent<AudioSource>();
            a.spatialBlend = 1f; a.minDistance = 1.5f; a.maxDistance = 25f; a.rolloffMode = AudioRolloffMode.Linear;
            a.playOnAwake = false;
            p.audioOutputMode = VideoAudioOutputMode.AudioSource;
            p.SetTargetAudioSource(0, a);
            _audioOf[p] = a;
            p.errorReceived += (_, msg) =>
            {
                _failed.Add(p);
                if (p != Player) return;
                Problem = "can't play: " + msg; _failedAt = Time.realtimeSinceStartup;
                Plugin.Log.LogWarning("video " + VideoId + ": " + msg);
            };
            p.prepareCompleted += _ =>
            {
                if (p != Player) return;
                Problem = null;
                CheckStandIn(p);
                MakeTexture(p);
            };
            return p;
        }

        private void Make(string id, string url)
        {
            Drop();
            _makeId = null;
            VideoId = id;
            var p = Take(id, out bool reused);
            if (Trace) Plugin.Log.LogInfo($"video make {id} reused {reused} pool {_pool.Count}");
            Player = p; Audio = _audioOf[p]; Audio.mute = false; _seeking = false; _lastSeek = -100f;
            _idOf[p] = id;
            if (reused && p.isPrepared) { Problem = null; MakeTexture(p); return; }   // open already: on to the shared position
            if (reused) return;   // still preparing: prepareCompleted carries on
            p.url = url;
            p.Prepare();
        }

        private void MakeTexture(VideoPlayer p)
        {
            int w = (int)p.width, h = (int)p.height;
            if (w <= 0 || h <= 0) { w = 640; h = 360; }
            Aspect = w / (float)h;
            if (w > MaxTextureWidth) { h = Mathf.RoundToInt(h * MaxTextureWidth / (float)w); w = MaxTextureWidth; }
            Texture = new RenderTexture(w, h, 0) { name = "HeadUnitVideo" };
            p.targetTexture = Texture;
        }

        /// Once a frame while the unit is near the player. ended: the queue index that finished.
        public void Follow(UnitState s, double now, float volume, System.Action<int> ended)
        {
            if (_unit != null) _host.transform.position = _unit.position;
            if (Audio != null) Audio.volume = volume;
            Prune();
            var cur = s.Current;
            if (cur == null) { if (VideoId != null || _wantId != null) Stop(); return; }
            // a video that couldn't be fetched or played is tried again after a while
            bool failed = Problem != null && Problem != "loading..." && Problem != NoCodecs && Time.realtimeSinceStartup - _failedAt > RetryS;
            if (failed && _wantId == cur.VideoId) { _wantId = null; VideoId = null; }
            if (cur.VideoId != VideoId && cur.VideoId != _wantId)
            {
                // a new video: its file (yt-dlp, cached), then a player for it
                Drop();
                _wantId = cur.VideoId; VideoId = null; _endedSent = false; Problem = "loading...";
                string id = cur.VideoId;
                Ytdlp.Resolve(id, url =>
                {
                    if (_wantId != id) return;   // moved on meanwhile
                    if (url == null) { Problem = Ytdlp.Status.Length > 0 ? Ytdlp.Status : "can't find the video"; _failedAt = Time.realtimeSinceStartup; return; }
                    _makeId = id; _makeUrl = url;   // made below, spaced out
                });
                return;
            }
            if (_makeId != null && _makeId == _wantId && Time.realtimeSinceStartup - _lastMake >= MakeGapS)
            {
                string id = _makeId, url = _makeUrl;
                _makeId = null; _makeUrl = null; _lastMake = Time.realtimeSinceStartup;
                Make(id, url);
            }
            if (Player == null || !Player.isPrepared) return;
            // the next two in the queue fetched while this one plays
            if (s.Index + 1 < s.Queue.Count) Ytdlp.Prefetch(s.Queue[s.Index + 1].VideoId);
            if (s.Index + 2 < s.Queue.Count) Ytdlp.Prefetch(s.Queue[s.Index + 2].VideoId);

            if (_placeholder.Contains(Player))
            {
                // nothing to show and no length to trust: the others go on with the shared queue. This machine converts
                // from now on (Ytdlp.ToVp8): the video fetched again as its VP8 copy
                if (Player.isPlaying) Player.Pause();
                if (!Plugin.Convert.Value) { Plugin.Convert.Value = true; Plugin.Log.LogWarning("no H.264 decoder here: videos are converted to VP8 from now on (config Video.ConvertToVP8)"); }
                if (Ytdlp.HasFfmpeg) { Drop(); _wantId = null; VideoId = null; return; }
                Problem = NoCodecs;
                return;
            }
            float want = s.Position(now);
            float len = (float)Player.length;
            if (len > 0 && cur.Duration <= 0f) cur.Duration = len;   // local only; the owner learns it from its own player
            if (len > 0 && want >= len - 0.25f)
            {
                if (Player.isPlaying) Player.Pause();
                if (s.Playing && !_endedSent && SendEnded) { _endedSent = true; Plugin.Log.LogInfo($"video {VideoId} ended: index {s.Index}, at {want:F1} of {len:F1} (player at {Player.time:F1})"); ended(s.Index); }
                return;
            }
            if (!s.Playing)
            {
                if (Player.isPlaying) Player.Pause();
                if (Mathf.Abs((float)Player.time - want) > 0.2f) SeekTo(want);
                return;
            }
            if (!Player.isPlaying) { SeekTo(want, force: true); Player.Play(); return; }
            if (Time.realtimeSinceStartup < _nextCheck) return;
            _nextCheck = Time.realtimeSinceStartup + 0.25f;
            float diff = want - (float)Player.time;   // > 0: we are behind
            if (Mathf.Abs(diff) > SeekOverS) { if (SeekTo(want)) Player.playbackSpeed = 1f; }
            else if (Mathf.Abs(diff) > NudgeOverS && Player.canSetPlaybackSpeed) Player.playbackSpeed = diff > 0 ? 1.05f : 0.95f;
            else if (Player.playbackSpeed != 1f) Player.playbackSpeed = 1f;
        }

        /// One seek at a time, at most one every SeekGapS (unless forced: starting to play).
        private bool SeekTo(float t, bool force = false)
        {
            if (_seeking && Time.realtimeSinceStartup - _lastSeek > 5f) _seeking = false;   // no word back: don't wait forever
            if (_seeking && !force) return false;
            if (!force && Time.realtimeSinceStartup - _lastSeek < SeekGapS) return false;
            if (!Player.canSetTime) return false;
            _lastSeek = Time.realtimeSinceStartup; _seeking = true;
            if (Trace) Plugin.Log.LogInfo($"video seek {VideoId} to {t:F2} from {Player.time:F2} force {force} t {Time.realtimeSinceStartup:F2}");
            Player.time = t;
            return true;
        }

        public void Dispose() => Drop();

        /// The current player as the media framework reports it (Probe).
        public string Describe() => Player == null ? "no player" : $"{VideoId}: length {Player.length:F1}, frames {Player.frameCount} at {Player.frameRate:F1}/s, time {Player.time:F1}, frame {Player.frame}, playing {Player.isPlaying}, prepared {Player.isPrepared}";
    }
}
