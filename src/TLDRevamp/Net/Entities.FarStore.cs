using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// A shared object far from this machine's player lives only as records in the game's far store (the save's
    /// itemData + chunk index; itemPlaceRemoveScript places it when someone comes near). Those records ARE what this
    /// machine saves and what it shows on arrival — so every change has to reach them, not only changes to objects
    /// that happen to be loaded here.
    ///
    /// Before v0.57.93 (tools/farworld.py, players 200 km apart over the relay): the host's records of a building the
    /// far player opened had all 69 part conditions at 0 (the far player's: 18 worn or broken), the far player's car
    /// 0.5 L of fuel instead of 3.7 — resyncs for stored copies were dropped, and an object arriving far away was
    /// spawned as a GameObject 200 km from the player and streamed straight back out by the game. Both now go to the
    /// records directly.
    public static partial class Entities
    {
        public static long FarAdds, FarResyncs, FarResyncItems, FarShared, InterestSent;

        // ------------------------------------------------------------------ server: the whole world, by distance

        /// Objects reach a player when they come within InterestM of them (and stay known after: far away they sit in
        /// that player's far store, kept current by states and resyncs). Beyond the state range (MpServer.CarFarM)
        /// so a car driving into range is already there. Before v0.57.93 every object went to every player at join —
        /// and only what the host had LOADED was shared at all: after a restart, everything in its far store (every
        /// building a far player opened) never reached anyone again.
        public static double InterestM = 3000;
        public static int InterestPerTick = 150;   // objects offered per player per tick (spreads a big area over ticks)
        private static readonly Dictionary<int, HashSet<uint>> Known = new Dictionary<int, HashSet<uint>>();
        private static readonly List<KeyValuePair<int, Vector3d>> _peerPos = new List<KeyValuePair<int, Vector3d>>();
        private static float _interestAcc;

        private static HashSet<uint> KnownBy(int playerId)
        {
            if (!Known.TryGetValue(playerId, out var k)) Known[playerId] = k = new HashSet<uint>();
            return k;
        }

        private static Vector3d RecordRootPos(byte[] rec)
        {
            var d = RecordCodec.Decode(rec);
            return d != null && d.items.Count > 0 ? d.items[0].transform.pos : default;
        }

        /// One object to one player: the object, its parts that came off, its latest item states.
        private static void SendObject(int playerId, SEnt se)
        {
            WriteAdd(W, se); ServerSendTo(playerId, W, true);
            foreach (var pn in se.Parts) if (Server.TryGetValue(pn, out var part)) { WritePartDetached(W, part); ServerSendTo(playerId, W, true); }
            if (se.ItemState.Count > 0) { WriteResyncAll(WS, se); ServerSendTo(playerId, WS, true); }
            KnownBy(playerId).Add(se.NetId);
            if (playerId != 0) ReplayAttaches(playerId, se);   // the host's world has them already
            InterestSent++;
        }

        /// A new object: the host always gets it (its world is the save), players within InterestM at once.
        private static void Offer(SEnt se, int from)
        {
            if (from != 0) SendObject(0, se);
            if (Mp.Server == null) return;
            Mp.Server.ReadyPeers(_peerPos);
            foreach (var kv in _peerPos)
            {
                if (kv.Key == from) continue;
                double dx = kv.Value.x - se.Where.x, dz = kv.Value.z - se.Where.z;
                if (dx * dx + dz * dz < InterestM * InterestM) SendObject(kv.Key, se);
            }
        }

        /// Twice a second: objects that came within InterestM of a player (they moved, or the player did).
        private static void ServerInterestTick(float dt)
        {
            if (Mp.Server == null) return;
            _interestAcc += dt;
            if (_interestAcc < 0.5f) return;
            _interestAcc = 0f;
            Mp.Server.ReadyPeers(_peerPos);
            foreach (var kv in _peerPos)
            {
                var known = KnownBy(kv.Key);
                int n = 0;
                foreach (var se in Server.Values)
                {
                    if (se.PartIndex >= 0 || known.Contains(se.NetId)) continue;   // parts go with their car
                    double dx = kv.Value.x - se.Where.x, dz = kv.Value.z - se.Where.z;
                    if (dx * dx + dz * dz >= InterestM * InterestM) continue;
                    SendObject(kv.Key, se);
                    if (++n >= InterestPerTick) break;
                }
            }
        }

        /// Hosting starts: everything in the host's far store becomes a shared object too (owned by the server), not
        /// only what is loaded around the host. Records are grouped the way the game stores them: a car with its
        /// parts (attached to an item) is one object; things attached to a building or the world are their own.
        public static int ShareFarStore()
        {
            var data = savedatascript.s != null ? savedatascript.s.data : null;
            if (data == null || data.itemData == null) return 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fd = data.itemData;
            var recs = new Dictionary<uint, save_item>();
            foreach (var it in fd.items) if (it != null) recs[it.id] = it;
            var covered = new HashSet<uint>();
            foreach (var e in ByNet.Values) if (e.ItemIds != null) foreach (var id in e.ItemIds) covered.Add(id);
            var parentOf = new Dictionary<uint, uint>();
            foreach (var a in fd.attachable)
                if (a != null && a.attachType == (int)save_attachable.attachTypeEmu.toItem && a.parentid != a.id) parentOf[a.id] = a.parentid;
            // entries by id, per list (one pass over the store)
            var byField = new List<KeyValuePair<FieldInfo, Dictionary<uint, List<object>>>>();
            foreach (var f in ItemSnapshot.ListFields)
            {
                if (!(f.GetValue(fd) is IList list)) continue;
                var ix = new Dictionary<uint, List<object>>();
                FieldInfo idf = null;
                foreach (var o in list)
                {
                    if (o == null) continue;
                    if (idf == null) idf = _idField(o);
                    if (idf == null) break;
                    uint id = (uint)idf.GetValue(o);
                    if (!ix.TryGetValue(id, out var l)) ix[id] = l = new List<object>();
                    l.Add(o);
                }
                byField.Add(new KeyValuePair<FieldInfo, Dictionary<uint, List<object>>>(f, ix));
            }
            // groups: records linked by an item attachment (part → what it's on) or a container's lock (box → what's in
            // it) go together — their records reference each other by id, and only ids inside one group are remapped on
            // arrival. Before v0.58.0 container contents went as their own groups: the box kept the host's ids of its
            // contents, and on the far player's machine those ids were other items — the box pulled them in (farworld
            // runs 13/15: a car's wheels, trunk and coolant tank inside the building's boxes, still "on" the car).
            var uf = new Dictionary<uint, uint>();
            uint Find(uint x) { while (uf.TryGetValue(x, out var p) && p != x) { uf.TryGetValue(p, out var gp); uf[x] = gp; x = p; } return x; }
            void Union(uint a, uint b) { a = Find(a); b = Find(b); if (a != b) uf[b] = a; }
            var skip = new HashSet<uint>();      // attached to an item loaded here: goes with it
            var under = new HashSet<uint>();     // attached to / locked in another record: not a group's root
            foreach (var it in fd.items) if (it != null && !covered.Contains(it.id)) uf[it.id] = it.id;
            foreach (var kv in parentOf)
            {
                if (!uf.ContainsKey(kv.Key)) continue;
                if (uf.ContainsKey(kv.Value)) { Union(kv.Value, kv.Key); under.Add(kv.Key); }
                else if (savedatascript.s.items.ContainsKey(kv.Value)) skip.Add(kv.Key);
            }
            foreach (var pl in fd.physLocks)
            {
                if (pl == null || pl.IDs == null || !uf.ContainsKey(pl.id)) continue;
                foreach (var cid in pl.IDs) if (cid != pl.id && uf.ContainsKey(cid)) { Union(pl.id, cid); under.Add(cid); }
            }
            var members = new Dictionary<uint, List<uint>>();
            foreach (var it in fd.items)
            {
                if (it == null || !uf.ContainsKey(it.id)) continue;
                uint r = Find(it.id);
                if (!members.TryGetValue(r, out var l)) members[r] = l = new List<uint>();
                l.Add(it.id);
            }
            var groups = new Dictionary<uint, List<uint>>();
            foreach (var l in members.Values)
            {
                bool drop = false; uint root = 0;
                foreach (var id in l) { if (skip.Contains(id)) drop = true; if (root == 0 && !under.Contains(id)) root = id; }
                if (drop) continue;
                if (root == 0) root = l[0];
                var g = new List<uint> { root };
                foreach (var id in l) if (id != root) g.Add(id);
                groups[root] = g;
            }
            int n = 0;
            foreach (var kv in groups)
            {
                var d = new itemDataClass();
                foreach (var fx in byField)
                {
                    if (!(fx.Key.GetValue(d) is IList dst)) continue;
                    foreach (var id in kv.Value) if (fx.Value.TryGetValue(id, out var l)) foreach (var o in l) dst.Add(o);
                }
                if (FarGroupLog && (kv.Value.Count > 1 || parentOf.ContainsKey(kv.Key)))
                {
                    var names = new List<string>();
                    foreach (var id in kv.Value) names.Add(PrefabName(recs[id].prefabID));
                    Plugin.Log.LogInfo($"far group: root {PrefabName(recs[kv.Key].prefabID)} #{kv.Key} at {recs[kv.Key].transform.pos.x:F0},{recs[kv.Key].transform.pos.z:F0} " +
                                       $"{(parentOf.TryGetValue(kv.Key, out var rp) ? "parent #" + rp + (recs.ContainsKey(rp) ? " (stored)" : savedatascript.s.items.ContainsKey(rp) ? " (loaded)" : " (gone)") : "no parent")}, {kv.Value.Count} items: {string.Join(" ", names)}");
                }
                var se = new SEnt { NetId = _nextNet++, OwnerId = 0, Epoch = 1, Record = RecordCodec.Encode(d), Where = recs[kv.Key].transform.pos };
                Server[se.NetId] = se;
                var e = new Ent { NetId = se.NetId, OwnerId = 0, Epoch = 1, RootId = kv.Key, ItemIds = new List<uint>(kv.Value) };
                foreach (var id in kv.Value) e.Items.Add(null);
                ByNet[se.NetId] = e;
                KnownBy(0).Add(se.NetId);
                n++;
            }
            FarShared += n;
            Plugin.Log.LogInfo($"Far store shared at hosting start: {n} objects from {fd.items.Count} records in {sw.ElapsedMilliseconds} ms");
            return n;
        }

        public static bool FarGroupLog = true;   // diagnostics (farworld B): each multi-item / attached group shared at hosting start

        internal static string PrefabName(int prefabID)
        {
            var go = itemdatabase.s != null ? itemdatabase.s.Item(prefabID) : null;
            return go != null ? go.name : "prefab" + prefabID;
        }

        /// Lists a resync may replace in the far store (Entities.StateOnly's lists). `attachable` stays out: the
        /// resync carries it stripped of the world attach point (gpos), and the stored one must keep it.
        private static readonly HashSet<string> FarStateLists = new HashSet<string>
            { "partconditions", "tanks", "usable", "colors", "door_rots", "car", "ammo", "ginlamp", "busdoorscript", "painting", "food", "rendszam" };

        private static FieldInfo _idField(object o) => o.GetType().GetField("id", BindingFlags.Public | BindingFlags.Instance);

        /// Is this global point beyond the game's item range of every generation centre here (where the game would
        /// stream an item out)? Then a received object goes to the far store, not into the scene.
        internal static bool FarFromHere(Vector3d g)
        {
            var ips = itemPlaceRemoveScript.s;
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (ips == null || map == null || map.genArounds == null || map.genArounds.Count == 0) return false;
            double lim = ips.itemRemoveDist;
            foreach (var ga in map.genArounds)
            {
                var c = mainscript.GlobalFromUnityPos(ga.upos);
                double dx = c.x - g.x, dz = c.z - g.z;
                if (dx * dx + dz * dz < lim * lim) return false;
            }
            return true;
        }

        /// An object that arrives far from here: its records into the far store under fresh local ids, a dormant entity
        /// for it. The game places it when someone comes near (Resolve binds it then).
        private static Ent AddFar(uint net, int owner, uint epoch, itemDataClass d, bool hasState, Vector3d pos, Quaternion rot)
        {
            var data = savedatascript.s.data;
            uint rootOld = d.items[0].id;
            var map = new Dictionary<uint, uint>();
            foreach (var it in d.items) map[it.id] = savedatascript.s.GetNewKey();
            ItemSnapshot.DropForeignRefs(d, map, "far add net " + net);
            ItemSnapshot.Remap(d, map);
            data.itemData.Append(d);
            foreach (var it in d.items) savedatascript.s.AddToChunk(it.transform.pos, it.id);
            var e = new Ent { NetId = net, OwnerId = owner, Epoch = epoch, RootId = map[rootOld], ItemIds = new List<uint>() };
            foreach (var it in d.items) { e.ItemIds.Add(it.id); e.Items.Add(null); }
            if (owner != MyId) { e.Proxy = true; e.Ip = new PoseInterpolator(); }
            ByNet[net] = e;
            if (hasState) { e.FarPos = pos; e.FarRot = rot; FlushFarRecords(e); }
            FarAdds++;
            if (FarGroupLog && d.items.Count > 1)
            {
                var names = new List<string>();
                foreach (var it in d.items) names.Add(PrefabName(it.prefabID));
                int att = 0; foreach (var a in d.attachable) if (a != null) att++;
                Plugin.Log.LogInfo($"far add: net {net} root {names[0]} {d.items.Count} items, {att} attach records: {string.Join(" ", names)}");
            }
            return e;
        }

        /// A resync for an object stored here: its state lists replace the stored ones, item by item.
        private static void FarResync(Ent e, int idx, itemDataClass d)
        {
            var data = savedatascript.s.data.itemData;
            uint id = idx >= 0 && idx < e.ItemIds.Count ? e.ItemIds[idx] : 0;
            if (id == 0 || !savedatascript.IndexOfID(data.items, id, out _)) return;
            foreach (var f in ItemSnapshot.ListFields)
            {
                // the resync carries all of the item's entries in these lists: none = none (an emptied tank isn't saved)
                if (!FarStateLists.Contains(f.Name) || !(f.GetValue(d) is IList src)) continue;
                if (!(f.GetValue(data) is IList dst)) continue;
                for (int i = dst.Count - 1; i >= 0; i--)
                {
                    var o = dst[i]; if (o == null) continue;
                    var idf = _idField(o);
                    if (idf != null && (uint)idf.GetValue(o) == id) dst.RemoveAt(i);
                }
                foreach (var o in src) if (o != null) dst.Add(o);
            }
            FarResyncItems++;
        }
    }
}
