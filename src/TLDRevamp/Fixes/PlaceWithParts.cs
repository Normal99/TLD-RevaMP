using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The game places a stored item when its record is near a player (itemPlaceRemoveScript.PlaceStuff, by the record's
    /// own position). A part bolted onto it is a record of its own, attached by slot: placing the part places its car
    /// (save_attachable.Load → GetOne(parent)), but placing the car never places its parts — the game relies on the
    /// parts' records lying where the car is. When a part's record is somewhere else, the car comes back without it,
    /// and every later save keeps it that way: the live car moves on, its stored parts stay behind.
    ///
    /// Playtest 2026-10-16: the joiner's Car06 came into the host's view without its wheels, doors, hood, engine…; the
    /// host's save had those parts' records still attached to the car (slot by slot) but 1–3.5 km away from it, left
    /// at earlier places of the car. Here: an item placed → every stored record attached to it is placed too (the
    /// game's GetOne, which bolts it back into its slot), parts of parts the same way.
    ///
    /// A part the streaming selected near here and is about to place (FarItemLookup's spread) is left to
    /// its own turn: placed within frames anyway, and the car's frame doesn't carry all its parts.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.PlaceWithParts.Enabled
    [HarmonyPatch(typeof(itemPlaceRemoveScript), nameof(itemPlaceRemoveScript.PlaceOne))]
    public static class PlaceWithParts
    {
        public static bool Enabled = true;
        public static float StrayM = 15f;   // a part's record this far from its car's counts as a stray (logged)
        public static long Placed, Strays, LeftToStreaming;
        public static string LastStray = "";
        private static readonly Dictionary<uint, List<uint>> _kids = new Dictionary<uint, List<uint>>();
        public static bool ReuseIndex = false;   // opt-in until tested in game. A/B: stable adjacency during recursive loads, no per-part arrays
        public static long IndexBuilds, ReusedLoads;
        private static readonly List<List<uint>> Buckets = new List<List<uint>>();
        private static int _depth;
        private static bool _snapshot;
        private static itemDataClass _store;

        [HarmonyPrefix]
        private static void Prefix()
        {
            if (_depth++ != 0) return;
            _snapshot = Enabled && ReuseIndex;
            var data = savedatascript.s != null && savedatascript.s.data != null ? savedatascript.s.data.itemData : null;
            if (_snapshot && data != null && data.attachable != null && (_store != data || _kidsFrame != Time.frameCount || _kidsCount != data.attachable.Count)) Index(data.attachable);
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            if (--_depth != 0 || !_snapshot) return;
            var data = savedatascript.s != null && savedatascript.s.data != null ? savedatascript.s.data.itemData : null;
            _store = data;
            if (data != null && data.attachable != null) _kidsCount = data.attachable.Count;
        }

        private static int _kidsFrame = -1, _kidsCount = -1;

        [HarmonyPostfix]
        private static void Postfix(tosaveitemscript __result)
        {
            if (!Enabled || __result == null || savedatascript.s == null || itemPlaceRemoveScript.s == null) return;
            var data = savedatascript.s.data != null ? savedatascript.s.data.itemData : null;
            if (data == null || data.attachable == null || data.attachable.Count == 0) return;
            if (!_snapshot && (_kidsFrame != Time.frameCount || _kidsCount != data.attachable.Count)) Index(data.attachable);
            if (!_kids.TryGetValue(__result.idInSave, out var kids)) return;
            var here = __result.transform.position;
            if (_snapshot) ReusedLoads++;
            IList<uint> children = _snapshot ? kids : (IList<uint>)kids.ToArray();
            for (int i = 0; i < children.Count; i++)
            {
                uint id = children[i];
                if (savedatascript.s.items.TryGetValue(id, out var live) && live != null) continue;
                if (FarItemLookup.Claimed(id)) { LeftToStreaming++; continue; }   // selected near here: placed in its turn
                if (!savedatascript.IndexOfID(data.items, id, out int k)) continue;
                var g = mainscript.GlobalFromUnityPos(here);
                double d = (data.items[k].transform.pos - g).magnitude;
                if (d > StrayM)
                {
                    Strays++;
                    LastStray = $"{Net.Entities.PrefabName(data.items[k].prefabID)} ({id}) {d:F0} m from {__result.name} ({__result.idInSave})";
                    if (Strays <= 20) Plugin.Log.LogInfo("part placed with its car: " + LastStray);
                }
                if (itemPlaceRemoveScript.s.GetOne(id, out _)) Placed++;   // PlaceOne again: its own parts follow
            }
        }

        private static void Index(List<save_attachable> list)
        {
            _kids.Clear();
            foreach (var bucket in Buckets) bucket.Clear();
            int used = 0;
            IndexBuilds++;
            foreach (var a in list)
            {
                if (a == null || a.attachType != 1 || a.parentid == 0) continue;
                if (!_kids.TryGetValue(a.parentid, out var l))
                {
                    if (ReuseIndex)
                    {
                        if (used == Buckets.Count) Buckets.Add(new List<uint>());
                        l = Buckets[used++];
                    }
                    else l = new List<uint>();
                    _kids[a.parentid] = l;
                }
                l.Add(a.id);
            }
            _kidsFrame = Time.frameCount; _kidsCount = list.Count;
        }
    }
}
