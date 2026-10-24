using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TLDRevamp.Replay
{
    /// The replay's pure core (issue #36): the recording's data model, how keys are written while recording, how a
    /// moment is sampled during playback, the file format and the playback clock. Nothing here touches Unity, so all
    /// of it is unit-tested (tools/unit/ReplayCoreTest.cs).
    ///
    /// A recording is a set of tracks. Each object track holds a key only when the object moved, turned, changed
    /// parent or was shown/hidden, so a parked car costs one key for the whole recording and a car part costs nothing
    /// while it rides on its car (its pose is kept relative to the car). Players are sampled at the fixed rate: their
    /// limbs move all the time.

    public struct ReplayVec
    {
        public float X, Y, Z;
        public ReplayVec(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static ReplayVec Lerp(ReplayVec a, ReplayVec b, float u) => new ReplayVec(a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u, a.Z + (b.Z - a.Z) * u);
        public static float Distance(ReplayVec a, ReplayVec b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        public bool Finite => ReplayMath.Finite(X) && ReplayMath.Finite(Y) && ReplayMath.Finite(Z);
    }

    public struct ReplayRot
    {
        public float X, Y, Z, W;
        public ReplayRot(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static readonly ReplayRot Identity = new ReplayRot(0, 0, 0, 1);
        public static float Dot(ReplayRot a, ReplayRot b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        /// Normalised linear blend along the short way round: at 20 samples a second the turn between two keys is
        /// small, and nlerp is then indistinguishable from slerp.
        public static ReplayRot Nlerp(ReplayRot a, ReplayRot b, float u)
        {
            if (Dot(a, b) < 0) b = new ReplayRot(-b.X, -b.Y, -b.Z, -b.W);
            var r = new ReplayRot(a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u, a.Z + (b.Z - a.Z) * u, a.W + (b.W - a.W) * u);
            return r.Normalized();
        }
        public ReplayRot Normalized()
        {
            float n = (float)Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
            return n < 1e-6f || !ReplayMath.Finite(n) ? Identity : new ReplayRot(X / n, Y / n, Z / n, W / n);
        }
        /// The angle between two rotations, in degrees.
        public static float Angle(ReplayRot a, ReplayRot b)
        {
            float d = Math.Min(1f, Math.Abs(Dot(a, b)));
            return (float)(2.0 * Math.Acos(d) * 180.0 / Math.PI);
        }
        public bool Finite => ReplayMath.Finite(X) && ReplayMath.Finite(Y) && ReplayMath.Finite(Z) && ReplayMath.Finite(W);
    }

    public static class ReplayMath
    {
        public static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        public static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }

    /// The bounds the recorder and the file reader hold to. A file that breaks one is refused, never half-read.
    public static class ReplayLimits
    {
        public const int SampleHz = 20;
        public const double SampleStep = 1.0 / SampleHz;
        /// How long one recording may run. Recording stops by itself at this point.
        public const double MaxSeconds = 3600;
        public const int MaxObjects = 4096;
        public const int MaxPlayers = 16;
        public const int MaxPiecesPerObject = 512;
        public const int MaxLightsPerObject = 32;
        /// Keys over every object track together (about 40 bytes each in memory).
        public const int MaxObjectKeys = 2000000;
        public const int MaxPieceKeys = 1000000;
        public const int MaxOutfitEntries = 256;
        /// Objects closer than this to a player join the recording; tracked ones farther than DropRadius stop being
        /// sampled and stay where they were last seen.
        public const float RecordRadius = 250f, DropRadius = 300f;
        /// The moving pieces of an object (a steering wheel, a gauge needle, a door on its hinge) are checked only
        /// near a player, where they can be seen.
        public const float PieceRadius = 30f;
        /// The bones of a skinned mesh (an animal, a body, a ragdoll) are read farther out: a rabbit running across a field
        /// is seen from further away than a gauge needle.
        public const float BoneRadius = 120f;
        /// Changes smaller than these write no key.
        public const float PositionEpsilon = 0.002f, AngleEpsilon = 0.25f;
        /// The material sets one piece is seen drawing with (a lamp's glass lit and unlit, a battery's LED), the one
        /// it was captured with included.
        public const int MaxMaterialSets = 16;
        /// Mods' own data (replay channels): channels per recording, keys over all of them, one key's bytes, and every
        /// key's bytes together. A key over a limit is left out; the recording goes on.
        public const int MaxModChannels = 64, MaxChannelName = 129;
        public const int MaxModKeys = 500000, MaxModKeyBytes = 256 * 1024;
        public const long MaxModBytes = 96L * 1024 * 1024;
        public static readonly float[] Speeds = { 0.1f, 0.25f, 0.5f, 1f, 2f, 4f };
        public const double JumpSeconds = 5;

        /// Whether an object at `distance` from the nearest player is sampled. Hysteresis keeps an object at the edge
        /// from joining and leaving every second.
        public static bool InScope(float distance, bool tracked) => tracked ? distance <= DropRadius : distance <= RecordRadius;
    }

    /// One key of an object track. A world-parented key (Parent -1) is relative to the recording's origin; any other
    /// key is relative to the parent object's root, in its unscaled frame.
    public struct ObjKey
    {
        public float T;
        public int Parent;
        public ReplayVec Pos;
        public ReplayRot Rot;
        public bool Visible;
        /// Which of the object's own lights are on (bit i: light i of its look; up to 32).
        public uint Lights;
    }

    /// A mod's own data in a recording (a replay channel, TLDRevamp.Multiplayer's MpApi.RegisterReplayChannel): the
    /// whole state of what the mod shows for one recorded object (Object, an index into the recording's objects; -1:
    /// none) from time T on. Playback hands each (channel, object) its latest key at the playhead.
    public struct ModKey
    {
        public float T;
        public int Channel;
        public int Object;
        public byte[] Data;
    }

    public struct PieceKey
    {
        public float T;
        public ReplayVec Pos;
        public ReplayRot Rot;
        /// The piece's renderer did not draw (switched off, or in a part of the object that was switched off: an
        /// engine's other variants, a lamp's unlit bulb). The default, false, is drawn.
        public bool Hidden;
        /// Which of the piece's material sets it drew with (0: the one it was captured with; the rest only exist in the
        /// session that recorded, ReplayLook.Piece.Variants).
        public byte Mat;
    }

    /// Writes keys only when something changed, and keeps interpolation honest: when an object starts moving after a
    /// rest, the rest pose is written again at the last moment it was still true, so playback does not smear the
    /// start of the move back over the whole rest.
    public sealed class ObjectTrack
    {
        public readonly List<ObjKey> Keys = new List<ObjKey>();
        private float _lastSampleT = -1;

        /// Offers the object's state at a sample. Returns how many keys were written (0, 1 or 2).
        public int Offer(ObjKey k)
        {
            if (Keys.Count == 0) { Keys.Add(k); _lastSampleT = k.T; return 1; }
            var last = Keys[Keys.Count - 1];
            if (k.T < last.T) return 0;
            bool structural = k.Parent != last.Parent || k.Visible != last.Visible || k.Lights != last.Lights;
            // A hidden object (in a pocket, in a box) can be anywhere: its pose is not worth a key.
            bool moved = k.Visible && (ReplayVec.Distance(k.Pos, last.Pos) > ReplayLimits.PositionEpsilon ||
                                       ReplayRot.Angle(k.Rot, last.Rot) > ReplayLimits.AngleEpsilon);
            int written = 0;
            if (structural || moved)
            {
                if (last.T < _lastSampleT - 1e-4f && _lastSampleT < k.T)
                {
                    var hold = last; hold.T = _lastSampleT; Keys.Add(hold); written++;
                }
                Keys.Add(k); written++;
            }
            _lastSampleT = k.T;
            return written;
        }

        /// The object's state at time t. Before the first key the first key holds (an object that came into range
        /// later was most likely already where it was first seen); after the last, the last.
        public bool Sample(float t, out ObjKey result)
        {
            result = default;
            if (Keys.Count == 0) return false;
            int i = Find(Keys, t);
            if (i < 0) { result = Keys[0]; return true; }
            if (i >= Keys.Count - 1) { result = Keys[Keys.Count - 1]; return true; }
            var a = Keys[i]; var b = Keys[i + 1];
            if (a.Parent != b.Parent || a.Visible != b.Visible || a.Lights != b.Lights || b.T <= a.T) { result = a; return true; }
            float u = (t - a.T) / (b.T - a.T);
            result = a;
            result.T = t;
            result.Pos = ReplayVec.Lerp(a.Pos, b.Pos, u);
            result.Rot = ReplayRot.Nlerp(a.Rot, b.Rot, u);
            return true;
        }

        /// The last key at or before t, or -1.
        internal static int Find(List<ObjKey> keys, float t)
        {
            int lo = 0, hi = keys.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (keys[mid].T <= t) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return found;
        }
    }

    /// The moving pieces of one object, each relative to the object's root. A piece that never moved has no keys and
    /// keeps the pose it had when the object was first seen.
    public sealed class PieceTrack
    {
        public readonly List<PieceKey> Keys = new List<PieceKey>();
        private float _lastSampleT = -1;

        public int Offer(PieceKey k)
        {
            if (Keys.Count == 0)
            {
                // The first offer is the baseline, not a key: a piece is only worth keys once it moves. A track with no
                // keys plays as drawn with its first material set, so a first offer that is anything else is a key.
                if (_lastSampleT < 0)
                {
                    _lastSampleT = k.T;
                    if (k.Hidden || k.Mat != 0) { Keys.Add(k); return 1; }
                    _first = k; return 0;
                }
                if (!Changed(k, _first)) { _lastSampleT = k.T; return 0; }
                var hold = _first; hold.T = _lastSampleT; Keys.Add(hold); Keys.Add(k); _lastSampleT = k.T; return 2;
            }
            var last = Keys[Keys.Count - 1];
            if (k.T < last.T) return 0;
            int written = 0;
            if (Changed(k, last))
            {
                if (last.T < _lastSampleT - 1e-4f && _lastSampleT < k.T) { var hold = last; hold.T = _lastSampleT; Keys.Add(hold); written++; }
                Keys.Add(k); written++;
            }
            _lastSampleT = k.T;
            return written;
        }
        private PieceKey _first;

        /// Shown or hidden, the material set, or (while drawn) the pose. A hidden piece can be anywhere: its pose is
        /// not worth a key.
        private static bool Changed(PieceKey k, PieceKey last) =>
            k.Hidden != last.Hidden || k.Mat != last.Mat ||
            !k.Hidden && (ReplayVec.Distance(k.Pos, last.Pos) > ReplayLimits.PositionEpsilon || ReplayRot.Angle(k.Rot, last.Rot) > ReplayLimits.AngleEpsilon);

        /// Forgets the baseline, so a piece that was out of sight for a while starts from a fresh one.
        public void Pause() { if (Keys.Count == 0) _lastSampleT = -1; }

        public bool Sample(float t, out PieceKey result)
        {
            result = default;
            if (Keys.Count == 0) return false;
            int lo = 0, hi = Keys.Count - 1, i = -1;
            while (lo <= hi) { int mid = (lo + hi) >> 1; if (Keys[mid].T <= t) { i = mid; lo = mid + 1; } else hi = mid - 1; }
            if (i < 0) { result = Keys[0]; return true; }
            if (i >= Keys.Count - 1) { result = Keys[Keys.Count - 1]; return true; }
            var a = Keys[i]; var b = Keys[i + 1];
            if (b.T <= a.T) { result = a; return true; }
            float u = (t - a.T) / (b.T - a.T);
            result = new PieceKey { T = t, Pos = ReplayVec.Lerp(a.Pos, b.Pos, u), Rot = ReplayRot.Nlerp(a.Rot, b.Rot, u), Hidden = a.Hidden, Mat = a.Mat };
            return true;
        }
    }

    /// One object in a recording. SaveId is the game's own save id (the same object in the same save has the same id
    /// in a later session); Prefab is the game's item index, the fallback when that object is not there.
    public sealed class ReplayObject
    {
        public uint SaveId;
        public int Prefab;
        public int PieceCount;
        /// How many lights the object's look had when it was recorded (the bits of ObjKey.Lights).
        public int LightCount;
        public readonly ObjectTrack Track = new ObjectTrack();
        /// Indexed by piece; null where a piece never moved.
        public PieceTrack[] Pieces = new PieceTrack[0];

        public PieceTrack Piece(int i)
        {
            if (i < 0 || i >= PieceCount) return null;
            if (Pieces.Length < PieceCount) Array.Resize(ref Pieces, PieceCount);
            return Pieces[i] ?? (Pieces[i] = new PieceTrack());
        }
    }

    /// The six IK targets of a player's body (head, left hand, right hand, left foot, right foot, pelvis), in the
    /// order the game's own playeranimationscript.setPose takes them.
    public static class ReplayLimbs { public const int Count = 6; }

    public struct PlayerSample
    {
        public float T;
        public bool Visible;
        public ReplayVec Body;
        public ReplayRot BodyRot;
        /// What the player sees: the local player's own camera, or a remote player's head.
        public ReplayVec Eye;
        public ReplayRot EyeRot;
        public float HeadPitch;
        public ReplayVec L0, L1, L2, L3, L4, L5;
        public ReplayRot R0, R1, R2, R3, R4, R5;

        public ReplayVec Limb(int i) { switch (i) { case 0: return L0; case 1: return L1; case 2: return L2; case 3: return L3; case 4: return L4; default: return L5; } }
        public ReplayRot LimbRot(int i) { switch (i) { case 0: return R0; case 1: return R1; case 2: return R2; case 3: return R3; case 4: return R4; default: return R5; } }
        public void SetLimb(int i, ReplayVec p, ReplayRot r)
        {
            switch (i)
            {
                case 0: L0 = p; R0 = r; break; case 1: L1 = p; R1 = r; break; case 2: L2 = p; R2 = r; break;
                case 3: L3 = p; R3 = r; break; case 4: L4 = p; R4 = r; break; default: L5 = p; R5 = r; break;
            }
        }

        public static PlayerSample Lerp(PlayerSample a, PlayerSample b, float u)
        {
            var r = a;
            r.Body = ReplayVec.Lerp(a.Body, b.Body, u); r.BodyRot = ReplayRot.Nlerp(a.BodyRot, b.BodyRot, u);
            r.Eye = ReplayVec.Lerp(a.Eye, b.Eye, u); r.EyeRot = ReplayRot.Nlerp(a.EyeRot, b.EyeRot, u);
            r.HeadPitch = a.HeadPitch + (b.HeadPitch - a.HeadPitch) * u;
            for (int i = 0; i < ReplayLimbs.Count; i++) r.SetLimb(i, ReplayVec.Lerp(a.Limb(i), b.Limb(i), u), ReplayRot.Nlerp(a.LimbRot(i), b.LimbRot(i), u));
            return r;
        }

        public bool Finite
        {
            get
            {
                if (!ReplayMath.Finite(T) || !ReplayMath.Finite(HeadPitch) || !Body.Finite || !BodyRot.Finite || !Eye.Finite || !EyeRot.Finite) return false;
                for (int i = 0; i < ReplayLimbs.Count; i++) if (!Limb(i).Finite || !LimbRot(i).Finite) return false;
                return true;
            }
        }
    }

    public sealed class ReplayPlayer
    {
        /// The player who recorded. Every other player is a remote one, numbered in the order they appeared.
        public bool Local;
        public int Outfit = -1;
        public bool[] OutfitEnabled = new bool[0];
        public float[] OutfitColors = new float[0];   // r, g, b, a per colour
        public readonly List<PlayerSample> Samples = new List<PlayerSample>();

        /// Players are sampled at the fixed rate, so a gap means the player was not there (left, not loaded).
        public bool Sample(float t, out PlayerSample result)
        {
            result = default;
            if (Samples.Count == 0) return false;
            int lo = 0, hi = Samples.Count - 1, i = -1;
            while (lo <= hi) { int mid = (lo + hi) >> 1; if (Samples[mid].T <= t) { i = mid; lo = mid + 1; } else hi = mid - 1; }
            if (i < 0) { result = Samples[0]; return Samples[0].T - t < ReplayLimits.SampleStep * 2 && Samples[0].Visible; }
            if (i >= Samples.Count - 1) { result = Samples[i]; return t - Samples[i].T < ReplayLimits.SampleStep * 2 && Samples[i].Visible; }
            var a = Samples[i]; var b = Samples[i + 1];
            if (!a.Visible) { result = a; return false; }
            if (!b.Visible || b.T - a.T > ReplayLimits.SampleStep * 4) { result = a; return t - a.T < ReplayLimits.SampleStep * 2; }
            result = PlayerSample.Lerp(a, b, (t - a.T) / (b.T - a.T));
            result.T = t;
            return true;
        }
    }

    public sealed class ReplayRecording
    {
        /// 2: mods' replay channels after the players (format 1 files still read, with none).
        /// 3: each piece key says whether the piece drew and with which material set.
        public const int Format = 3;
        /// The piece keys say whether each piece drew (format 3 on). An older file's pieces draw as the world shows them.
        public bool PieceStates = true;
        /// The RevaMP wire protocol of the build that recorded (informational).
        public int Protocol;
        /// The global position every world-parented key is relative to.
        public double OriginX, OriginY, OriginZ;
        public float Duration;
        public readonly List<ReplayObject> Objects = new List<ReplayObject>();
        public readonly List<ReplayPlayer> Players = new List<ReplayPlayer>();
        public int ObjectKeys, PieceKeys;
        /// The mods' replay channels ("modid/name") and their keys, in time order.
        public readonly List<string> Channels = new List<string>();
        public readonly List<ModKey> ModKeys = new List<ModKey>();
        public long ModBytes;
        /// Mod keys left out because a limit was reached (status).
        public int ModKeysDropped;

        public int PlayerSamples { get { int n = 0; foreach (var p in Players) n += p.Samples.Count; return n; } }

        /// Bytes this recording holds in memory, roughly (for the status and the caps).
        public long ApproxBytes => (long)ObjectKeys * 40 + (long)PieceKeys * 32 + (long)PlayerSamples * 236 + Objects.Count * 64L + ModBytes + ModKeys.Count * 32L;

        /// A channel's name is a mod id and a name, each 1-64 letters, digits, '-', '_' or '.', joined by '/'.
        public static bool ValidChannel(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > ReplayLimits.MaxChannelName) return false;
            int slash = name.IndexOf('/');
            if (slash < 1 || slash > 64 || name.Length - slash - 1 < 1 || name.Length - slash - 1 > 64) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (i == slash) continue;
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == '.')) return false;
            }
            return true;
        }

        /// Adds a mod's key at time t (never before the last key: a late one is moved up to it). False when it breaks a
        /// limit or names no valid channel or object: the key is left out and the recording goes on.
        public bool AddModKey(string channel, int obj, float t, byte[] data)
        {
            if (data == null || !ReplayMath.Finite(t) || t < 0 || obj < -1 || obj >= Objects.Count || !ValidChannel(channel)) return false;
            int ch = Channels.IndexOf(channel);
            if (data.Length > ReplayLimits.MaxModKeyBytes || ModKeys.Count >= ReplayLimits.MaxModKeys || ModBytes + data.Length > ReplayLimits.MaxModBytes ||
                ch < 0 && Channels.Count >= ReplayLimits.MaxModChannels)
            { ModKeysDropped++; return false; }
            if (ch < 0) { ch = Channels.Count; Channels.Add(channel); }
            if (ModKeys.Count > 0 && t < ModKeys[ModKeys.Count - 1].T) t = ModKeys[ModKeys.Count - 1].T;
            ModKeys.Add(new ModKey { T = t, Channel = ch, Object = obj, Data = (byte[])data.Clone() });
            ModBytes += data.Length;
            return true;
        }

        public bool Full => ObjectKeys >= ReplayLimits.MaxObjectKeys || PieceKeys >= ReplayLimits.MaxPieceKeys || Duration >= ReplayLimits.MaxSeconds;
    }

    /// Playback's view of the mod keys: for each (channel, object) that has keys, the latest one at the playhead.
    public sealed class ReplayModIndex
    {
        /// Each (channel, object) with keys, in the order of their first key.
        public readonly List<KeyValuePair<int, int>> Pairs = new List<KeyValuePair<int, int>>();
        private readonly List<List<int>> _keys = new List<List<int>>();
        private readonly ReplayRecording _rec;

        public ReplayModIndex(ReplayRecording rec)
        {
            _rec = rec;
            var at = new Dictionary<long, int>();
            for (int i = 0; i < rec.ModKeys.Count; i++)
            {
                var k = rec.ModKeys[i];
                long id = ((long)k.Channel << 32) | (uint)(k.Object + 1);
                if (!at.TryGetValue(id, out int pair)) { pair = Pairs.Count; at[id] = pair; Pairs.Add(new KeyValuePair<int, int>(k.Channel, k.Object)); _keys.Add(new List<int>()); }
                _keys[pair].Add(i);
            }
        }

        /// The pair's latest key at or before t (its index in the recording's ModKeys), or -1 before its first key.
        public int Latest(int pair, float t)
        {
            var list = _keys[pair];
            int lo = 0, hi = list.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_rec.ModKeys[list[mid]].T <= t) { found = list[mid]; lo = mid + 1; } else hi = mid - 1;
            }
            return found;
        }
    }

    /// The playhead: where in the recording playback is, whether it runs, and how fast.
    public sealed class ReplayClock
    {
        public double Time;
        public double Duration;
        public bool Playing;
        public int SpeedIndex = 3;   // 1×
        public float Speed => ReplayLimits.Speeds[SpeedIndex];

        public void Reset(double duration) { Duration = Math.Max(0, duration); Time = 0; Playing = true; SpeedIndex = 3; }

        /// Moves the playhead by a frame. Playback stops at the end rather than looping, so the last frame stays up.
        public void Advance(double dt)
        {
            if (!Playing || !ReplayMath.Finite(dt) || dt <= 0) return;
            dt = Math.Min(dt, 0.25);   // a long hitch must not skip seconds of the take
            Time += dt * Speed;
            if (Time >= Duration) { Time = Duration; Playing = false; }
        }

        public void Seek(double t) { Time = ReplayMath.Finite(t) ? Math.Max(0, Math.Min(Duration, t)) : 0; }
        public void Jump(double seconds) => Seek(Time + seconds);
        /// Play from the start again when the end was reached, otherwise pause or resume.
        public void Toggle()
        {
            if (!Playing && Time >= Duration - 1e-6) Time = 0;
            Playing = !Playing;
        }
        public void Faster() { if (SpeedIndex < ReplayLimits.Speeds.Length - 1) SpeedIndex++; }
        public void Slower() { if (SpeedIndex > 0) SpeedIndex--; }
        public bool SetSpeed(float speed)
        {
            for (int i = 0; i < ReplayLimits.Speeds.Length; i++)
                if (Math.Abs(ReplayLimits.Speeds[i] - speed) < 1e-4f) { SpeedIndex = i; return true; }
            return false;
        }

        public static string Clock(double seconds)
        {
            if (!ReplayMath.Finite(seconds) || seconds < 0) seconds = 0;
            int s = (int)Math.Floor(seconds);
            return (s / 60).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// The file format: a binary, gzip-compressed stream of numbers. There are no names, paths, chat or account ids in
    /// it: objects are save ids and item indexes, players are numbered, and an outfit is the game's own numbers. The
    /// only text is the mods' channel names ("modid/name"); what a mod puts in its keys is the mod's to answer for
    /// (docs/REPLAY.md).
    public static class ReplayCodec
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("TLDRPLAY");

        /// `format` writes an older layout (the tests' old files); the game always writes the newest.
        public static void Write(Stream stream, ReplayRecording rec, int format = ReplayRecording.Format)
        {
            if (format < 1 || format > ReplayRecording.Format) throw new ArgumentOutOfRangeException(nameof(format));
            using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                w.Write(Magic); w.Write(format); w.Write(rec.Protocol); w.Write(ReplayLimits.SampleHz);
                w.Write(rec.OriginX); w.Write(rec.OriginY); w.Write(rec.OriginZ); w.Write(rec.Duration);
                w.Write(rec.Objects.Count);
                foreach (var o in rec.Objects)
                {
                    w.Write(o.SaveId); w.Write(o.Prefab); w.Write(o.PieceCount); w.Write(o.LightCount);
                    w.Write(o.Track.Keys.Count);
                    foreach (var k in o.Track.Keys) { w.Write(k.T); w.Write(k.Parent); W(w, k.Pos); W(w, k.Rot); w.Write(k.Visible); w.Write(k.Lights); }
                    int moving = 0;
                    for (int i = 0; i < o.Pieces.Length; i++) if (o.Pieces[i] != null && o.Pieces[i].Keys.Count > 0) moving++;
                    w.Write(moving);
                    for (int i = 0; i < o.Pieces.Length; i++)
                    {
                        var p = o.Pieces[i];
                        if (p == null || p.Keys.Count == 0) continue;
                        w.Write(i); w.Write(p.Keys.Count);
                        foreach (var k in p.Keys) { w.Write(k.T); W(w, k.Pos); W(w, k.Rot); if (format >= 3) { w.Write(k.Hidden); w.Write(k.Mat); } }
                    }
                }
                w.Write(rec.Players.Count);
                foreach (var p in rec.Players)
                {
                    w.Write(p.Local); w.Write(p.Outfit);
                    w.Write(p.OutfitEnabled.Length); foreach (var e in p.OutfitEnabled) w.Write(e);
                    w.Write(p.OutfitColors.Length); foreach (var c in p.OutfitColors) w.Write(c);
                    w.Write(p.Samples.Count);
                    foreach (var s in p.Samples)
                    {
                        w.Write(s.T); w.Write(s.Visible); W(w, s.Body); W(w, s.BodyRot); W(w, s.Eye); W(w, s.EyeRot); w.Write(s.HeadPitch);
                        for (int i = 0; i < ReplayLimbs.Count; i++) { W(w, s.Limb(i)); W(w, s.LimbRot(i)); }
                    }
                }
                if (format < 2) return;
                w.Write(rec.Channels.Count);
                foreach (var c in rec.Channels) { var b = Encoding.ASCII.GetBytes(c); w.Write((byte)b.Length); w.Write(b); }
                w.Write(rec.ModKeys.Count);
                foreach (var k in rec.ModKeys) { w.Write(k.T); w.Write(k.Channel); w.Write(k.Object); w.Write(k.Data.Length); w.Write(k.Data); }
            }
        }

        private static void W(BinaryWriter w, ReplayVec v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        private static void W(BinaryWriter w, ReplayRot r) { w.Write(r.X); w.Write(r.Y); w.Write(r.Z); w.Write(r.W); }

        /// Reads a recording. Anything out of bounds (a count, a time running backwards, a non-finite number, a parent
        /// that does not exist) refuses the whole file with a FormatException.
        public static ReplayRecording Read(Stream stream)
        {
            using (var r = new BinaryReader(stream, Encoding.UTF8, true))
            {
                var magic = r.ReadBytes(Magic.Length);
                for (int i = 0; i < Magic.Length; i++) if (magic.Length != Magic.Length || magic[i] != Magic[i]) throw new FormatException("not a replay file");
                int format = r.ReadInt32();
                if (format < 1 || format > ReplayRecording.Format) throw new FormatException("replay format " + format + " is not supported");
                var rec = new ReplayRecording { Protocol = r.ReadInt32(), PieceStates = format >= 3 };
                if (r.ReadInt32() != ReplayLimits.SampleHz) throw new FormatException("unexpected sample rate");
                rec.OriginX = Fin(r.ReadDouble()); rec.OriginY = Fin(r.ReadDouble()); rec.OriginZ = Fin(r.ReadDouble());
                rec.Duration = Fin(r.ReadSingle());
                if (rec.Duration < 0 || rec.Duration > ReplayLimits.MaxSeconds + 1) throw new FormatException("bad duration");
                int objects = Count(r.ReadInt32(), ReplayLimits.MaxObjects, "objects");
                for (int n = 0; n < objects; n++)
                {
                    var o = new ReplayObject { SaveId = r.ReadUInt32(), Prefab = r.ReadInt32(), PieceCount = Count(r.ReadInt32(), ReplayLimits.MaxPiecesPerObject, "pieces"), LightCount = Count(r.ReadInt32(), ReplayLimits.MaxLightsPerObject, "lights") };
                    int keys = Count(r.ReadInt32(), ReplayLimits.MaxObjectKeys - rec.ObjectKeys, "object keys");
                    float last = -1;
                    for (int k = 0; k < keys; k++)
                    {
                        var key = new ObjKey { T = Time(r.ReadSingle(), ref last, rec.Duration), Parent = r.ReadInt32(), Pos = V(r), Rot = Q(r), Visible = r.ReadBoolean(), Lights = r.ReadUInt32() };
                        if (key.Parent < -1 || key.Parent >= objects || key.Parent == n) throw new FormatException("bad parent");
                        if (o.LightCount < 32 && key.Lights >> o.LightCount != 0) throw new FormatException("a light the object does not have");
                        o.Track.Keys.Add(key);
                    }
                    rec.ObjectKeys += keys;
                    int moving = Count(r.ReadInt32(), o.PieceCount, "moving pieces");
                    int lastPiece = -1;
                    for (int m = 0; m < moving; m++)
                    {
                        int index = r.ReadInt32();
                        if (index <= lastPiece || index >= o.PieceCount) throw new FormatException("bad piece index");
                        lastPiece = index;
                        var track = o.Piece(index);
                        int pk = Count(r.ReadInt32(), ReplayLimits.MaxPieceKeys - rec.PieceKeys, "piece keys");
                        if (pk == 0) throw new FormatException("empty piece track");
                        float plast = -1;
                        for (int k = 0; k < pk; k++)
                        {
                            var key = new PieceKey { T = Time(r.ReadSingle(), ref plast, rec.Duration), Pos = V(r), Rot = Q(r) };
                            if (format >= 3)
                            {
                                key.Hidden = r.ReadBoolean(); key.Mat = r.ReadByte();
                                if (key.Mat >= ReplayLimits.MaxMaterialSets) throw new FormatException("a material set past the limit");
                            }
                            track.Keys.Add(key);
                        }
                        rec.PieceKeys += pk;
                    }
                    rec.Objects.Add(o);
                }
                int players = Count(r.ReadInt32(), ReplayLimits.MaxPlayers, "players");
                int locals = 0;
                for (int n = 0; n < players; n++)
                {
                    var p = new ReplayPlayer { Local = r.ReadBoolean(), Outfit = r.ReadInt32() };
                    if (p.Local) locals++;
                    int en = Count(r.ReadInt32(), ReplayLimits.MaxOutfitEntries, "outfit entries");
                    p.OutfitEnabled = new bool[en]; for (int i = 0; i < en; i++) p.OutfitEnabled[i] = r.ReadBoolean();
                    int cn = Count(r.ReadInt32(), ReplayLimits.MaxOutfitEntries * 4, "outfit colours");
                    if (cn % 4 != 0) throw new FormatException("bad outfit colours");
                    p.OutfitColors = new float[cn]; for (int i = 0; i < cn; i++) p.OutfitColors[i] = Fin(r.ReadSingle());
                    int samples = Count(r.ReadInt32(), (int)(ReplayLimits.MaxSeconds * ReplayLimits.SampleHz) + 16, "player samples");
                    float last = -1;
                    for (int k = 0; k < samples; k++)
                    {
                        var s = new PlayerSample { T = Time(r.ReadSingle(), ref last, rec.Duration), Visible = r.ReadBoolean(), Body = V(r), BodyRot = Q(r), Eye = V(r), EyeRot = Q(r), HeadPitch = Fin(r.ReadSingle()) };
                        for (int i = 0; i < ReplayLimbs.Count; i++) s.SetLimb(i, V(r), Q(r));
                        p.Samples.Add(s);
                    }
                    rec.Players.Add(p);
                }
                if (locals > 1) throw new FormatException("more than one recording player");
                if (format >= 2) ReadMods(r, rec, objects);
                if (stream.CanSeek ? stream.Position != stream.Length : r.PeekChar() != -1) throw new FormatException("trailing data");
                return rec;
            }
        }

        private static void ReadMods(BinaryReader r, ReplayRecording rec, int objects)
        {
            int channels = Count(r.ReadInt32(), ReplayLimits.MaxModChannels, "replay channels");
            for (int i = 0; i < channels; i++)
            {
                int len = r.ReadByte();
                var name = Encoding.ASCII.GetString(r.ReadBytes(len));
                if (!ReplayRecording.ValidChannel(name) || rec.Channels.Contains(name)) throw new FormatException("bad replay channel name");
                rec.Channels.Add(name);
            }
            int keys = Count(r.ReadInt32(), ReplayLimits.MaxModKeys, "mod keys");
            float last = -1;
            for (int i = 0; i < keys; i++)
            {
                var k = new ModKey { T = Time(r.ReadSingle(), ref last, rec.Duration), Channel = r.ReadInt32(), Object = r.ReadInt32() };
                if (k.Channel < 0 || k.Channel >= channels) throw new FormatException("a mod key of no channel");
                if (k.Object < -1 || k.Object >= objects) throw new FormatException("a mod key of no object");
                int len = Count(r.ReadInt32(), ReplayLimits.MaxModKeyBytes, "bytes in a mod key");
                if (rec.ModBytes + len > ReplayLimits.MaxModBytes) throw new FormatException("too many mod bytes");
                k.Data = r.ReadBytes(len);
                if (k.Data.Length != len) throw new EndOfStreamException();
                rec.ModKeys.Add(k); rec.ModBytes += len;
            }
        }

        private static int Count(int n, int max, string what)
        {
            if (n < 0 || n > max) throw new FormatException("too many " + what);
            return n;
        }
        private static float Fin(float v) { if (!ReplayMath.Finite(v)) throw new FormatException("non-finite number"); return v; }
        private static double Fin(double v) { if (!ReplayMath.Finite(v)) throw new FormatException("non-finite number"); return v; }
        private static float Time(float t, ref float last, float duration)
        {
            Fin(t);
            if (t < last || t < 0 || t > duration + 1f) throw new FormatException("time runs backwards or out of range");
            last = t; return t;
        }
        private static ReplayVec V(BinaryReader r) => new ReplayVec(Fin(r.ReadSingle()), Fin(r.ReadSingle()), Fin(r.ReadSingle()));
        private static ReplayRot Q(BinaryReader r)
        {
            var q = new ReplayRot(Fin(r.ReadSingle()), Fin(r.ReadSingle()), Fin(r.ReadSingle()), Fin(r.ReadSingle()));
            float n = ReplayRot.Dot(q, q);
            if (n < 0.9f || n > 1.1f) throw new FormatException("rotation is not a unit quaternion");
            return q;
        }

        public static void WriteFile(string path, ReplayRecording rec)
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
                Write(gz, rec);
        }

        public static ReplayRecording ReadFile(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
            using (var mem = new MemoryStream())
            {
                // Decompressed, a recording is bounded by the caps; a file that inflates past them is refused.
                var buffer = new byte[81920]; int n; long total = 0;
                const long cap = 512L * 1024 * 1024;
                while ((n = gz.Read(buffer, 0, buffer.Length)) > 0) { total += n; if (total > cap) throw new FormatException("replay file is too large"); mem.Write(buffer, 0, n); }
                mem.Position = 0;
                return Read(mem);
            }
        }
    }

    /// Where recordings live and what they are called. The name is the time it was saved, never anything typed.
    public static class ReplayFiles
    {
        public const string Extension = ".tldreplay";
        public static string Name(DateTime now) => "replay-" + now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + Extension;

        /// The recordings in a folder, newest first.
        public static List<string> List(string folder)
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(folder)) return list;
                foreach (var f in Directory.GetFiles(folder, "*" + Extension)) list.Add(f);
            }
            catch { return list; }
            list.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a)));
            return list;
        }

        /// Whether a bridge-supplied file name is a plain replay name in the folder (no paths, no other files).
        public static bool SafeName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64 || !name.EndsWith(Extension, StringComparison.Ordinal)) return false;
            foreach (char c in name) if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '.')) return false;
            return name.IndexOf("..", StringComparison.Ordinal) < 0;
        }
    }

    /// Which replay bridge commands only read. The rest change what this machine shows.
    public static class ReplayCommandPolicy
    {
        public static bool IsReadOnly(string arg)
        {
            if (arg == null) return true;
            arg = arg.Trim();
            return arg.Length == 0 || arg == "status" || arg == "list" || arg == "near" || arg.StartsWith("near ", StringComparison.Ordinal);
        }
    }
}
