using TLDRevamp.Multiplayer;
using UnityEngine;

namespace TLDRevamp.Replay
{
    /// The replays as mods see them (MpApi replay channels, API 4): a mod records keys about the game's objects while
    /// a replay records, and is handed them back at the playhead while one plays (ReplayRecorder.RecordMod,
    /// ReplayViewer.ApplyMods).
    internal sealed class ReplayMods : MpApi.IReplayBackend
    {
        private static readonly ReplayMods I = new ReplayMods();
        public static void Install() { MpApi.Replays = I; }

        public bool Recording => ReplayRecorder.Recording;
        public bool Playing => ReplayViewer.Active;
        public bool Record(ReplayChannel channel, GameObject about, byte[] data) => ReplayRecorder.RecordMod(channel.Key, about, data);
        public Renderer CopyOf(GameObject copy, Renderer live) => ReplayViewer.CopyOf(copy, live);
    }
}
