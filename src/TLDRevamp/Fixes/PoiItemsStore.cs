using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// A building that streams out (poiGenScript.RemovePoi) is destroyed with everything under it. Items in its sockets
    /// (lamps in a building's lamp holders: attached toPOI, children of the building) are saved only if the game's item
    /// streaming (itemPlaceRemoveScript.RemoveStuff, once a second, at itemRemoveDist) stored them first; the building's
    /// own save (save_poi) keeps usables and tanks, not socket items. When the building goes first, its socket items are
    /// destroyed unsaved — and because the building's fixed spawn is done, they never come back.
    ///
    /// Found by tools/farworld.py (v0.57.93): a far player left a building, the 3 lamps of it were gone from its own
    /// save and from the host's. Before the building is destroyed, its registered items are stored exactly as
    /// RemoveStuff would have stored them (tosaveitemscript.Remove → the far store); items already stored are no-ops.
    ///
    /// Loose items standing in it the same: after a teleport the building went within the second before the item
    /// streaming ran, and the items in it fell for that long before being stored — each machine that left the site saved
    /// them 0.7–4.4 m lower (farworld runs 7 and 10: the far store's average height dropped 0.25 m, then 0.45 m, per
    /// departure; everything at the site "misplaced" by the same drop). Items inside the building's collider bounds the
    /// game is about to stream out anyway (beyond itemRemoveDist of every generation centre) are stored first.
    /// Driving never gets here (buildings go at ~9.4 km, items at 300 m); teleports and long joins do.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.PoiItemsStore.Enabled
    [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.RemovePoi))]
    public static class PoiItemsStore
    {
        public static bool Enabled = true;
        public static long Stored, Buildings, LooseStored;
        private static readonly List<tosaveitemscript> _loose = new List<tosaveitemscript>();
        private static readonly List<tosaveitemscript> _items = new List<tosaveitemscript>();

        [HarmonyPrefix, HarmonyPriority(Priority.Low)]   // after ItemSpawnSpread's flush: spread spawns exist by now
        private static void Prefix(poiGenScript.poiClass _poi)
        {
            if (!Enabled || _poi == null || _poi.pobj == null || _poi.pobj.isFixPOI || savedatascript.s == null) return;
            _items.Clear();
            _poi.pobj.GetComponentsInChildren(true, _items);
            int n = 0;
            foreach (var it in _items)
            {
                if (it == null || it.preDestroyed || !savedatascript.s.items.TryGetValue(it.idInSave, out var reg) || reg != it) continue;
                it.Remove();
                n++;
            }
            if (n > 0) { Stored += n; Buildings++; }
            _items.Clear();
            // loose items standing in it
            var cols = _poi.pobj.cols;
            if (cols == null || cols.Count == 0) return;
            bool any = false; var b = new Bounds();
            foreach (var c in cols)
            {
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            if (!any) return;
            b.Expand(2f);
            _loose.Clear();
            foreach (var it in savedatascript.s.items.Values)
            {
                if (it == null || it.preDestroyed || it.transform.parent != null || !b.Contains(it.transform.position)) continue;
                if (!Net.Entities.FarFromHere(mainscript.GlobalFromUnityPos(it.transform.position))) continue;
                _loose.Add(it);
            }
            // as itemPlaceRemoveScript.RemoveStuff does: every item of the hierarchy stored, the object destroyed
            foreach (var it in _loose)
            {
                if (it == null || it.preDestroyed) continue;
                foreach (var c in it.GetComponentsInChildren<tosaveitemscript>(true)) c.Remove();
                Object.Destroy(it.gameObject);
                LooseStored++;
            }
            _loose.Clear();
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"stored\":" + Stored + ",\"buildings\":" + Buildings + ",\"looseStored\":" + LooseStored + "}";

        /// Every building generator's distances (bridge `poigens`): which ones go before their items are stored.
        public static string Gens()
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.poiGens == null) return "{\"error\":\"no map\"}";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var g in map.poiGens)
                if (g != null) rows.Add("[" + Json.Str(g.name) + "," + g.placeDistance.ToString("F0", ic) + "," + g.lodDistance.ToString("F0", ic) + "," + g.removeDistance.ToString("F0", ic) + "]");
            var ips = itemPlaceRemoveScript.s;
            return "{\"itemRemoveDist\":" + (ips != null ? ips.itemRemoveDist.ToString("F0", ic) : "0") + ",\"gens\":[" + string.Join(",", rows) + "]}";
        }
    }
}
