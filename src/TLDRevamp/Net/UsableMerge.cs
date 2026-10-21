namespace TLDRevamp.Net
{
    internal static class UsableMerge
    {
        /// UpdFromMulti also calls JustTurn unconditionally. Render continuous building motion through Refresh;
        /// preserve only its endpoint sounds, using the actual previous rendered value (never packet targets).
        internal static void ApplyBuilding(usablescript u, float slide, float x, float y, int turn, float tuned, bool inputEvent)
        {
            u.FStart();
            float oldSlide = u.slideValue, oldX = u.xRot, oldY = u.yRot;
            // Refresh calls Slide(0)/Rot(0), whose endpoint hooks run even with sync=false. Suppress
            // those zero-volume duplicates; below we supply the real rendered delta exactly once.
            u.lastSlideValue = slide; u.lastXrot = x; u.lastYrot = y;
            Apply(u, slide, x, y, turn, tuned, inputEvent: inputEvent);
            if (inputEvent) return; // discrete receipt uses the current continuous pose
            u.SlideSound(u.slideValue, oldSlide, System.Math.Abs(u.slideValue - oldSlide));
            RotationEdges(u, oldX, u.xRot, u.minX, u.maxX);
            RotationEdges(u, oldY, u.yRot, u.minY, u.maxY);
        }
        private static void RotationEdges(usablescript u, float before, float after, float min, float max)
        {
            int edges = UsableEdges.Changed(before, after, min, max);
            float delta = System.Math.Abs(after - before);
            if ((edges & UsableEdges.EnterMin) != 0 && u.startPosEnd != null) u.StartEndStuff(u.startPosEnd, delta, true);
            if ((edges & UsableEdges.EnterMax) != 0 && u.endPosEnd != null) u.StartEndStuff(u.endPosEnd, delta, true);
            if ((edges & UsableEdges.LeaveMin) != 0 && u.startPosStart != null) u.StartEndStuff(u.startPosStart, delta, false);
            if ((edges & UsableEdges.LeaveMax) != 0 && u.endPosStart != null) u.StartEndStuff(u.endPosStart, delta, false);
        }
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
