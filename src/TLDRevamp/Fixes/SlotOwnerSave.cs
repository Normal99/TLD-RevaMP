using HarmonyLib;

namespace TLDRevamp.Fixes
{
    /// The game records what a part is attached to by searching UP from its attach point for a tosaveitemscript
    /// (attachablescript.AttachedToTosave). A part slot can sit on a body outside its owner's hierarchy — Bus01's rear
    /// wheel slots are on BusBack (its own rigidbody on a joint to BusFront, no tosaveitemscript) though they are listed
    /// in BusFront's partslotscripts — so the search finds nothing and the record says "attached to the world at this
    /// point" (type 4). Loaded back (a save, the far store, a shared copy) the rear wheels hang in the air where the bus
    /// stood: the bus drives off without them, and a player driving that copy has a rear section dragging on the ground.
    ///
    /// Found by tools/busdrive.py (2026-10-08): the joining player's bus had both rear wheels on a free attachPoint; when
    /// they took the wheel it couldn't move (the user saw a wheel fall off). Here: a part in a slot whose owner the
    /// search missed is recorded as what it is — slot `index` of the slot's own item (type 1, index type 2), which the
    /// game's loader crafts back into that slot. Readable by the unmodded game too.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.SlotOwnerSave.Enabled
    [HarmonyPatch(typeof(save_attachable), nameof(save_attachable.Save), new[] { typeof(attachablescript), typeof(uint) })]
    public static class SlotOwnerSave
    {
        public static bool Enabled = true;
        public static long Fixed;

        /// What the part is attached to: the game's search, or — for a slot outside its owner's hierarchy — the slot's item.
        public static tosaveitemscript Owner(attachablescript at)
        {
            if (at == null || !at.attached) return null;
            var t = at.AttachedToTosave();
            if (t != null || !Enabled) return t;
            var s = at.slot;
            return s != null && s.tosaveitem != null && s.tosaveitem.partslotscripts != null && s.tosaveitem.partslotscripts.Contains(s) ? s.tosaveitem : null;
        }

        [HarmonyPostfix]
        private static void Postfix(save_attachable __instance, attachablescript _script)
        {
            if (!Enabled || __instance.attachType != 4 || _script == null || _script.point == null || _script.AttachedToTosave() != null) return;
            var owner = Owner(_script);
            if (owner == null || owner == _script.tosave) return;
            __instance.attachType = 1;
            __instance.indexType = 2;
            __instance.parentid = owner.idInSave;
            __instance.index = owner.partslotscripts.IndexOf(_script.slot);
            var lp = owner.transform.InverseTransformPoint(_script.point.position);
            __instance.lpos = new UnityEngine.Vector3d(lp.x, lp.y, lp.z);
            Fixed++;
        }
    }
}
