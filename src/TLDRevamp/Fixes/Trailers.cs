using HarmonyLib;

namespace TLDRevamp.Fixes
{
    /// A trailer's drawbar can't be hitched in this beta. The drawbar is its own object: attachgripscript.FStart
    /// instantiates it (UtanFutoGrip: pickupable + attachablescript, no save record) and makes its part script the
    /// trailer's (tosaveitem.attachable), but nothing gives the drawbar's scripts their save record: both `tosave`
    /// fields stay empty (their own FStart looks for one on the drawbar itself), and the trailer has no pickupable, so
    /// its record's P is empty too. Carrying the drawbar to a tow ball and attaching it then throws
    /// (fpscontroller: pickedUp.tosave.attachable.Attach; attachablescript.Attach: tosave.P.…), the game's own trailers
    /// and the car trailer example mod alike (tools/cartrailer.py `--probe`: both drawbars "tosave null", the trailers
    /// "P False"; `mount hitch` threw a NullReferenceException in attachablescript.Attach).
    ///
    /// Here the drawbar's scripts get the trailer's record, and the record gets the drawbar's pickupable as its P (the
    /// record already points at the drawbar's part script, so the two describe the same object; the trailer itself has
    /// no pickupable of its own to lose).
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.TrailerGrip.Enabled (applies to trailers started after the change)
    [HarmonyPatch(typeof(attachgripscript), nameof(attachgripscript.FStart))]
    public static class TrailerGrip
    {
        public static bool Enabled = true;
        public static long Wired;
        /// Every started trailer (Entities.CarCargoTick: a car carried on a trailer goes with the car towing it).
        public static readonly System.Collections.Generic.List<attachgripscript> All = new System.Collections.Generic.List<attachgripscript>();

        [HarmonyPostfix]
        private static void Postfix(attachgripscript __instance)
        {
            All.RemoveAll(g => g == null);
            if (!All.Contains(__instance)) All.Add(__instance);
            if (!Enabled || __instance.Grip == null) return;
            var save = __instance.tosaveitem;
            if (save == null) return;
            var part = __instance.Grip.GetComponent<attachablescript>();
            var pick = __instance.Grip.GetComponent<pickupable>();
            bool any = false;
            if (part != null && part.tosave == null) { part.tosave = save; any = true; }
            if (pick != null && pick.tosave == null) { pick.FStart(save); if (pick.tosave == null) pick.tosave = save; any = true; }
            if (pick != null && save.P == null) { save.P = pick; any = true; }
            if (any) Wired++;
            TrailerLockBox.Apply(save);
        }
    }

    /// A trailer's storage areas (mountStuff: the box that physics-locks what is put in the trailer) are solid in this
    /// beta: a player can stand on the box, and an item dropped in is locked inside a collider of the trailer's own
    /// body, which pushes the trailer about (seen in a playtest, 2026-10-14). The area finds what is in it with its own
    /// overlap test (mountStuff: Physics.OverlapBox over the box's size), so the box needs no collision: here it is a
    /// trigger. Only trailers (attachgripscript); the Brakas transporter's bed is part of a rigid truck and its carried
    /// car rests on that box, so it is left as it is.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.TrailerLockBox.Enabled (applies to trailers started after the change)
    public static class TrailerLockBox
    {
        public static bool Enabled = true;
        public static long Made;

        public static void Apply(tosaveitemscript trailer)
        {
            if (!Enabled || trailer == null) return;
            foreach (var m in trailer.GetComponentsInChildren<mountStuff>(true))
                if (m.col != null && !m.col.isTrigger) { m.col.isTrigger = true; Made++; }
        }
    }
}
