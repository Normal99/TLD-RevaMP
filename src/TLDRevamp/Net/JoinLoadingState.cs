using System;

namespace TLDRevamp.Net
{
    /// Pure join gate. Facts may arrive together or out of order; presentation always advances in order.
    internal sealed class JoinLoadingState
    {
        internal static readonly string[] Names = { "Connecting to the host", "Checking version and mods", "Loading the host's world", "Generating terrain and roads", "Receiving shared objects", "Placing you in the world", "Ready" };
        internal readonly double[] Ms = new double[7];
        internal bool Visible, Ready, Muted, Connected, Verified, Scene, Geometry, Objects, Placed;
        internal int Stage, Count, Total;
        internal double Started, Changed, StageAt;
        internal float PreviousVolume;
        internal string Reason = "";
        private Action<float> _volume;
        private Action<int, double> _completed;
        internal bool Stuck(double now) => Visible && now - Changed >= 60;
        internal void Begin(bool enabled, double now, float volume, Action<float> setVolume, Action<int, double> completed)
        {
            if (!enabled) return;
            Visible = Muted = true; Started = Changed = StageAt = now;
            PreviousVolume = volume; _volume = setVolume; _completed = completed;
            _volume(0);
        }
        internal void Progress(double now) { if (Visible) Changed = now; }
        internal void Observe(double now, bool connected, bool verified, bool scene, bool geometry, bool objects, bool placed, int count, int total)
        {
            if (!Visible) return;
            if (Connected != connected || Verified != verified || Scene != scene || Geometry != geometry || Objects != objects || Placed != placed || Count != count || Total != total) Progress(now);
            Connected = connected; Verified = verified; Scene = scene; Geometry = geometry; Objects = objects; Placed = placed; Count = count; Total = total;
            while (Stage < 6 && Done(Stage))
            {
                Ms[Stage] = (now - StageAt) * 1000;
                _completed?.Invoke(Stage, Ms[Stage]); Stage++; StageAt = now;
            }
            // Recheck all facts: a new generation request invalidates an earlier completed stage.
            if (Stage == 6 && Connected && Verified && Scene && Geometry && Objects && Placed)
            {
                Ready = true; Ms[6] = (now - Started) * 1000; _completed?.Invoke(6, Ms[6]); Close("ready");
            }
        }
        private bool Done(int stage) => stage == 0 ? Connected : stage == 1 ? Verified : stage == 2 ? Scene : stage == 3 ? Geometry : stage == 4 ? Objects : Placed && Connected && Verified && Scene && Geometry && Objects;
        internal void Continue(double now) { if (Stuck(now)) Close("continued"); }
        internal void Close(string reason)
        {
            Visible = false; Reason = reason;
            if (!Muted) return;
            Muted = false; _volume(PreviousVolume);
        }
    }
}
