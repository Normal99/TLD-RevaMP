using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TLDRevamp.Replay
{
    /// The replay's 1:1 check (tools/replay.py fidelity). While a recording runs with `replay truth <seconds>` set, the
    /// recorder also writes down, every few seconds, what the world really showed at that sample: each tracked
    /// object's pose, whether it was shown, its lights, and for the objects near a player each piece's world pose,
    /// whether its renderer drew and which materials it drew with; and every player's body and limb targets. It stays
    /// in memory with the take (never in the file).
    ///
    /// `replay verify` then plays the take from memory, seeks to each of those moments and compares the copies with
    /// what was really there. Read-only on the world; the viewer's own copies are moved to the moments as a seek would.
    internal sealed class ReplayTruth
    {
        internal sealed class Obj
        {
            public int Index;
            public Vector3d Pos;
            public Quaternion Rot;
            public bool Visible;
            public uint Lights;
            public Vector3d[] PiecePos;     // null: the object was not near a player (its pieces are not recorded then)
            public bool[] PieceDrawn;       // the game's renderer drew: enabled and in an active object
            public int[] PieceMaterials;    // a hash of the renderer's materials
            public bool[] PieceLive;        // the renderer showed a live view (a mirror's glass): a copy leaves it out
            public int Unlooked;            // renderers of the object's own that the look does not have (off when it was captured)
            public Vector3d[] BonePos;      // the bones skinned pieces are drawn with (null like PiecePos)
        }

        internal sealed class Player
        {
            public int Index;
            public bool Visible;
            public Vector3d Body;
            public Vector3d[] Limbs = new Vector3d[ReplayLimbs.Count];
        }

        public float T;
        public readonly List<Obj> Objects = new List<Obj>();
        public readonly List<Player> Players = new List<Player>();

        internal const int MaxFrames = 120;

        internal static int Materials(Material[] m)
        {
            unchecked
            {
                int h = 17;
                if (m != null) foreach (var x in m) h = h * 31 + (x != null ? x.GetInstanceID() : 0);
                return h;
            }
        }

        /// The renderers an object draws of its own (its nested objects draw their own), as ReplayLook.Capture walks them.
        internal static int OwnRenderers(Transform root, HashSet<Renderer> looked)
        {
            int n = 0;
            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                if (t != root && (ReplayRecorder.Nested(t) || t.GetComponent<fpscontroller>() != null || t.GetComponent<mpplayerscript>() != null)) continue;
                foreach (var r in t.GetComponents<Renderer>())
                    if (r != null && r.enabled && r.gameObject.activeInHierarchy && !looked.Contains(r) && !(r is ParticleSystemRenderer) && !(r is LineRenderer) && !(r is TrailRenderer)) n++;
                for (int i = 0; i < t.childCount; i++) stack.Push(t.GetChild(i));
            }
            return n;
        }
    }

    internal static partial class ReplayRecorder
    {
        /// Seconds between two truth frames while recording; 0: none (the default; only the 1:1 check sets it).
        internal static float TruthEvery;
        private static readonly List<ReplayTruth> _truth = new List<ReplayTruth>();
        private static double _nextTruth;

        private static void ResetTruth() { _truth.Clear(); _nextTruth = 0; }

        private static void Truth(float t, double now)
        {
            if (TruthEvery <= 0 || _truth.Count >= ReplayTruth.MaxFrames || now < _nextTruth) return;
            _nextTruth = now + TruthEvery;
            var f = new ReplayTruth { T = t };
            for (int i = 0; i < _objects.Count; i++)
            {
                var tr = _objects[i];
                if (!tr.Active || tr.It == null) continue;
                var o = new ReplayTruth.Obj
                {
                    Index = i, Pos = mainscript.GlobalFromUnityPos(tr.T.position), Rot = tr.T.rotation,
                    Visible = tr.It.gameObject.activeInHierarchy, Lights = tr.Look.LightMask(),
                };
                float radius = tr.Look.Bones.Count > 0 ? ReplayLimits.BoneRadius : ReplayLimits.PieceRadius;
                if (o.Visible && tr.Look.Tracks > 0 && Nearest(tr.T.position) <= radius)
                {
                    o.BonePos = new Vector3d[tr.Look.Bones.Count];
                    for (int b = 0; b < o.BonePos.Length; b++)
                        if (tr.Look.Bones[b].SourceT != null) o.BonePos[b] = mainscript.GlobalFromUnityPos(tr.Look.Bones[b].SourceT.position);
                    int n = tr.Look.Pieces.Count;
                    o.PiecePos = new Vector3d[n]; o.PieceDrawn = new bool[n]; o.PieceMaterials = new int[n]; o.PieceLive = new bool[n];
                    var looked = new HashSet<Renderer>();
                    if (tr.Look.LeftOut != null) foreach (var r in tr.Look.LeftOut) if (r != null) looked.Add(r);
                    for (int p = 0; p < n; p++)
                    {
                        var piece = tr.Look.Pieces[p];
                        if (piece.SourceT == null || piece.Source == null) continue;
                        looked.Add(piece.Source);
                        o.PiecePos[p] = mainscript.GlobalFromUnityPos(piece.SourceT.position);
                        o.PieceDrawn[p] = piece.Source.enabled && piece.Source.gameObject.activeInHierarchy;
                        var m = piece.Source.sharedMaterials;
                        o.PieceMaterials[p] = ReplayTruth.Materials(m);
                        o.PieceLive[p] = ReplayLook.ShowsLiveView(m);
                    }
                    o.Unlooked = ReplayTruth.OwnRenderers(tr.T, looked);
                }
                f.Objects.Add(o);
            }
            // the players as the recorder sampled them this moment: the local player first, then the remote bodies
            var me = _player;
            if (me != null) f.Players.Add(TruthPlayer(0, me.anim, !me.died, me.transform));
            foreach (var kv in Net.RemotePlayers.ShownBodies())
            {
                if (kv.Value == null || !_remoteIndex.TryGetValue(kv.Key, out int index)) continue;
                var mp = kv.Value.GetComponent<mpplayerscript>();
                f.Players.Add(TruthPlayer(index, mp != null ? mp.anim : null, kv.Value.gameObject.activeInHierarchy, kv.Value));
            }
            _truth.Add(f);
        }

        private static ReplayTruth.Player TruthPlayer(int index, playeranimationscript a, bool visible, Transform fallback)
        {
            var T = a != null ? (a.T != null ? a.T : a.transform) : fallback;
            var p = new ReplayTruth.Player { Index = index, Visible = visible, Body = mainscript.GlobalFromUnityPos(T.position) };
            for (int i = 0; i < ReplayLimbs.Count; i++)
            {
                var target = a != null ? Limb(a, i) : null;
                p.Limbs[i] = mainscript.GlobalFromUnityPos(target != null ? target.position : T.position);
            }
            return p;
        }
    }

    internal static partial class ReplayViewer
    {
        private sealed class Worst
        {
            private readonly List<KeyValuePair<double, string>> _rows = new List<KeyValuePair<double, string>>();
            public int Count, Over;
            public double Max, Sum;
            private readonly double _limit;
            public Worst(double limit) { _limit = limit; }
            public void Add(double error, Func<string> row)
            {
                Count++; Sum += error; if (error > Max) Max = error;
                if (error <= _limit) return;
                Over++;
                if (_rows.Count < 12) { _rows.Add(new KeyValuePair<double, string>(error, row())); return; }
                int min = 0; for (int i = 1; i < _rows.Count; i++) if (_rows[i].Key < _rows[min].Key) min = i;
                if (error > _rows[min].Key) _rows[min] = new KeyValuePair<double, string>(error, row());
            }
            public string Json(string unit)
            {
                _rows.Sort((a, b) => b.Key.CompareTo(a.Key));
                var sb = new StringBuilder("{\"checked\":").Append(Count).Append(",\"over\":").Append(Over)
                    .Append(",\"limit\":").Append(TLDRevamp.Json.Num(_limit, "F3")).Append(",\"unit\":").Append(TLDRevamp.Json.Str(unit))
                    .Append(",\"max\":").Append(TLDRevamp.Json.Num(Max, "F4")).Append(",\"mean\":").Append(TLDRevamp.Json.Num(Count > 0 ? Sum / Count : 0, "F4")).Append(",\"worst\":[");
                for (int i = 0; i < _rows.Count; i++) { if (i > 0) sb.Append(','); sb.Append(_rows[i].Value); }
                return sb.Append("]}").ToString();
            }
        }

        private static double Dist(Vector3d a, Vector3d b) { double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z; return Math.Sqrt(dx * dx + dy * dy + dz * dz); }

        /// Plays the take just recorded with truth frames, seeks to each and compares. Leaves the replay paused at the
        /// last frame checked.
        internal static string Verify()
        {
            var take = ReplayRecorder.Last;
            if (take == null || take.Truth == null || take.Truth.Count == 0) return Error("no recording with truth frames (replay truth <s>, then record)");
            if (!Active || _take != take)
            {
                string started = Play(take.Recording, take, take.File ?? "last");
                if (!Active) return started;
            }
            _clock.Playing = false;
            var pose = new Worst(0.05); var turn = new Worst(2.0); var shown = new Worst(0.5); var lights = new Worst(0.5);
            var pieces = new Worst(0.05); var bones = new Worst(0.05); var drawn = new Worst(0.5); var mats = new Worst(0.5); var unlooked = new Worst(0.5);
            var bodies = new Worst(0.05); var limbs = new Worst(0.05);
            foreach (var f in take.Truth)
            {
                _clock.Seek(f.T);
                Apply();
                float t = f.T;
                foreach (var o in f.Objects)
                {
                    if (o.Index >= _proxies.Count) continue;
                    var proxy = _proxies[o.Index];
                    var ro = _rec.Objects[o.Index];
                    string Row(string what) => "{\"t\":" + Json.Num(t, "F2") + ",\"object\":" + o.Index + ",\"item\":" + ro.Prefab + ",\"what\":" + Json.Str(what) + "}";
                    bool copyShown = proxy.Root != null && proxy.Root.activeInHierarchy;
                    shown.Add(copyShown == o.Visible ? 0 : 1, () => Row("shown: real " + o.Visible + ", copy " + copyShown));
                    if (!o.Visible || !copyShown) continue;
                    var at = mainscript.GlobalFromUnityPos(proxy.Root.transform.position);
                    double d = Dist(at, o.Pos);
                    pose.Add(d, () => Row("root off by " + d.ToString("F3") + " m"));
                    float a = Quaternion.Angle(proxy.Root.transform.rotation, o.Rot);
                    turn.Add(a, () => Row("root turned " + a.ToString("F1") + "°"));
                    uint copyLights = 0;
                    for (int i = 0; i < proxy.Lights.Length; i++) if (proxy.Lights[i] != null && proxy.Lights[i].enabled) copyLights |= 1u << i;
                    lights.Add(copyLights == o.Lights ? 0 : 1, () => Row("lights: real " + Convert.ToString(o.Lights, 2) + ", copy " + Convert.ToString(copyLights, 2) +
                        " (" + proxy.Lights.Length + " copied of " + ro.LightCount + ")"));
                    if (o.PiecePos == null) continue;
                    unlooked.Add(o.Unlooked, () => Row(o.Unlooked + " renderer(s) drawn live that the copy does not have"));
                    var look = o.Index < _looks.Count ? _looks[o.Index] : null;
                    for (int p = 0; p < o.PiecePos.Length && p < proxy.Pieces.Length; p++)
                    {
                        var r = proxy.Renderers[p];
                        if (r == null) continue;
                        string mesh = look != null && p < look.Pieces.Count && look.Pieces[p].Mesh != null ? look.Pieces[p].Mesh.name : "";
                        bool copyDrawn = r.enabled && r.gameObject.activeInHierarchy;
                        int pi = p;
                        if (o.PieceLive[p]) continue;   // a mirror's live glass: left out of every copy on purpose
                        drawn.Add(copyDrawn == o.PieceDrawn[p] ? 0 : 1, () => Row("piece " + pi + " (" + mesh + ") drawn: real " + o.PieceDrawn[pi] + ", copy " + copyDrawn));
                        int copyMats = ReplayTruth.Materials(r.sharedMaterials);
                        mats.Add(copyMats == o.PieceMaterials[p] ? 0 : 1, () => Row("piece " + pi + " (" + mesh + ") materials differ"));
                        if (!o.PieceDrawn[p]) continue;
                        double pd = Dist(mainscript.GlobalFromUnityPos(proxy.Pieces[p].position), o.PiecePos[p]);
                        pieces.Add(pd, () => Row("piece " + pi + " (" + mesh + ") off by " + pd.ToString("F3") + " m"));
                    }
                    for (int bi = 0; o.BonePos != null && bi < o.BonePos.Length && bi < proxy.Bones.Length; bi++)
                    {
                        if (proxy.Bones[bi] == null) continue;
                        double bd = Dist(mainscript.GlobalFromUnityPos(proxy.Bones[bi].position), o.BonePos[bi]);
                        string bone = look != null && bi < look.Bones.Count && look.Bones[bi].SourceT != null ? look.Bones[bi].SourceT.name : "";
                        bones.Add(bd, () => Row("bone " + bone + " off by " + bd.ToString("F3") + " m"));
                    }
                }
                foreach (var pl in f.Players)
                {
                    if (pl.Index >= _bodies.Count) continue;
                    var b = _bodies[pl.Index];
                    bool on = b.Go != null && b.Go.activeInHierarchy;
                    string Row(string what) => "{\"t\":" + Json.Num(t, "F2") + ",\"player\":" + pl.Index + ",\"what\":" + Json.Str(what) + "}";
                    if (on != pl.Visible) { shown.Add(1, () => Row("player shown: real " + pl.Visible + ", copy " + on)); continue; }
                    if (!on) continue;
                    var a = b.Mp != null ? b.Mp.anim : null;
                    var T = a != null && a.T != null ? a.T : (a != null ? a.transform : b.Go.transform);
                    double bd = Dist(mainscript.GlobalFromUnityPos(T.position), pl.Body);
                    bodies.Add(bd, () => Row("body off by " + bd.ToString("F3") + " m"));
                    if (a == null) continue;
                    for (int i = 0; i < ReplayLimbs.Count; i++)
                    {
                        var target = ReplayRecorder.Limb(a, i);
                        if (target == null) continue;
                        double ld = Dist(mainscript.GlobalFromUnityPos(target.position), pl.Limbs[i]);
                        int li = i;
                        limbs.Add(ld, () => Row("limb " + li + " off by " + ld.ToString("F3") + " m"));
                    }
                }
            }
            return "{\"frames\":" + take.Truth.Count + ",\"shown\":" + shown.Json("mismatch") + ",\"pose\":" + pose.Json("m") + ",\"turn\":" + turn.Json("deg") +
                   ",\"lights\":" + lights.Json("mismatch") + ",\"pieces\":" + pieces.Json("m") + ",\"bones\":" + bones.Json("m") + ",\"drawn\":" + drawn.Json("mismatch") +
                   ",\"materials\":" + mats.Json("mismatch") + ",\"unlooked\":" + unlooked.Json("renderers") +
                   ",\"bodies\":" + bodies.Json("m") + ",\"limbs\":" + limbs.Json("m") + "}";
        }
    }
}
