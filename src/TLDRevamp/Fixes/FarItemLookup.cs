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
    /// Selected placements wait in an id queue, with one shared budget for PlaceStuff and Runner.Update in a frame.
    /// Multiplayer uses the same store and rebind path; network messages keep their original ordering.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.FarItemLookup.Enabled / .Verify (also runs the original
    /// selection and compares the two lists, element by element) / .Spread / .SpreadMultiplayer / .BudgetMs
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
        public static bool SpreadMultiplayer = false;   // opt-in until tested in game. A/B: before this, RevaMP placed the entire selection at once
        public static long MultiplayerPasses, OutOfRange, DyingSkipped;
        private static readonly FrameWorkBudget Work = new FrameWorkBudget();
        public static double BudgetMs = 3.0;
        public static long Deferred, PlacedLater, SkippedLater, MaxQueue, SaveFlushes;
        public static double MaxTickMs;

        private static readonly Queue<uint> Pending = new Queue<uint>();
        private static readonly HashSet<uint> PendingSet = new HashSet<uint>();

        /// Selected by the streaming and waiting for its turn (Spread): it will be placed within frames.
        public static bool Claimed(uint id) => PendingSet.Count > 0 && PendingSet.Contains(id);
        private static itemPlaceRemoveScript _owner;
        private static savedatascript _save;

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
            if (CanSpread())
            {
                if (_owner != s || _save != save) { Pending.Clear(); PendingSet.Clear(); _owner = s; _save = save; }
                if (Net.Mp.Server != null || Net.Mp.Client != null) MultiplayerPasses++;
                // the game's placing loop over the selection in its order; past the budget the rest waits for the next frames.
                // Every selected id is claimed first (PendingSet: selected, not placed yet), so a car placed early in the
                // loop leaves its selected parts to their own turn (PlaceWithParts asks Claimed) instead of placing them
                // all inside its own PlaceOne. An id already queued by an earlier pass keeps its place in the queue.
                for (int l = 0; l < s.addFromFar.Count; l++)
                    if (PendingSet.Add(s.addFromFar[l])) { Pending.Enqueue(s.addFromFar[l]); Deferred++; }
                Drain(s);
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

        private static bool CanSpread() =>
            Spread && ((SpreadMultiplayer && (Net.Mp.Server != null || Net.Mp.Client != null)) || SinglePlayer());

        private static bool SinglePlayer() =>
            Net.Mp.Server == null && Net.Mp.Client == null && !syncScript.IsConnected() && !syncScript.IsHost();

        /// Every frame from the mod's runner: the deferred placements within the budget (at least one).
        public static void Tick()
        {
            if (Pending.Count == 0) return;
            var s = itemPlaceRemoveScript.s;
            if (s == null || s != _owner || savedatascript.s != _save || savedatascript.s == null || savedatascript.s.data == null || menuhandler.s == null || menuhandler.s.currentMainMap == null)
            { Pending.Clear(); PendingSet.Clear(); return; }
            if (!CanSpread()) { Flush(); return; }
            Sw.Restart();
            Drain(s);
            double ms = Sw.Elapsed.TotalMilliseconds;
            if (ms > MaxTickMs) MaxTickMs = ms;
        }

        private static void Drain(itemPlaceRemoveScript s)
        {
            Work.Begin(Time.frameCount, BudgetMs);
            while (Pending.Count > 0 && Work.CanRun)
            {
                long start = Stopwatch.GetTimestamp();
                Next(s);
                Work.Charge(Stopwatch.GetTimestamp() - start);
            }
        }

        /// All deferred placements now (before a save, when spreading stops).
        public static void Flush()
        {
            var s = itemPlaceRemoveScript.s;
            if (Pending.Count == 0) return;
            if (s == null || s != _owner || savedatascript.s != _save || savedatascript.s == null || savedatascript.s.data == null || menuhandler.s == null || menuhandler.s.currentMainMap == null) { Pending.Clear(); PendingSet.Clear(); return; }
            while (Pending.Count > 0) Next(s);
        }

        private static void Next(itemPlaceRemoveScript s)
        {
            uint cid = Pending.Dequeue();
            PendingSet.Remove(cid);
            try
            {
                var save = savedatascript.s;
                if (save.items.TryGetValue(cid, out var live) && live != null && live.preDestroyed) { DyingSkipped++; return; }
                if (savedatascript.IndexOfID(save.data.itemData.items, cid, out int k))
                {
                    var centers = menuhandler.s.currentMainMap.genArounds;
                    bool near = false;
                    foreach (var center in centers)
                        if ((mainscript.GlobalFromUnityPos(center.upos) - save.data.itemData.items[k].transform.pos).magnitude < s.itemSpawnDist) { near = true; break; }
                    if (!near) { OutOfRange++; return; }
                }
                if (PlaceOneAsTheGame(s, save, cid)) PlacedLater++; else SkippedLater++;
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
            ",\"spreadMultiplayer\":" + (SpreadMultiplayer ? "true" : "false") + ",\"multiplayerPasses\":" + MultiplayerPasses +
            ",\"outOfRange\":" + OutOfRange + ",\"dyingSkipped\":" + DyingSkipped + ",\"pending\":" + Pending.Count + ",\"deferred\":" + Deferred + ",\"placedLater\":" + PlacedLater + ",\"skippedLater\":" + SkippedLater +
            ",\"maxQueue\":" + MaxQueue + ",\"saveFlushes\":" + SaveFlushes + ",\"maxTickMs\":" + MaxTickMs.ToString("F3") + "}";
    }
}
