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
    /// The game's own PlaceStuff with its two IndexOfID calls swapped at runtime (GamePatch). In the selection loop the
    /// lookup uses the store's id → index map, built once per call before the game's code runs (later entries win, as
    /// IndexOfID searches from the end); the store is not changed during that loop. The placing loop keeps the game's
    /// IndexOfID (it changes the store), or with Spread hands the id to the queue below instead.
    ///
    /// Selected placements wait in an id queue, with one shared budget for PlaceStuff and Runner.Update in a frame.
    /// Multiplayer uses the same store and rebind path; network messages keep their original ordering.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.FarItemLookup.Enabled / .Verify (every lookup in the selection
    /// loop also runs the game's IndexOfID and compares found and index) / .Spread / .SpreadMultiplayer / .BudgetMs
    [HarmonyPatch]
    public static class FarItemLookup
    {
        /// On by default: exact (Verify: 19 passes over 175 stored items, 0 differences); O(N) per pass instead of O(N²).
        public static bool Enabled = true;
        public static bool Verify = false;
        public static long Calls, VerifyChecks, VerifyMismatches;
        public static int Patched;
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
        private static readonly Stopwatch Sw = new Stopwatch();
        private static bool _indexReady, _spreading, _active;

        private delegate bool Lookup(List<save_item> items, uint id, out int index);

        [HarmonyPatch(typeof(itemPlaceRemoveScript), nameof(itemPlaceRemoveScript.PlaceStuff)), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var p = new GamePatch(nameof(FarItemLookup), instructions);
            p.CallsEach(new Lookup(savedatascript.IndexOfID<save_item>).Method,   // the overload the game's call binds to (there are two)
                AccessTools.Method(typeof(FarItemLookup), nameof(SelectIndex)), AccessTools.Method(typeof(FarItemLookup), nameof(PlaceIndex)));
            return p.Result(ref Patched);
        }

        // after every other prefix (StreamRace's notes the dying records before PlaceStuff runs)
        [HarmonyPatch(typeof(itemPlaceRemoveScript), nameof(itemPlaceRemoveScript.PlaceStuff))]
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        private static void Prefix(itemPlaceRemoveScript __instance)
        {
            _active = _indexReady = _spreading = false;
            if (Patched == 0 || (!Enabled && !Spread)) return;
            _active = true;
            Sw.Restart();
            Calls++;
            var s = __instance;
            var save = savedatascript.s;
            _spreading = CanSpread();
            if (_spreading)
            {
                if (_owner != s || _save != save) { Pending.Clear(); PendingSet.Clear(); _owner = s; _save = save; }
                if (Net.Mp.Server != null || Net.Mp.Client != null) MultiplayerPasses++;
            }
            else Flush();
            var items = save.data.itemData.items;
            LastStored = items.Count;
            if (Enabled)
            {
                Index.Clear();
                for (int n = 0; n < items.Count; n++) Index[items[n].id] = n;
                _indexReady = true;
            }
        }

        [HarmonyPatch(typeof(itemPlaceRemoveScript), nameof(itemPlaceRemoveScript.PlaceStuff)), HarmonyPostfix]
        private static void Postfix(itemPlaceRemoveScript __instance)
        {
            if (!_active) return;
            _active = _indexReady = false;
            LastSelected = __instance.addFromFar.Count;
            if (_spreading)
            {
                // the game's placing loop handed every selected id to the queue in its order; past the budget the rest waits
                Drain(__instance);
                if (Pending.Count > MaxQueue) MaxQueue = Pending.Count;
            }
            LastMs = Sw.Elapsed.TotalMilliseconds;
            if (LastMs > MaxMs) MaxMs = LastMs;
        }

        /// The selection loop's IndexOfID: the map built in the prefix.
        public static bool SelectIndex(List<save_item> items, uint id, out int index)
        {
            if (!_indexReady) return savedatascript.IndexOfID(items, id, out index);
            bool found = Index.TryGetValue(id, out index);
            if (Verify)
            {
                VerifyChecks++;
                bool theirs = savedatascript.IndexOfID(items, id, out int at);
                if (theirs != found || (found && at != index))
                    if (++VerifyMismatches <= 5) Plugin.Log.LogWarning($"FarItemLookup: id {id} found {found} at {index} vs the game's {theirs} at {at}");
            }
            return found;
        }

        /// The placing loop's IndexOfID. Spreading: the id is claimed and queued instead (every selected id is claimed
        /// first, so a car placed early leaves its selected parts to their own turn: PlaceWithParts asks Claimed; an id
        /// already queued by an earlier pass keeps its place), and the game's loop body is skipped for it.
        public static bool PlaceIndex(List<save_item> items, uint id, out int index)
        {
            if (!_spreading) return savedatascript.IndexOfID(items, id, out index);
            if (PendingSet.Add(id)) { Pending.Enqueue(id); Deferred++; }
            index = -1;
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
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"patched\":" + Patched + ",\"calls\":" + Calls + ",\"lastMs\":" + LastMs.ToString("F3") +
            ",\"maxMs\":" + MaxMs.ToString("F3") + ",\"stored\":" + LastStored + ",\"selected\":" + LastSelected +
            ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches + ",\"spread\":" + (Spread ? "true" : "false") +
            ",\"spreadMultiplayer\":" + (SpreadMultiplayer ? "true" : "false") + ",\"multiplayerPasses\":" + MultiplayerPasses +
            ",\"outOfRange\":" + OutOfRange + ",\"dyingSkipped\":" + DyingSkipped + ",\"pending\":" + Pending.Count + ",\"deferred\":" + Deferred + ",\"placedLater\":" + PlacedLater + ",\"skippedLater\":" + SkippedLater +
            ",\"maxQueue\":" + MaxQueue + ",\"saveFlushes\":" + SaveFlushes + ",\"maxTickMs\":" + MaxTickMs.ToString("F3") + "}";
    }
}
