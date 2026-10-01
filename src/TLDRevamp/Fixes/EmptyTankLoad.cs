using System.Collections.Generic;
using HarmonyLib;

namespace TLDRevamp.Fixes
{
    /// The game saves a tank only if it holds something (save_tankscript.NeedsSave: amount > 0) and loads only the
    /// tanks it finds a record for. An item loaded from a save (or placed from the far store) whose tank was empty
    /// therefore keeps the prefab's own contents: an emptied bottle (Palack01) is full again — 30 L — whenever it
    /// streams back in or the save is reloaded.
    ///
    /// Found by tools/farworld.py (v0.57.93): the far player's empty bottle was 30 L for everyone who loaded it after.
    /// After the game's LoadStuff, every tank of the item with no record is emptied — exactly what its save says. Also
    /// used for network resyncs (Entities.Resync): an owner's emptied tank arrives as "no record" the same way.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.EmptyTankLoad.Enabled
    [HarmonyPatch(typeof(savedatascript), nameof(savedatascript.LoadStuff), new[] { typeof(savedata), typeof(uint), typeof(bool) })]
    public static class EmptyTankLoad
    {
        public static bool Enabled = true;
        public static long Emptied;

        // the game removes the item's records from the far store at the end of LoadStuff (itemData.RemoveID): which
        // tanks had one is read before (v0.57.95 read it after — every tank of every placed item went empty)
        public struct Pending { public tosaveitemscript It; public bool[] Recorded; }

        [HarmonyPrefix]
        private static void Prefix(savedatascript __instance, savedata _data, uint _key, bool _clientDontAskOnSpawn, out Pending __state)
        {
            __state = default;
            if (!Enabled || _data == null || _data.itemData == null || !__instance.items.TryGetValue(_key, out var it) || it == null || it.loaded || it.tanks == null || it.tanks.Count == 0) return;
            if (!syncScript.IsHostOrSingle() && !_clientDontAskOnSpawn) return;   // the game asks the host instead
            if (!savedatascript.IndexOfID(_data.itemData.items, _key, out _)) return;  // LoadStuff returns early
            var rec = new bool[it.tanks.Count];
            for (int i = 0; i < rec.Length; i++) rec[i] = savedatascript.IndexOfID(_data.itemData.tanks, _key, i, out _);
            __state = new Pending { It = it, Recorded = rec };
        }

        [HarmonyPostfix]
        private static void Postfix(Pending __state)
        {
            if (__state.It == null || !__state.It.loaded) return;
            for (int i = 0; i < __state.Recorded.Length && i < __state.It.tanks.Count; i++)
                if (!__state.Recorded[i]) Empty(__state.It.tanks[i]);
        }

        /// Tanks of `it` without a record in `recs` hold nothing (`recs` must still hold the item's entries).
        public static void EmptyUnrecorded(tosaveitemscript it, List<save_tankscript> recs)
        {
            if (!Enabled || it == null || it.tanks == null) return;
            for (int i = 0; i < it.tanks.Count; i++)
                if (!savedatascript.IndexOfID(recs, it.idInSave, i, out _)) Empty(it.tanks[i]);
        }

        private static void Empty(tankscript t)
        {
            if (t == null) return;
            t.FStart();
            if (t.F == null || t.F.fluids.Count == 0) return;
            t.F.fluids.Clear();
            t.Upd();
            Emptied++;
        }
    }
}
