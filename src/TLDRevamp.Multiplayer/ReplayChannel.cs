using System;
using UnityEngine;

namespace TLDRevamp.Multiplayer
{
    /// A mod's own data in TLD Revamp's replays (API 4; docs/MODDING-MULTIPLAYER.md, docs/REPLAY.md). A replay records
    /// where the game's objects are and how they look; what a mod draws on them (a picture on a screen, a sign's text)
    /// it records itself, through a channel:
    ///
    ///   var screen = MpApi.RegisterReplayChannel(mod, "screen");
    ///   screen.RecordingStarted += () => { foreach (var tv in tvs) screen.Record(tv.gameObject, tv.Snapshot()); };
    ///   // whenever what a TV shows changes, while recording:
    ///   if (screen.Recording) screen.Record(tv.gameObject, tv.Snapshot());
    ///   // playback:
    ///   screen.Shown += key => ShowOnCopy(key);
    ///   screen.PlaybackEnded += PutBack;
    ///
    /// A key is the whole state of what the mod shows for one object from that moment on (never a change to the one
    /// before): playback hands each object its latest key at the playhead, also after a jump back. Recordings are
    /// local files, never sent; keep keys small and leave out what the player wouldn't want saved (docs/REPLAY.md).
    public sealed class ReplayChannel
    {
        public string Key { get; internal set; }
        public ModInfo Mod { get; internal set; }

        /// TLD Revamp records a replay now.
        public bool Recording => MpApi.Replays != null && MpApi.Replays.Recording;
        /// A replay plays now (the world shows copies of the recorded objects).
        public bool Playing => MpApi.Replays != null && MpApi.Replays.Playing;

        /// Records a key: what the mod shows for `about` (one of the game's saved objects: a car, an item; null: for no
        /// object) from now on. False when nothing records, the object is not in the recording (too far from every
        /// player), or a limit was reached (a key over 256 KB; 96 MB of mod keys in one recording).
        public bool Record(GameObject about, byte[] data)
        {
            if (data == null || MpApi.Replays == null || !MpApi.Replays.Recording) return false;
            return MpApi.Replays.Record(this, about, data);
        }

        /// Recording started: record what every object shows now (the first key of each).
        public event Action RecordingStarted;
        /// Playback: an object's latest key changed at the playhead (it played past a key, or the playhead jumped).
        public event Action<ReplayShown> Shown;
        /// Playback ended: put back whatever Shown changed. The copies are gone by then.
        public event Action PlaybackEnded;

        internal void RaiseRecordingStarted() => Raise(RecordingStarted, "RecordingStarted");
        internal void RaisePlaybackEnded() => Raise(PlaybackEnded, "PlaybackEnded");
        internal void RaiseShown(ReplayShown shown)
        {
            try { Shown?.Invoke(shown); }
            catch (Exception e) { MpApi.Backend?.Log("mods: replay channel " + Key + " Shown threw: " + e); }
        }
        private void Raise(Action a, string what)
        {
            try { a?.Invoke(); }
            catch (Exception e) { MpApi.Backend?.Log("mods: replay channel " + Key + " " + what + " threw: " + e); }
        }

        public override string ToString() => Key;
    }

    /// One object's key at the playhead (ReplayChannel.Shown).
    public readonly struct ReplayShown
    {
        /// The copy that stands in for the recorded object during playback (null: the key is for no object, or the
        /// object could not be drawn). The copy is made of plain renderers, one per renderer of the object.
        public readonly GameObject Copy;
        /// The game's own object, when it is in this world (the same save); hidden while the replay plays.
        public readonly GameObject Live;
        /// The key's bytes; null before the object's first key (show the object as it is by default).
        public readonly byte[] Data;
        /// The key's time in the recording, in seconds (0 before the first key).
        public readonly float Time;
        /// The item index of the recorded object (the game's itemdatabase), -1 for none.
        public readonly int Item;

        public ReplayShown(GameObject copy, GameObject live, byte[] data, float time, int item)
        {
            Copy = copy; Live = live; Data = data; Time = time; Item = item;
        }

        /// The copy's renderer that stands in for one of the object's renderers (`live` on the game's object, or a
        /// renderer of the item's prefab with the same mesh), or null.
        public Renderer CopyOf(Renderer live) => Copy != null && live != null && MpApi.Replays != null ? MpApi.Replays.CopyOf(Copy, live) : null;
    }
}
