using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Spawn leases (docs/MULTIPLAYER-ARCHITECTURE.md §4b): world contents exist once per session.
    /// Everything a building contains comes from poiScript.SpawnStuff — ItemSpawn (the game's random spawners, an unseeded
    /// UnityEngine.Random: different on every machine) and FixedSpawn (fixed sets such as the start house's cars). In a
    /// session every machine asks the server before spawning a building; only the lease holder runs the game's
    /// SpawnStuff, and everything it spawned is shared. Everyone else gets those objects from the server — nothing to
    /// hide, no duplicates. The official MP's version of this ("only the host spawns", clients skip SpawnStuff) can't
    /// work when players are far apart: the host never loads buildings 200 km away.
    public static partial class Entities
    {
        public const byte LeaseReq = 28, LeaseGrant = 29, LeaseTaken = 30, LeaseDone = 33, LeaseFreed = 34;
        /// Built-in building items disabled because the lease holder's copies are shared: per lease key, so a lease
        /// that ends without a spawn (its holder left mid-spawn) can give them back.
        private static readonly Dictionary<string, List<GameObject>> DisabledBuiltIn = new Dictionary<string, List<GameObject>>();
        public static long BuiltInShared, BuiltInDisabled, BuiltInReEnabled, LeasesDone, LeasesFreed;

        private static readonly HashSet<string> LeaseAsked = new HashSet<string>();      // this machine asked already
        private static readonly Dictionary<string, int> Leases = new Dictionary<string, int>(); // server: building → holder
        private static readonly List<string> PendingGrants = new List<string>();
        private static bool _leaseSpawning;
        /// One capture window per leased building being spawned here. Before v0.64.4 there was a single window: a second
        /// grant (two buildings 35 m apart, granted moments apart — the first play session with a friend) restarted it,
        /// so whatever the first building had spawned since its first capture pass was never shared (items only the
        /// client saw), and its LeaseDone was never sent (the server freed the lease when the client left).
        private sealed class Capture { public string Key; public Vector3 At; public float Until; }
        private static readonly List<Capture> Captures = new List<Capture>();
        private static HashSet<uint> _captureKnown;   // item ids seen while any capture runs
        public static long LeasesAsked, LeasesGranted, LeaseSpawns, LeaseItemsShared, HostItemsShared;

        private static string Key(poiGenScript.poiClass p) =>
            p.posID.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + p.posID.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        [HarmonyLib.HarmonyPatch(typeof(poiScript), nameof(poiScript.SpawnStuff), new[] { typeof(poiGenScript.poiClass) })]
        private static class LeaseGate
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(poiGenScript.poiClass _poiClass)
            {
                if (!InSession || _leaseSpawning || _poiClass == null) return true;
                if (_poiClass.spawnedItems && _poiClass.spawnedFixed) return true;   // nothing left to spawn anyway
                string k0 = Key(_poiClass);
                // freed a moment ago (a refusal frees it too: player not placed on the server yet, out of reach): not again
                // every frame — the flags stay down, the game asks again
                if (_leaseRetryAt.TryGetValue(k0, out float retryAt) && Time.realtimeSinceStartup < retryAt) return false;
                // the game must not spawn it here; the server decides who does
                _poiClass.spawnedItems = true;
                _poiClass.spawnedFixed = true;
                string k = Key(_poiClass);
                if (LeaseAsked.Add(k))
                {
                    // with the building type generated here: the server compares it with the holder's (a building that
                    // differs between machines — the missing diner)
                    W.Reset(); W.U8(LeaseReq); W.Str(k);
                    if (LeaseTypeCheck) { W.I32(_poiClass.id); W.Str(_poiClass.poiName ?? ""); }
                    ToServer(W, true);
                    LeasesAsked++;
                }
                return false;
            }
        }

        /// Server: the first to ask spawns it; everyone else gets the result as shared objects.
        /// Server: the building type each lease's holder generated there (id, name).
        private static readonly Dictionary<string, KeyValuePair<int, string>> LeaseTypes = new Dictionary<string, KeyValuePair<int, string>>();
        public static long LeaseTypeMismatches;
        public static bool LeaseTypeCheck = true;   // A/B: false = before v0.66.8 (no type in LeaseReq, nothing compared)

        private static void OnLeaseTaken(string key, int holderType = int.MinValue, string holderName = "")
        {
            var poi = FindPoi(key);
            if (holderType != int.MinValue && poi != null && poi.id != holderType)
            {
                LeaseTypeMismatches++;
                Anomaly($"building differs at {key}: here {poi.poiName} (type {poi.id}), the player who spawned its contents has {holderName} (type {holderType})");
            }
            var pobj = FindPoiObject(key);
            if (pobj == null) return;
            var shared = new HashSet<tosaveitemscript>();
            foreach (var e in ByNet.Values) foreach (var it in e.Items) if (it != null) shared.Add(it);
            var list = new List<GameObject>();
            foreach (var it in pobj.GetComponentsInChildren<tosaveitemscript>(true))
                if (!shared.Contains(it) && it.gameObject.activeSelf) { it.gameObject.SetActive(false); list.Add(it.gameObject); BuiltInDisabled++; }
            if (list.Count > 0) DisabledBuiltIn[key] = list;
        }

        private static readonly Dictionary<string, float> _leaseRetryAt = new Dictionary<string, float>();
        public static float LeaseRetryS = 2f;

        /// A lease ended without a spawn: its holder left while the building was still empty. Everything the holder
        /// would have shared is gone, so the building is spawnable again: hand the disabled built-ins back, forget that
        /// this machine asked, and clear the poi's spawn flags so the game calls SpawnStuff once someone is near.
        private static void OnLeaseFreed(string key)
        {
            Plugin.Log.LogInfo($"LeaseFreed '{key}': had asked {LeaseAsked.Contains(key)}, clearing");
            LeaseAsked.Remove(key);
            _leaseRetryAt[key] = Time.realtimeSinceStartup + LeaseRetryS;
            PendingGrants.RemoveAll(k => k == key);
            Captures.RemoveAll(c => c.Key == key);
            if (Captures.Count == 0) _captureKnown = null;
            if (DisabledBuiltIn.TryGetValue(key, out var list))
            {
                foreach (var go in list) if (go != null) { go.SetActive(true); BuiltInReEnabled++; }
                DisabledBuiltIn.Remove(key);
            }
            var poi = FindPoi(key);
            if (poi != null) { poi.spawnedItems = false; poi.spawnedFixed = false; }
        }

        private static poiScript FindPoiObject(string key)
        {
            var poi = FindPoi(key);
            return poi != null ? poi.pobj : null;
        }

        private static poiGenScript.poiClass FindPoi(string key)
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.poiGens == null) return null;
            foreach (var g in map.poiGens)
            {
                if (g == null) continue;
                foreach (var ch in g.chunks.Values)
                    foreach (var poi in ch.pois.Values)
                        if (Key(poi) == key) return poi;
            }
            return null;
        }

        /// Server state for tests: which lease is whose, which are done, the counters. Plus `leasearm`: a test hook
        /// that marks a key as asked-for on this machine (a client that asked once, to see LeaseFreed clear it).
        public static string LeaseTable()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var kv in Leases)
                rows.Add("{\"key\":" + Json.Str(kv.Key) + ",\"holder\":" + kv.Value + ",\"done\":" + (_leasesDone.Contains(kv.Key) ? "true" : "false") + "}");
            return "{\"leases\":[" + string.Join(",", rows) + "],\"asked\":" + LeasesAsked + ",\"granted\":" + LeasesGranted +
                   ",\"spawns\":" + LeaseSpawns + ",\"done\":" + LeasesDone + ",\"freed\":" + LeasesFreed + ",\"leaseRefusedWhy\":" + LeaseRefusalsJson() + "}";
        }

        public static void LeaseArm(string key) => LeaseAsked.Add(key);

        /// The lease table outlives the server: without it, a client arriving at a building that a PREVIOUS server
        /// session populated would be granted a fresh lease and spawn the contents again — the save would grow one
        /// duplicate set per visited building per restart. Saved next to the world save (`.tlds.leases`), loaded when
        /// hosting starts; every holder becomes 0 (after a restart the server itself is the keeper of everything).
        ///
        /// The table belongs to ONE world. Before v0.64.5 it was read from "the last save name" — in a NEW world that is
        /// still the previous world's save, so a new world inherited the old one's leases: buildings at the same keys
        /// (the start area's sit at the same place in every world) counted as spawned and stayed empty forever
        /// (leasepair --site 245 620: the start gas station had nothing, on both machines). Now: the world is the file
        /// it was loaded from (none for a new game), then the file it was last saved to; its lease lines travel with it,
        /// autosave rotation (1 → 2 → 3) included.
        private static int _worldScene;          // mainscript instance the state below is for (a new world = a new one)
        private static string _worldFile;        // the save file this world lives in (null: new world, never saved)
        private static List<string> _worldLines = new List<string>();   // its leases as last loaded / saved

        /// The save file the game read for the world being loaded. DataFromMenuScript.load can't tell (the map clears it
        /// on its first frame) and savefilename survives into later new games; so: every scene change goes through
        /// ResetStuff (new game, load, main menu, joining) → no file; the game reading a save → that file.
        private static string _readSave;

        [HarmonyLib.HarmonyPatch(typeof(DataFromMenuScript), nameof(DataFromMenuScript.ResetStuff))]
        private static class WorldReset
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix() { _readSave = null; _worldScene = 0; }
        }

        [HarmonyLib.HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.ReadSave))]
        private static class WorldFromReadSave
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(string _name) { _readSave = _name; _worldScene = 0; }
        }

        /// The save file this world was loaded from or last saved to (null: a new world, never saved). Other tables that
        /// belong to the world (PlayerSave's `.players`) live next to it.
        internal static string WorldSaveFile { get { EnsureWorld(); return _worldFile; } }

        private static void EnsureWorld()
        {
            int id = mainscript.s != null ? mainscript.s.GetInstanceID() : 0;
            if (id != 0 && id == _worldScene) return;
            _worldScene = id;
            _worldLines = new List<string>();
            _worldFile = _readSave;
            try
            {
                if (_worldFile != null && System.IO.File.Exists(_worldFile + ".leases"))
                    _worldLines.AddRange(System.IO.File.ReadAllLines(_worldFile + ".leases"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("lease load: " + e.Message); }
            Plugin.Log.LogInfo($"world: {(_worldFile == null ? "new" : System.IO.Path.GetFileName(_worldFile))}, {_worldLines.Count} saved leases");
        }

        /// After the game saved this world to `dataFile`: its lease table goes next to it (or a stale one is removed).
        internal static void SaveLeases(string dataFile)
        {
            try
            {
                EnsureWorld();
                if (string.IsNullOrEmpty(dataFile)) return;
                if (InSession || Leases.Count > 0)
                {
                    _worldLines = new List<string>();
                    foreach (var kv in Leases) if (_leasesDone.Contains(kv.Key)) _worldLines.Add(kv.Key + "\t0\t1");
                }
                var path = dataFile + ".leases";
                if (_worldLines.Count > 0) System.IO.File.WriteAllLines(path, _worldLines);
                else if (System.IO.File.Exists(path)) System.IO.File.Delete(path);   // another world's, from before
                _worldFile = dataFile;
            }
            catch (Exception e) { Plugin.Log.LogWarning("lease save: " + e.Message); }
        }

        /// `mp save` (the game's Save already ran its postfix; kept for explicit calls)
        internal static void SaveLeases()
        {
            var name = mainscript.GetLastSaveName();
            if (!string.IsNullOrEmpty(name)) SaveLeases(pathscript.SaveDataName(name));
        }

        internal static void LoadLeases()
        {
            EnsureWorld();
            int n = 0;
            foreach (var line in _worldLines)
            {
                var parts = line.Split('\t');
                if (parts.Length < 2 || !LeasePosition(parts[0], out _)) continue;
                if (Leases.ContainsKey(parts[0])) continue;   // the host's own pois are already marked
                Leases[parts[0]] = 0; n++;
                _leasesDone.Add(parts[0]);   // older hosts saved already populated buildings with done=0
            }
            Plugin.Log.LogInfo($"Leases loaded for this world ({(_worldFile == null ? "new" : System.IO.Path.GetFileName(_worldFile))}): {n}, table {Leases.Count}");
        }

        [HarmonyLib.HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.Save))]
        private static class SaveLeasesWithWorld
        {
            [HarmonyLib.HarmonyPostfix]   // covers AutoSave, `mp save` and menu saves alike
            private static void Postfix(string _text) => SaveLeases(pathscript.SaveDataName(pathscript.UnDuckStringBecauseItIsDuckedByDefault(_text)));
        }

        /// The game's autosave rotation (autosave1 → 2 → 3, before writing the new autosave1) moves the save files;
        /// their lease tables move the same way, under the same conditions. Only when the autosave really runs: a
        /// blocked one (TestSafety: tests, a client in the host's world) rotates nothing — the first version of this
        /// ran anyway and moved autosave1's table to autosave2 with the saves left in place (2026-10-02). Last in line
        /// so the blockers have decided; `__runOriginal` is their verdict.
        [HarmonyLib.HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.AutoSave))]
        private static class RotateLeasesWithAutosaves
        {
            [HarmonyLib.HarmonyPrefix, HarmonyLib.HarmonyPriority(HarmonyLib.Priority.Last)]
            private static void Prefix(bool __runOriginal)
            {
                if (!__runOriginal) return;
                RotateLeaseFiles();
            }

            internal static void RotateLeaseFiles()
            {
                try
                {
                    string d1 = pathscript.SaveDataName(pathscript.autosaveName1), d2 = pathscript.SaveDataName(pathscript.autosaveName2),
                           d3 = pathscript.SaveDataName(pathscript.autosaveName3);
                    if (!System.IO.File.Exists(d1)) return;
                    foreach (var x in SideFiles)
                    {
                        if (System.IO.File.Exists(d3) && System.IO.File.Exists(d2)) Del(d3 + x);
                        if (System.IO.File.Exists(d2)) { Del(d3 + x); Mv(d2 + x, d3 + x); }
                        Del(d2 + x); Mv(d1 + x, d2 + x);
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning("lease rotate: " + e.Message); }
            }
            // the lease table, and the players' records (PlayerSave) - each with the copies it writes on the way
            private static string[] SideFiles => PlayerSave.WithSaveFile
                ? new[] { ".leases", ".players", ".players.bak", ".players.tmp" } : new[] { ".leases" };
            private static void Del(string p) { if (System.IO.File.Exists(p)) System.IO.File.Delete(p); }
            private static void Mv(string a, string b) { if (System.IO.File.Exists(a)) System.IO.File.Move(a, b); }
        }

        internal static void RotateLeaseFiles() => RotateLeasesWithAutosaves.RotateLeaseFiles();

        public static void ResetCounters()
        {
            LeaseRefusedWhy.Clear();
            LeasesAsked = LeasesGranted = LeaseSpawns = LeaseItemsShared = HostItemsShared = BuiltInShared = BuiltInDisabled = BuiltInReEnabled = LeasesDone = LeasesFreed = 0;
            StatesSent = StatesIn = StatesStale = Spawned = OwnerChanges = 0;
            StatesSkippedPose = StatesSkippedLocked = StatesSkippedAttached = StatesSkippedRabbitAnimation = StatesSkippedRagdoll = 0;
            StatesSkippedIdentical = StatesSkippedCorpseRoot = StatesSkippedUnknown = StatesSkippedCreatureRange = StatesSkippedDeadAnimation = 0;
            PartsReported = PartsApplied = PartOffRejected = ClaimsRefused = ClaimsSent = 0; RefusedCrash = RefusedHeld = RefusedStored = RefusedDriven = 0; _refuseLogged.Clear();
            ResyncsSent = ResyncsApplied = ResyncItemsLoaded = ResyncSkipped = PartStatesChanged = ResyncBytes = ResyncItemsSent = ResyncHeld = 0;
            EditsSent = EditsApplied = EditsRejected = EditsMarked = 0;
            ResyncCaptureMsMax = ResyncCaptureMsTotal = 0; ResyncBudgetFrames = 0;
            LiveUsablesSent = LiveUsablesRelayed = LiveUsablesApplied = LiveUsablesStale = LiveUsablesRefused = 0;
        }

        private static void OnLeaseGrant(string key)
        {
            if (!TrySpawnLeased(key)) PendingGrants.Add(key);   // building object not there yet: retry in Tick
        }

        private static bool TrySpawnLeased(string key)
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.poiGens == null) return false;
            foreach (var g in map.poiGens)
            {
                if (g == null) continue;
                foreach (var ch in g.chunks.Values)
                    foreach (var poi in ch.pois.Values)
                    {
                        if (Key(poi) != key) continue;
                        if (poi.pobj == null) return false;
                        // capture what the game spawns: now, and for a few seconds (item spawning can be spread over
                        // frames — our own ItemSpawnSpread does that)
                        // items built into the building object exist on every machine the moment the building appears:
                        // the holder's go out, everyone else's are disabled (LeaseTaken)
                        var builtIn = new List<tosaveitemscript>(poi.pobj.GetComponentsInChildren<tosaveitemscript>(true));
                        BuiltInShared += ShareRoots(builtIn);
                        if (_captureKnown != null) CaptureTick();   // running captures take what their buildings spawned so far
                        if (_captureKnown == null) _captureKnown = new HashSet<uint>(savedatascript.s.items.Keys);   // (that may end the last)
                        Captures.Add(new Capture { Key = key, At = poi.pobj.transform.position, Until = Time.realtimeSinceStartup + 4f });
                        _leaseSpawning = true;
                        try
                        {
                            poi.spawnedItems = false;
                            poi.spawnedFixed = false;
                            poi.pobj.SpawnStuff(poi);
                        }
                        finally { _leaseSpawning = false; }
                        LeaseSpawns++;
                        CaptureTick();
                        return true;
                    }
            }
            return false;
        }

        /// Share everything new that appeared near a leased building being spawned here; a building's capture ends after
        /// its window (spawning can be spread over frames — our own ItemSpawnSpread does that) with LeaseDone.
        private static void CaptureTick()
        {
            if (_captureKnown == null) return;
            var fresh = new List<tosaveitemscript>();
            foreach (var kv in savedatascript.s.items)
            {
                if (!_captureKnown.Add(kv.Key) || kv.Value == null) continue;
                var pos = kv.Value.transform.position;
                foreach (var c in Captures)
                    if ((pos - c.At).sqrMagnitude < 400f * 400f) { fresh.Add(kv.Value); break; }
            }
            if (fresh.Count > 0) LeaseItemsShared += ShareRoots(fresh);
            float now = Time.realtimeSinceStartup;
            for (int i = Captures.Count - 1; i >= 0; i--)
            {
                if (now <= Captures[i].Until) continue;
                // everything this building spawns is shared: the lease is complete, the server may keep it even if we
                // leave (ServerOnLeaseDone)
                W.Reset(); W.U8(LeaseDone); W.Str(Captures[i].Key); ToServer(W, true);
                LeasesDone++;
                Captures.RemoveAt(i);
            }
            if (Captures.Count == 0) _captureKnown = null;
        }

        /// Share the group roots among `items` (cars with their parts, loose items); attached parts go with their car.
        private static int ShareRoots(List<tosaveitemscript> items)
        {
            var covered = new HashSet<tosaveitemscript>();
            foreach (var e in ByNet.Values) foreach (var it in e.Items) if (it != null) covered.Add(it);
            int n = 0;
            // cars first: their groups take their parts along
            items.Sort((a, b) => (b.car != null).CompareTo(a.car != null));
            foreach (var it in items)
            {
                if (it == null || covered.Contains(it) || !it.gameObject.activeInHierarchy) continue;
                if (AttachedToItem(it)) continue;                                                          // goes with that item
                if (it.transform.parent != null && it.transform.parent.GetComponentInParent<tosaveitemscript>() != null) continue; // inside another item
                var group = ItemSnapshot.Group(it);
                foreach (var g in group) covered.Add(g);
                ShareItem(it, group);
                n++;
            }
            return n;
        }

        /// Attached to another item (a car part, a thing in a slot): it travels in that item's group. Attached to a
        /// building or the world (a bulb in a lamp socket, an air freshener on a hook): it's shared on its own — its save
        /// record says which socket, and every machine has the same building.
        private static bool AttachedToItem(tosaveitemscript it)
        {
            var a = it.attachable;
            if (a == null || !a.attached || a.utanfuto != null) return false;
            var host = a.point != null ? a.point.GetComponentInParent<tosaveitemscript>() : null;
            if (host == null && it.transform.parent != null) host = it.transform.parent.GetComponentInParent<tosaveitemscript>();
            return host != null && host != it;
        }

        /// Hosting starts: buildings the host already spawned are done (it holds their lease), and its loaded loose
        /// items and cars become shared objects — and so does everything in its far store (Entities.FarStore).
        public static void HostStarted()
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map != null && map.poiGens != null)
                foreach (var g in map.poiGens)
                {
                    if (g == null) continue;
                    foreach (var ch in g.chunks.Values)
                        foreach (var poi in ch.pois.Values)
                            if (poi.spawnedItems || poi.spawnedFixed) { Leases[Key(poi)] = 0; _leasesDone.Add(Key(poi)); }
                }
            LoadLeases();   // leases from previous server sessions: distant buildings stay taken, no re-spawn duplicates
            var all = new List<tosaveitemscript>();
            foreach (var it in savedatascript.s.items.Values) if (it != null) all.Add(it);
            HostItemsShared += ShareRoots(all);
            ShareFarStore();   // and everything stored away from the host: it reaches players by distance
        }

        /// The client is about to load the host's world: everything from the old scene goes away with it.
        public static void ClientWorldReload()
        {
            LiveUsablesReset(keepPoiSequences: true);
            ByNet.Clear(); PendingShare.Clear(); ProxyItems.Clear();
            ProxyWheelOwner.Clear(); ProxyWheelIndex.Clear(); ProxyEngineOwner.Clear();
            LeaseAsked.Clear(); PendingGrants.Clear(); _captureKnown = null; Captures.Clear();
            Mp.SceneTearingDown = false;
            DisabledBuiltIn.Clear();
        }

        /// Per frame: grants waiting for their building object, capture windows.
        private static void LeaseTick()
        {
            if (IsHost) ServerLeaseExpiry();
            for (int i = PendingGrants.Count - 1; i >= 0; i--)
                if (TrySpawnLeased(PendingGrants[i])) PendingGrants.RemoveAt(i);
            if (_captureKnown != null) CaptureTick();
        }

        // ------------------------------------------------------------------ picking up someone else's item

        /// Picking up an item another machine owns: take it at once (provisional ownership, a server round trip at 150–
        /// 200 ms would be felt) and tell the server; the rare same-instant conflict is settled there.
        [HarmonyLib.HarmonyPatch(typeof(pickupable), nameof(pickupable.Pickup))]
        private static class PickupClaim
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(pickupable __instance)
            {
                if (!InSession || !IsProxy(__instance)) return;
                var limbOf = RagdollOf(__instance);   // a limb of a dead creature: not under the item any more
                // the item's own entity first: one under another shared object (stuck on a car or its door, riding in
                // it) matched that object's root as well, and whichever came first in ByNet won — the car (no claim:
                // the grabbed copy snapped back to its owner's pose) or nothing
                Ent hit = null;
                foreach (var e in ByNet.Values)
                    if (e.Proxy && e.Root != null && (e == limbOf || e.Root.gameObject == __instance.gameObject)) { hit = e; break; }
                if (hit == null)
                    foreach (var e in ByNet.Values)
                        if (e.Proxy && e.Root != null && e.Root.transform == __instance.transform.root) { hit = e; break; }
                if (hit == null && DrawbarPickupClaim)
                    foreach (var e in ByNet.Values)
                        if (e.Proxy && HitchTrailer(e) && e.Root.attachable.gameObject == __instance.gameObject) { hit = e; break; }
                if (hit == null) return;
                // a whole car: lifted (a bike, a moped, a car flipped back) it's ours like any item, as in single player —
                // unless someone rides it (CanPickup refuses that lift; the server refuses the claim of a driven car too).
                // Without CarCopyLift cars changed hands by driving only (a passenger entering claimed the car)
                if (hit.Root.car != null && (!CarCopyLift || Ridden(hit))) return;
                if (HitchTrailer(hit) && hit.Root.attachable.attached) return;
                // picked up out of its owner's physics lock: the game's Pick unlocks it right after this prefix, so the
                // takeover doesn't put it back in the owner's mount spot first. Put back, an item already brought to the
                // hand lay 1.9 m off it and the game dropped it at once (stress campaign 2026-10-25, heldincar C: the
                // lamp let go in a friend's moving car, picked up again after getting out)
                if (PickupSkipsLock && hit.PoseLocked) { hit.PoseLocked = false; PickupLocksSkipped++; }
                SetProxy(hit, false);
                hit.OwnerId = MyId;
                W.Reset(); WriteClaim(W, hit.NetId, ClaimReason.Pickup); ToServer(W, true);
                ClaimsSent++;
            }
        }

        /// A/B switch: true = picking up the drawbar of another player's trailer claims the trailer. The drawbar is its own
        /// root object (attachgripscript instantiates it unparented), so neither match above found the trailer: the copy
        /// stayed a proxy, HitchTick made the drawbar kinematic again every step, it never followed the hand and the game
        /// dropped it at once, before the attach press (user, 2026-10-24: the IFA trailer "sometimes won't couple": it
        /// coupled only when its owner pressed attach; hitchplayer.py J: the joiner's hands empty 1.5 s after picking it up).
        public static bool DrawbarPickupClaim = true;
        /// A/B switch: true = a pickup claim of a copy in its owner's physics lock skips the takeover's lock re-apply
        /// (false: SetProxy put it back in the owner's spot in the mount before the game's Pick unlocked it).
        public static bool PickupSkipsLock = true; public static long PickupLocksSkipped;

        /// A/B switch: true = lifting another player's car copy claims it unless someone rides it, the ridden one can't be
        /// lifted (CarCopyNoLift); false = the game's lift ran on the copy unclaimed (before v0.69.2).
        public static bool CarCopyLift = true;
        public static int CarLiftsRefused;   // refused CanPickup checks (the ray asks every frame it looks)

        /// Someone sits on car `e`: its owner at the wheel, or any other player in any of its seats.
        private static bool Ridden(Ent e)
        {
            if (e.Driven) return true;
            foreach (var (id, _, _) in RemotePlayers.Each()) if (RemotePlayers.SeatNetOf(id) == e.NetId) return true;
            return false;
        }

        /// A car copy someone rides can't be lifted. The game's pickupable.Pickup made the body dynamic and its Drop leaves
        /// it so; unclaimed, the copy stayed a proxy BodyMotionTick skips ("not kinematic") — the playtest bike fell over and
        /// lay there while its rider rode on, its shown time frozen (bikegrab.py: 6.3 m behind after the owner moved it 6 m,
        /// for good). CanPickup is the check the game's pickup ray makes, so the prompt doesn't show either.
        [HarmonyLib.HarmonyPatch(typeof(pickupable), nameof(pickupable.CanPickup))]
        private static class CarCopyNoLift
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(pickupable __instance, ref bool __result)
            {
                if (!__result || !CarCopyLift || !InSession || !IsProxy(__instance)) return;
                var e = ProxyOf(__instance.transform);
                if (e == null || e.Root == null || e.Root.car == null || e.Root.gameObject != __instance.gameObject || !Ridden(e)) return;
                __result = false;
                if (CarLiftsRefused++ == 0) Plugin.Log.LogInfo($"ridden car copy lift refused: {e.Root.name} net {e.NetId} (owner {e.OwnerId})");
            }
        }
    }
}
