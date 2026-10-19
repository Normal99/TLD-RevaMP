namespace TLDRevamp.Net
{
    internal static class UsableMerge
    {
        /// Save loaders refresh the geometry but omit the input flags read by radios (tune and AM/FM).
        internal static void Apply(usablescript u, float slide, float x, float y, int turn, float tuned, bool tunePath = true, bool inputEvent = false)
        {
            u.FStart();
            if (u.currentTurnState != turn || inputEvent && u.turnable && u.maxTurnState == 0) { u.UpdFromMulti(slide, x, y, turn, tuned); return; }
            float was = u.tunedFloat;
            u.tempTunedFloat = u.tunedFloat = tuned; u.Refresh(slide, x, y, turn);
            if (tunePath && u.tuneAble && tuned != was)
            {
                if (tuned > was) u.tunedUp = true; else u.tunedDown = true;
                u.tuned = true;
                u.Tuned(0f, syncinmulti: false);
            }
        }

        internal static bool Ignition(bool saved, bool hasSignal, bool signal) => hasSignal ? signal : saved;
    }
}
