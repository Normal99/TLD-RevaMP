using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The game's item streaming (itemPlaceRemoveScript.PlaceStuff, once a second) goes through every stored item id in
    /// the 3×3 item chunks around each player (a chunk is 25 km: in practice every item the world ever stored) and finds
    /// each one's record in the far store with savedatascript.IndexOfID, a linear search of the whole store list. That
    /// is quadratic in the number of stored items: every item left more than 300 m behind is stored, so the cost grows
    /// the longer you drive, once a second, in one frame.
    ///
    /// Exact copy of PlaceStuff where only the lookup in the selection loop changes: the store's id → index map is built
    /// once per call (later entries win, as IndexOfID searches from the end), the store is not changed during that
    /// loop. The placing loop below is the game's own (it changes the store, so it keeps IndexOfID).
    ///
    /// Spread (single player only): coming back to a place you left (stored once you were 300 m away), PlaceStuff
    /// placed all of its items in one frame: 58 ms for the 172 items of the start area (drive test, an 83 ms frame).
    /// With Spread the selected items are placed in the same order and the same way, but within BudgetMs per frame (at
    /// least one per frame); the rest the next frames. Each one is checked again when its turn comes, as the game's
    /// placing loop does: a record already placed by other means (a car's parts with the car) is skipped. Items appear
    /// at 250 m, a few frames later. Not in multiplayer (the host's and the clients' streaming expect the original).
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.FarItemLookup.Enabled / .Verify (also runs the original
    /// selection and compares the two lists, element by element) / .Spread / .BudgetMs
    [HarmonyPatch]
    public static class FarItemLookup
    {
        /// On by default: exact (Verify: 19 passes over 175 stored items, 0 differences); O(N) per pass instead of O(N²).
        public static bool Enabled = true;
        public static bool Verify = false;
        public static long Calls, VerifyChecks, VerifyMismatches;
        public static double LastMs, MaxMs;
        public static int LastStored, LastSelected;
        /// On by default: returnbench (start area stored and placed again, 169 items, 2 rounds each): worst frame 79.3 → 18.7 ms.
        public static bool Spread = true;
        public static double BudgetMs = 3.0;
        public static long Deferred, PlacedLater, SkippedLater, MaxQueue, SaveFlushes;
        public static double MaxTickMs;

        private static readonly Queue<uint> Pending = new Queue<uint>();
        private static readonly HashSet<uint> PendingSet = new HashSet<uint>();
        private static readonly List<uint> Fresh = new List<uint>();

        /// Selected by the streaming and waiting for its turn (single player, Spread): it will be placed within frames.
        public static bool Claimed(uint id) => PendingSet.Count > 0 && PendingSet.Contains(id);
        private static itemPlaceRemoveScript _owner;

        private static readonly Dictionary<uint, int> Index = new Dictionary<uint, int>();
        private static readonly List<uint> Check = new List<uint>();
        private static readonly Stopwatch Sw = new Stopwatch();

        // after every other prefix (StreamRace's notes the dying records before PlaceStuff runs)
        [HarmonyPatch(typeof(itemPlaceRemoveScript), nameof(itemPlaceRemoveScript.PlaceStuff))]
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        private static bool Prefix(itemPlaceRemoveScript __instance)
        {
            if (!Enabled && !Spread) return true;
            Sw.Restart();
            Calls++;
            var s = __instance;
            var save = savedatascript.s;
            var genArounds = menuhandler.s.currentMainMap.genArounds;
            var items = save.data.itemData.items;
            s.addFromFar.Clear();
            s.placeChunks.Clear();
            for (int i = 0; i < genArounds.Count; i++)
            {
                Vector2d gchunk = savedatascript.SnapChunkPos(genArounds[i].upos);
                for (int j = -1; j <= 1; j++)
                {
                    for (int k = -1; k <= 1; k++)
                    {
                        Vector2d cchunk = default;
                        cchunk.x = gchunk.x + savedatascript.itemChunkSize * (double)k;
                        cchunk.y = gchunk.y + savedatascript.itemChunkSize * (double)j;
                        s.placeChunks.Add(cchunk);
                    }
                }
            }
            Index.Clear();
            for (int n = 0; n < items.Count; n++) Index[items[n].id] = n;
            Select(s, save, genArounds, items, s.addFromFar, Enabled);
            if (Verify) Compare(s, save, genArounds, items);
            LastStored = items.Count;
            LastSelected = s.addFromFar.Count;
            if (Spread && SinglePlayer())
            {
                if (_owner != s) { Pending.Clear(); PendingSet.Clear(); _owner = s; }
                // the game's placing loop over the selection in its order; past the budget the rest waits for the next frames.
                // Every selected id is claimed first (PendingSet: selected, not placed yet), so a car placed early in the
                // loop leaves its selected parts to their own turn (PlaceWithParts asks Claimed) instead of placing them
                // all inside its own PlaceOne. An id already queued by an earlier pass keeps its place in the queue.
                Fresh.Clear();
                for (int l = 0; l < s.addFromFar.Count; l++)
                    if (PendingSet.Add(s.addFromFar[l])) Fresh.Add(s.addFromFar[l]);
                for (int l = 0; l < Fresh.Count; l++)
                {
                    uint cid = Fresh[l];
                    if (Pending.Count > 0 || Sw.Elapsed.TotalMilliseconds >= BudgetMs) { Pending.Enqueue(cid); Deferred++; continue; }
                    PendingSet.Remove(cid);
                    PlaceOneAsTheGame(s, save, cid);
                }
                if (Pending.Count > MaxQueue) MaxQueue = Pending.Count;
            }
            else
            {
                Flush();
                // the game's placing loop, unchanged
                for (int l = 0; l < s.addFromFar.Count; l++) PlaceOneAsTheGame(s, save, s.addFromFar[l]);
            }
            LastMs = Sw.Elapsed.TotalMilliseconds;
            if (LastMs > MaxMs) MaxMs = LastMs;
            return false;
        }

        /// The body of the game's placing loop for one selected id.
        private static bool PlaceOneAsTheGame(itemPlaceRemoveScript s, savedatascript save, uint cid)
        {
            if (!savedatascript.IndexOfID(save.data.itemData.items, cid, out var index2)) return false;
            if (save.items.TryGetValue(cid, out var value) && !value.mapSpawned)
                save.data.itemData.items.RemoveAt(index2);
            else
                s.PlaceOne(cid, index2);
            return true;
        }

        private static bool SinglePlayer() =>
            Net.Mp.Server == null && Net.Mp.Client == null && !syncScript.IsConnected() && !syncScript.IsHost();

        /// Every frame from the mod's runner: the deferred placements within the budget (at least one).
        public static void Tick()
        {
            if (Pending.Count == 0) return;
            var s = itemPlaceRemoveScript.s;
            if (s == null || s != _owner || savedatascript.s == null || savedatascript.s.data == null || menuhandler.s.currentMainMap == null)
            { Pending.Clear(); PendingSet.Clear(); return; }
            if (!Spread || !SinglePlayer()) { Flush(); return; }
            Sw.Restart();
            do Next(s); while (Pending.Count > 0 && Sw.Elapsed.TotalMilliseconds < BudgetMs);
            double ms = Sw.Elapsed.TotalMilliseconds;
            if (ms > MaxTickMs) MaxTickMs = ms;
        }

        /// All deferred placements now (before a save, when spreading stops).
        public static void Flush()
        {
            var s = itemPlaceRemoveScript.s;
            if (Pending.Count == 0) return;
            if (s == null || s != _owner || savedatascript.s == null || savedatascript.s.data == null) { Pending.Clear(); PendingSet.Clear(); return; }
            while (Pending.Count > 0) Next(s);
        }

        private static void Next(itemPlaceRemoveScript s)
        {
            uint cid = Pending.Dequeue();
            PendingSet.Remove(cid);
            try
            {
                if (PlaceOneAsTheGame(s, savedatascript.s, cid)) PlacedLater++; else SkippedLater++;
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("FarItemLookup: placing " + cid + ": " + e.Message); }
        }

        [HarmonyPatch(typeof(savedatascript), nameof(savedatascript.SaveToData)), HarmonyPrefix]
        private static void BeforeSave() { if (Pending.Count > 0) SaveFlushes++; Flush(); }

        private static void Select(itemPlaceRemoveScript s, savedatascript save, List<mapSettingScript.genAround> genArounds,
                                   List<save_item> items, List<uint> into, bool fast)
        {
            foreach (Vector2d placeChunk in s.placeChunks)
            {
                if (!save.itemChunks.ContainsKey(placeChunk)) continue;
                foreach (uint item in save.itemChunks[placeChunk])
                {
                    int index;
                    if (fast) { if (!Index.TryGetValue(item, out index)) continue; }
                    else if (!savedatascript.IndexOfID(items, item, out index)) continue;
                    foreach (mapSettingScript.genAround genAround in genArounds)
                    {
                        if ((mainscript.GlobalFromUnityPos(genAround.upos) - items[index].transform.pos).magnitude < (double)s.itemSpawnDist)
                        {
                            into.Add(item);
                            break;
                        }
                    }
                }
            }
        }

        private static void Compare(itemPlaceRemoveScript s, savedatascript save, List<mapSettingScript.genAround> genArounds, List<save_item> items)
        {
            Check.Clear();
            Select(s, save, genArounds, items, Check, false);
            VerifyChecks++;
            bool same = Check.Count == s.addFromFar.Count;
            for (int i = 0; same && i < Check.Count; i++) same = Check[i] == s.addFromFar[i];
            if (same) return;
            if (++VerifyMismatches <= 5) Plugin.Log.LogWarning($"FarItemLookup: selected {s.addFromFar.Count} vs original {Check.Count}");
        }

        /// Test: every live car root (with a carscript) and how many saved items hang under it, sorted by id.
        public static string CarParts()
        {
            var rows = new List<string>();
            foreach (var kv in savedatascript.s.items)
            {
                var it = kv.Value;
                if (it == null || it.transform.parent != null || it.GetComponent<carscript>() == null) continue;
                rows.Add(kv.Key + ":" + (it.GetComponentsInChildren<tosaveitemscript>(true).Length - 1));
            }
            rows.Sort(string.CompareOrdinal);
            return "\"" + string.Join(" ", rows) + "\"";
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"calls\":" + Calls + ",\"lastMs\":" + LastMs.ToString("F3") +
            ",\"maxMs\":" + MaxMs.ToString("F3") + ",\"stored\":" + LastStored + ",\"selected\":" + LastSelected +
            ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches + ",\"spread\":" + (Spread ? "true" : "false") +
            ",\"pending\":" + Pending.Count + ",\"deferred\":" + Deferred + ",\"placedLater\":" + PlacedLater + ",\"skippedLater\":" + SkippedLater +
            ",\"maxQueue\":" + MaxQueue + ",\"saveFlushes\":" + SaveFlushes + ",\"maxTickMs\":" + MaxTickMs.ToString("F3") + "}";
    }
}
