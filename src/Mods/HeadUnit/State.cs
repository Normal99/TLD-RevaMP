using System.Collections.Generic;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace HeadUnit
{
    public enum Mode : byte { Radio = 0, Video = 1, Nav = 2 }

    public sealed class QueueItem
    {
        public string VideoId, Title;
        public float Duration;   // seconds; 0 = unknown until it plays
        public int AddedBy;
    }

    /// What everyone in one car shares on its head unit: the YouTube queue, what plays and where, its volume. Which tab
    /// a player looks at is their own (UnitBehaviour), as is the radio (the game shares that itself).
    /// Playback is a position on the session clock, never a stream:
    /// position = Playing ? AnchorPos + (SessionTime - AnchorTime) : AnchorPos. Every machine plays the video itself
    /// and keeps to that (Video.cs).
    public sealed class UnitState
    {
        public uint Car;                 // the car's id in the session (MpApi.WorldObjectId); 0 = single player
        public float Volume = 0.6f;      // unused since the volume became each player's own (Plugin.VideoVolume); kept on the wire for older units
        public readonly List<QueueItem> Queue = new List<QueueItem>();
        public int Index;                // the video playing (or paused) in Queue
        public bool Playing;
        public double AnchorTime;        // session time of the last play / pause / seek
        public float AnchorPos;          // video position then

        public const int MaxQueue = 200;

        public float Position(double now) => Playing ? AnchorPos + (float)(now - AnchorTime) : AnchorPos;
        public QueueItem Current => Index >= 0 && Index < Queue.Count ? Queue[Index] : null;

        public void Write(RecordWriter w)
        {
            w.UInt(Car); w.Float(Volume); w.VarUInt((uint)Index); w.Bool(Playing); w.Double(AnchorTime); w.Float(AnchorPos);
            w.VarUInt((uint)Queue.Count);
            foreach (var q in Queue) { w.String(q.VideoId); w.String(q.Title); w.Float(q.Duration); w.VarUInt((uint)q.AddedBy); }
        }

        public bool Read(RecordReader r)
        {
            uint car = r.UInt(); float vol = r.Float(); int index = (int)r.VarUInt(); bool playing = r.Bool(); double at = r.Double(); float pos = r.Float();
            uint n = r.VarUInt();
            if (r.Bad || n > MaxQueue || float.IsNaN(vol) || float.IsNaN(pos) || double.IsNaN(at)) return false;
            var q = new List<QueueItem>();
            for (uint i = 0; i < n; i++)
            {
                var it = new QueueItem { VideoId = r.String(), Title = r.String(), Duration = r.Float(), AddedBy = (int)r.VarUInt() };
                if (r.Bad || !Ytdlp.ValidVideoId(it.VideoId)) return false;
                q.Add(it);
            }
            Car = car; Volume = Mathf.Clamp01(vol); Index = index; Playing = playing; AnchorTime = at; AnchorPos = Mathf.Max(0f, pos);
            Queue.Clear(); Queue.AddRange(q);
            return true;
        }
    }

    /// The edits a player can ask the unit's owner for (UnitStateType.OnEdit).
    public enum Op : byte { Volume = 1, Play = 2, Pause = 3, Seek = 4, Next = 5, Prev = 6, Add = 7, Remove = 8, Jump = 9, Clear = 11, Ended = 12 }

    /// One shared object per car with a head unit in use: it carries the UnitState. It has no body of its own (an
    /// empty GameObject); the unit in the car's radio slot finds it by the car's id. Whoever first uses a unit in a
    /// car makes it; edits from everyone go to its owner, who applies them and sends the new record.
    public sealed class UnitStateType : SharedType
    {
        public override string Name => "unitstate";
        public override int MaxRecordBytes => 32 * 1024;   // 200 queued videos with titles

        public override GameObject Create(RecordReader record, Vector3 position, Quaternion rotation)
        {
            var go = new GameObject("HeadUnitState");
            var holder = go.AddComponent<StateHolder>();
            if (!holder.State.Read(record)) { Object.Destroy(go); return null; }
            Object.DontDestroyOnLoad(go);
            return go;
        }

        public override void Write(GameObject obj, RecordWriter record) => obj.GetComponent<StateHolder>().State.Write(record);

        public override void Read(GameObject obj, RecordReader record)
        {
            var h = obj.GetComponent<StateHolder>();
            var copy = new UnitState();
            if (copy.Read(record)) { h.State = copy; h.Changed = true; }
        }

        public override bool OnEdit(SharedObject obj, RecordReader edit, int fromPlayer)
        {
            var h = obj.GameObject.GetComponent<StateHolder>();
            bool ok = Apply(h.State, edit, fromPlayer, MpApi.SessionTime);
            if (ok) h.Changed = true;
            return ok;
        }

        /// Apply one edit (the owner, or this machine alone). False: refused, nothing changed.
        internal static bool Apply(UnitState s, RecordReader e, int from, double now)
        {
            var op = (Op)e.Byte();
            switch (op)
            {
                case Op.Volume:
                {
                    float v = e.Float();
                    if (e.Bad || float.IsNaN(v)) return false;
                    s.Volume = Mathf.Clamp01(v);
                    return true;
                }
                case Op.Play:
                    if (s.Current == null || s.Playing) return false;
                    s.AnchorTime = now; s.Playing = true;   // AnchorPos stays: on from where it paused
                    return true;
                case Op.Pause:
                    if (!s.Playing) return false;
                    s.AnchorPos = s.Position(now); s.AnchorTime = now; s.Playing = false;
                    return true;
                case Op.Seek:
                {
                    float to = e.Float();
                    if (e.Bad || to < 0f || s.Current == null) return false;
                    if (s.Current.Duration > 0f) to = Mathf.Min(to, s.Current.Duration);
                    s.AnchorPos = to; s.AnchorTime = now;
                    return true;
                }
                case Op.Next: return JumpTo(s, s.Index + 1, now);
                case Op.Prev:
                    // the first seconds: the one before; later: this one from the start
                    if (s.Position(now) > 5f || s.Index == 0) { s.AnchorPos = 0f; s.AnchorTime = now; return true; }
                    return JumpTo(s, s.Index - 1, now);
                case Op.Jump:
                {
                    int i = (int)e.VarUInt();
                    return !e.Bad && JumpTo(s, i, now);
                }
                case Op.Ended:
                {
                    // a viewer's video ended: only counts for the video still current (two viewers end the same one)
                    int i = (int)e.VarUInt();
                    if (e.Bad || i != s.Index || !s.Playing) return false;
                    // not long before the end the owner knows of (a viewer's player that misreports its length)
                    if (s.Current != null && s.Current.Duration > 0f && s.Position(now) < s.Current.Duration - 3f) return false;
                    if (s.Index + 1 < s.Queue.Count) return JumpTo(s, s.Index + 1, now);
                    s.Playing = false; s.AnchorPos = s.Current?.Duration ?? 0f; s.AnchorTime = now;
                    return true;
                }
                case Op.Add:
                {
                    uint n = e.VarUInt();
                    if (e.Bad || n == 0 || n > 100) return false;
                    bool wasEmpty = s.Queue.Count == 0 || s.Index >= s.Queue.Count;
                    int firstNew = s.Queue.Count;
                    for (uint k = 0; k < n && s.Queue.Count < UnitState.MaxQueue; k++)
                    {
                        var it = new QueueItem { VideoId = e.String(), Title = e.String(), Duration = e.Float(), AddedBy = from };
                        if (e.Bad || !Ytdlp.ValidVideoId(it.VideoId)) return false;
                        if (it.Title.Length > 120) it.Title = it.Title.Substring(0, 120);
                        s.Queue.Add(it);
                    }
                    if (wasEmpty && firstNew < s.Queue.Count) { s.Index = firstNew; s.AnchorPos = 0f; s.AnchorTime = now; s.Playing = true; }
                    return true;
                }
                case Op.Remove:
                {
                    int i = (int)e.VarUInt();
                    if (e.Bad || i < 0 || i >= s.Queue.Count) return false;
                    s.Queue.RemoveAt(i);
                    if (i < s.Index) s.Index--;
                    else if (i == s.Index) { s.AnchorPos = 0f; s.AnchorTime = now; if (s.Index >= s.Queue.Count) s.Playing = false; }
                    return true;
                }
                case Op.Clear:
                    s.Queue.Clear(); s.Index = 0; s.Playing = false; s.AnchorPos = 0f; s.AnchorTime = now;
                    return true;
            }
            return false;
        }

        private static bool JumpTo(UnitState s, int i, double now)
        {
            if (i < 0 || i >= s.Queue.Count) return false;
            s.Index = i; s.AnchorPos = 0f; s.AnchorTime = now; s.Playing = true;
            return true;
        }
    }

    /// Holds a car's UnitState on this machine (the shared object's GameObject).
    public sealed class StateHolder : MonoBehaviour
    {
        public UnitState State = new UnitState();
        public bool Changed;   // the unit redraws when set
    }
}
