using System.Collections.Generic;
using HarmonyLib;

namespace TLDRevamp.Fixes
{
    /// The game's item streaming (itemPlaceRemoveScript.Upd, once a second) runs RemoveStuff and then PlaceStuff in the
    /// same call. RemoveStuff stores an item (tosaveitemscript.Remove: its far-store record) and Destroys it — but a
    /// Destroy takes effect at the end of the frame, so the object is still registered in savedatascript.items when
    /// PlaceStuff runs. If the fresh record lies within itemSpawnDist of a generation centre, PlaceStuff finds "a stored
    /// record AND a live object with that id" and deletes the record as a stale duplicate (the !mapSpawned branch): the
    /// object goes at the end of the frame, its record went with it — the item is lost for good, in single player too.
    /// A whole car goes this way when any item under it is past itemRemoveDist (RemoveStuff then stores the whole
    /// hierarchy): the car vanished from the road (2026-10-03 reports, bug 10: "the game would delete my car at times").
    ///
    /// Second half of the same race: tosaveitemscript.OnDestroy removes savedatascript.items[id] whoever holds that id
    /// now. An object destroyed after its record was placed again (a new object with the same id) unregisters the NEW
    /// object: never saved, streamed or synced again — gone on the next load.
    ///
    /// Fix, exact otherwise: a record PlaceStuff dropped while its object was on its way out (preDestroyed) is put back;
    /// a dying object leaves the id to the object that holds it now.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.StreamRace.Enabled
    [HarmonyPatch]
    public static class StreamRace
    {
        public static bool Enabled = true;
        public static long RecordsKept, IdsKept;
        private static readonly List<KeyValuePair<uint, save_item>> _dying = new List<KeyValuePair<uint, save_item>>();

        [HarmonyPatch(typeof(itemPlaceRemoveScript), nameof(itemPlaceRemoveScript.PlaceStuff))]
        [HarmonyPrefix]
        private static void BeforePlace()
        {
            _dying.Clear();
            var s = savedatascript.s;
            if (!Enabled || s == null || s.data == null || s.data.itemData == null) return;
            var list = s.data.itemData.items;
            foreach (var kv in s.items)
            {
                var it = kv.Value;
                if (it == null || !it.preDestroyed || it.mapSpawned) continue;
                if (savedatascript.IndexOfID(list, kv.Key, out int k)) _dying.Add(new KeyValuePair<uint, save_item>(kv.Key, list[k]));
            }
        }

        [HarmonyPatch(typeof(itemPlaceRemoveScript), nameof(itemPlaceRemoveScript.PlaceStuff))]
        [HarmonyPostfix]
        private static void AfterPlace()
        {
            if (_dying.Count == 0) return;
            var list = savedatascript.s.data.itemData.items;
            foreach (var kv in _dying)
                if (!savedatascript.IndexOfID(list, kv.Key, out _)) { list.Add(kv.Value); RecordsKept++; }   // its chunk entry was never removed
            _dying.Clear();
        }

        /// Test (bridge `streamrace arm` / `streamrace check <id>`): the race on demand, in the game's own streaming pass.
        /// arm: the car nearest the player gets one of its parts moved 400 m away (past itemRemoveDist) and the game's
        /// Upd runs at once — RemoveStuff stores and destroys the whole car (the part is far), PlaceStuff finds the car's
        /// fresh record near the player while the car still exists. check: a frame later, is the car's record there, and
        /// does the game place the car again?
        public static string Probe(string arg)
        {
            var parts = (arg ?? "").Split(' ');
            var s = savedatascript.s; var ips = itemPlaceRemoveScript.s; var pl = mainscript.s != null ? mainscript.s.player : null;
            if (s == null || ips == null || pl == null) return "{\"error\":\"no world\"}";
            if (parts[0] == "arm")
            {
                tosaveitemscript car = null; float best = 60f;
                if (parts.Length > 1) car = Net.Entities.RootOfNet(uint.Parse(parts[1]));   // a given shared car
                if (car == null)
                foreach (var it in s.items.Values)
                    if (it != null && it.car != null && it.transform.parent == null && !it.preDestroyed)
                    {
                        float d = (it.transform.position - pl.transform.position).magnitude;
                        if (d < best) { best = d; car = it; }
                    }
                if (car == null) return "{\"error\":\"no car within 60 m\"}";
                tosaveitemscript part = null; int members = 0;
                foreach (var c in car.GetComponentsInChildren<tosaveitemscript>())
                    if (c != car) { members++; if (part == null) part = c; }
                if (part == null) return "{\"error\":\"the car has no parts\"}";
                long kept0 = RecordsKept;
                part.transform.position += new UnityEngine.Vector3(400f, 0f, 0f);
                ips.Upd();
                bool rec = savedatascript.IndexOfID(s.data.itemData.items, car.idInSave, out _);
                return "{\"car\":" + Json.Str(car.name) + ",\"id\":" + car.idInSave + ",\"members\":" + members + ",\"part\":" + Json.Str(part.name) +
                       ",\"dist\":" + best.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + ",\"preDestroyed\":" + (car.preDestroyed ? "true" : "false") +
                       ",\"recordAfterUpd\":" + (rec ? "true" : "false") + ",\"recordsKept\":" + (RecordsKept - kept0) +
                       ",\"spawnDist\":" + ips.itemSpawnDist.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"removeDist\":" + ips.itemRemoveDist.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"enabled\":" + (Enabled ? "true" : "false") + "}";
            }
            if (parts[0] == "check" && parts.Length > 1 && uint.TryParse(parts[1], out uint id))
            {
                bool rec = savedatascript.IndexOfID(s.data.itemData.items, id, out _);
                bool live = s.items.TryGetValue(id, out var obj) && obj != null;
                return "{\"id\":" + id + ",\"record\":" + (rec ? "true" : "false") + ",\"live\":" + (live ? "true" : "false") +
                       (live ? ",\"name\":" + Json.Str(obj.name) + ",\"members\":" + (obj.GetComponentsInChildren<tosaveitemscript>().Length - 1) +
                               ",\"dist\":" + (obj.transform.position - pl.transform.position).magnitude.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "") +
                       ",\"recordsKept\":" + RecordsKept + ",\"idsKept\":" + IdsKept + "}";
            }
            return "{\"error\":\"streamrace arm | check <id>\"}";
        }

        /// The holder of the id when the object dies; restored after the game's OnDestroy took it off the list.
        [HarmonyPatch(typeof(tosaveitemscript), "OnDestroy")]
        [HarmonyPrefix]
        private static void BeforeDestroy(tosaveitemscript __instance, out tosaveitemscript __state)
        {
            __state = null;
            if (!Enabled || savedatascript.s == null) return;
            if (savedatascript.s.items.TryGetValue(__instance.idInSave, out var now) && now != null && now != __instance) __state = now;
        }

        [HarmonyPatch(typeof(tosaveitemscript), "OnDestroy")]
        [HarmonyPostfix, HarmonyPriority(Priority.First)]   // before Entities' deletion check, which reads the holder
        private static void AfterDestroy(tosaveitemscript __instance, tosaveitemscript __state)
        {
            if (__state == null) return;
            if (!savedatascript.s.items.TryGetValue(__instance.idInSave, out var now) || now != __state) { savedatascript.s.items[__instance.idInSave] = __state; IdsKept++; }
        }
    }
}
