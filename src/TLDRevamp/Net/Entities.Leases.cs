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
                // the game must not spawn it here; the server decides who does
                _poiClass.spawnedItems = true;
                _poiClass.spawnedFixed = true;
                string k = Key(_poiClass);
                if (LeaseAsked.Add(k))
                {
                    W.Reset(); W.U8(LeaseReq); W.Str(k); ToServer(W, true);
                    LeasesAsked++;
                }
                return false;
            }
        }

        /// Server: the first to ask spawns it; everyone else gets the result as shared objects.
        private static void ServerLease(int from, string key)
        {
            if (Leases.TryGetValue(key, out int holder))
            {
                // someone else spawned it: the asker drops its own built-in copies and takes theirs
                if (holder != from)
                {
                    Plugin.Log.LogInfo($"lease {key}: taken by {from} (holder {holder}{(_leasesDone.Contains(key) ? ", done" : "")})");
                    W.Reset(); W.U8(LeaseTaken); W.Str(key); ServerSendTo(from, W, true);
                }
                return;
            }
            Leases[key] = from;
            Plugin.Log.LogInfo($"lease {key}: granted to {from}");
            W.Reset(); W.U8(LeaseGrant); W.Str(key);
            ServerSendTo(from, W, true);
            LeasesGranted++;
        }

        /// Server: this lease's holder finished spawning and sharing its building.
        private static void ServerLeaseDone(int from, string key)
        {
            if (Leases.TryGetValue(key, out int holder) && holder == from) { _leasesDone.Add(key); Plugin.Log.LogInfo($"lease {key}: done ({from})"); }
        }

        private static void OnLeaseTaken(string key)
        {
            var pobj = FindPoiObject(key);
            if (pobj == null) return;
            var shared = new HashSet<tosaveitemscript>();
            foreach (var e in ByNet.Values) foreach (var it in e.Items) if (it != null) shared.Add(it);
            var list = new List<GameObject>();
            foreach (var it in pobj.GetComponentsInChildren<tosaveitemscript>(true))
                if (!shared.Contains(it) && it.gameObject.activeSelf) { it.gameObject.SetActive(false); list.Add(it.gameObject); BuiltInDisabled++; }
            if (list.Count > 0) DisabledBuiltIn[key] = list;
        }

        /// A lease ended without a spawn: its holder left while the building was still empty. Everything the holder
        /// would have shared is gone, so the building is spawnable again: hand the disabled built-ins back, forget that
        /// this machine asked, and clear the poi's spawn flags so the game calls SpawnStuff once someone is near.
        private static void OnLeaseFreed(string key)
        {
            Plugin.Log.LogInfo($"LeaseFreed '{key}': had asked {LeaseAsked.Contains(key)}, clearing");
            LeaseAsked.Remove(key);
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
                   ",\"spawns\":" + LeaseSpawns + ",\"done\":" + LeasesDone + ",\"freed\":" + LeasesFreed + "}";
        }

        public static void LeaseArm(string key) => LeaseAsked.Add(key);

        /// The lease table outlives the server: without it, a client arriving at a building that a PREVIOUS server
        /// session populated would be granted a fresh lease and spawn the contents again — the save would grow one
        /// duplicate set per visited building per restart. Saved next to the world save (`.tlds.leases`), loaded when
        /// hosting starts; every holder becomes 0 (after a restart the server itself is the keeper of everything).
        internal static void SaveLeases()
        {
            try
            {
                if (!InSession && Leases.Count == 0) return;
                var name = mainscript.GetLastSaveName();
                if (string.IsNullOrEmpty(name)) return;
                var path = pathscript.SaveDataName(name) + ".leases";
                var lines = new List<string>();
                foreach (var kv in Leases)
                    lines.Add(kv.Key + "\t0\t" + (_leasesDone.Contains(kv.Key) ? "1" : "0"));
                System.IO.File.WriteAllLines(path, lines);
            }
            catch (Exception e) { Plugin.Log.LogWarning("lease save: " + e.Message); }
        }

        internal static void LoadLeases()
        {
            try
            {
                var name = mainscript.GetLastSaveName();
                if (string.IsNullOrEmpty(name)) return;
                var path = pathscript.SaveDataName(name) + ".leases";
                if (!System.IO.File.Exists(path)) return;
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 2 || string.IsNullOrEmpty(parts[0])) continue;
                    if (Leases.ContainsKey(parts[0])) continue;   // the host's own pois are already marked
                    Leases[parts[0]] = 0;
                    if (parts.Length > 2 && parts[2] == "1") _leasesDone.Add(parts[0]);
                }
                Plugin.Log.LogInfo($"Leases loaded from the save: {Leases.Count}");
            }
            catch (Exception e) { Plugin.Log.LogWarning("lease load: " + e.Message); }
        }

        [HarmonyLib.HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.Save))]
        private static class SaveLeasesWithWorld
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix() => SaveLeases();   // covers AutoSave, `mp save` and menu saves alike
        }

        public static void ResetCounters()
        {
            LeasesAsked = LeasesGranted = LeaseSpawns = LeaseItemsShared = HostItemsShared = BuiltInShared = BuiltInDisabled = BuiltInReEnabled = LeasesDone = LeasesFreed = 0;
            StatesSent = StatesIn = StatesStale = Spawned = OwnerChanges = 0;
            PartsReported = PartsApplied = PartOffRejected = ClaimsRefused = ClaimsSent = 0;
            ResyncsSent = ResyncsApplied = ResyncItemsLoaded = ResyncSkipped = PartStatesChanged = ResyncBytes = ResyncItemsSent = ResyncHeld = 0;
            EditsSent = EditsApplied = EditsRejected = EditsMarked = 0;
            ResyncCaptureMsMax = ResyncCaptureMsTotal = 0;
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
            if (a == null || !a.attached) return false;
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
                            if (poi.spawnedItems || poi.spawnedFixed) Leases[Key(poi)] = 0;
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
            ByNet.Clear(); PendingShare.Clear(); ProxyItems.Clear();
            ProxyWheelOwner.Clear(); ProxyWheelIndex.Clear(); ProxyEngineOwner.Clear();
            LeaseAsked.Clear(); PendingGrants.Clear(); _captureKnown = null; Captures.Clear();
            Mp.SceneTearingDown = false;
            DisabledBuiltIn.Clear();
        }

        /// Per frame: grants waiting for their building object, capture windows.
        private static void LeaseTick()
        {
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
                foreach (var e in ByNet.Values)
                    if (e.Proxy && e.Root != null && (e.Root.gameObject == __instance.gameObject || e.Root.transform == __instance.transform.root))
                    {
                        // a whole CAR must never flip via the pickup interaction (the E-press on a car can be the
                        // enter/flip interaction): cars change hands by driving only — otherwise a passenger entering
                        // claims the car and both machines end up simulating it
                        if (e.Root.car != null) return;
                        SetProxy(e, false);
                        e.OwnerId = MyId;
                        W.Reset(); W.U8(Claim); W.U32(e.NetId); ToServer(W, true);
                        ClaimsSent++;
                        return;
                    }
            }
        }
    }
}
